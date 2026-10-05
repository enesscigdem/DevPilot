using System.Text.Json;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

/// <summary>
/// Builds the human-readable verdict (what happened, why, what next) and the AI usage summary for an execution
/// deterministically from its recorded activities. Pure functions: no I/O, no AI.
/// </summary>
public static class ExecutionVerdictBuilder
{
    public const int SnapshotSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ExecutionVerificationSnapshot BuildSnapshot(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities,
        ExecutionVerificationOutcome outcome,
        AiPricingOptions? pricing = null) =>
        new(
            SnapshotSchemaVersion,
            DateTime.UtcNow,
            BuildVerdict(execution, activities, outcome),
            AggregateUsage(activities, pricing));

    /// <summary>
    /// Returns the persisted terminal snapshot when it still agrees with the live outcome, otherwise builds it live
    /// (running / older executions). Gating decisions always use the live outcome; this is explanation only.
    /// </summary>
    public static ExecutionVerificationSnapshot Resolve(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities,
        ExecutionVerificationOutcome liveOutcome,
        AiPricingOptions? pricing = null)
    {
        var persisted = TryDeserialize(execution.VerificationSnapshotJson);
        if (persisted != null &&
            string.Equals(persisted.Verdict.Outcome, liveOutcome.ToString(), StringComparison.Ordinal))
        {
            return persisted;
        }

        return BuildSnapshot(execution, activities, liveOutcome, pricing);
    }

    public static string Serialize(ExecutionVerificationSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, JsonOptions);

    public static ExecutionVerificationSnapshot? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<ExecutionVerificationSnapshot>(json, JsonOptions);
            return snapshot is { Verdict: not null, Usage: not null } ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ExecutionVerdictDto BuildVerdict(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities,
        ExecutionVerificationOutcome outcome)
    {
        var parsed = Parse(activities);
        var findings = new List<VerdictFindingDto>();

        var baselineUnverified = parsed.Any(p => p.Metadata?.BaselineUnverified == true);
        var flakeConfirmed = parsed.Any(p => p.Activity.Message.StartsWith("Flaky failure detected", StringComparison.OrdinalIgnoreCase));
        var weakening = parsed.Where(p => p.Metadata?.TestWeakeningSuspected == true).ToList();
        var freshness = parsed.LastOrDefault(p => p.Metadata?.EventKind == "BaseFreshness").Metadata;
        var staleBase = freshness?.BaseFreshness is "Behind" or "Diverged";

        var compileRounds = CountRepairRounds(parsed, "Compile", "FixingBuildIssue");
        var testRounds = CountRepairRounds(parsed, "Test", "FixingFailingTest");
        var summary = parsed.LastOrDefault(p => p.Metadata?.EventKind == "GenerationSummary").Metadata;
        var compactRetries = summary?.CompactRetryCount ?? 0;
        var applicabilityRepairs = summary?.ApplicabilityRepairCount ?? 0;

        // ── Findings ─────────────────────────────────────────────────────────
        if (staleBase)
        {
            findings.Add(new("StaleBase", "warning",
                $"The base was {freshness!.BaseBehindCount ?? 0} commit(s) behind origin when this ran; the change may need a rebase before merging."));
        }
        else if (freshness?.BaseFreshness == "FastForwarded")
        {
            findings.Add(new("Base", "info", "The base was fast-forwarded to match origin before this run."));
        }
        else if (freshness?.BaseFreshness == "FetchFailed")
        {
            findings.Add(new("Base", "info", "Origin could not be reached, so base freshness is unknown."));
        }

        if (applicabilityRepairs > 0 || compactRetries > 0)
        {
            findings.Add(new("Generation", "info",
                $"Generation needed {applicabilityRepairs} applicability repair(s) and {compactRetries} compact retr{(compactRetries == 1 ? "y" : "ies")}."));
        }

        if (compileRounds > 0)
        {
            findings.Add(new("CompileRepair", "info", $"Compiler repair used {compileRounds} round(s)."));
        }

        if (testRounds > 0)
        {
            findings.Add(new("TestRepair", "info", $"Test repair used {testRounds} round(s)."));
        }

        if (flakeConfirmed)
        {
            findings.Add(new("Flake", "info", "A failing test passed on a confirmation rerun and was treated as flaky; no repair was needed."));
        }

        if (baselineUnverified)
        {
            findings.Add(new("BaselineUnverified", "warning",
                "The baseline comparison was inconclusive, so a failure was repaired from raw diagnostics only. It could not be proven new or pre-existing."));
        }

        foreach (var item in weakening)
        {
            findings.Add(new("TestWeakening", "danger", item.Activity.Message));
        }

        var preExisting = parsed.Select(p => p.Metadata?.PreExistingFailureCount).LastOrDefault(c => c.HasValue) ?? 0;
        if (outcome == ExecutionVerificationOutcome.NoNewRegressions && preExisting > 0)
        {
            findings.Add(new("PreExisting", "info", $"{preExisting} pre-existing failure(s) on the base commit remain unchanged."));
        }

        var checksNotRun = ComputeChecksNotRun(parsed, outcome);
        if (checksNotRun.Count > 0)
        {
            findings.Add(new("ChecksNotRun", "warning", $"Discovered but not run: {string.Join(", ", checksNotRun)}."));
        }

        var stop = FindFailureExplanation(parsed);
        if (stop != null)
        {
            findings.Insert(0, new("FailureReason", "danger", stop));
        }

        var failedTests = parsed.LastOrDefault(p =>
            p.Activity.Stage == ExecutionStage.Test &&
            p.Activity.Status == ExecutionActivityStatus.Failed &&
            p.Metadata?.FailingTestCount is > 0).Metadata;
        var failingTestCount = outcome == ExecutionVerificationOutcome.NeedsReview ? failedTests?.FailingTestCount ?? 0 : 0;
        var buildPassed = parsed.Any(p => p.Activity.Stage == ExecutionStage.Build &&
                                          p.Activity.Status == ExecutionActivityStatus.Completed &&
                                          p.Activity.Message.StartsWith("Build passed", StringComparison.OrdinalIgnoreCase));

        var (severity, headline, action) = Describe(execution, outcome, baselineUnverified, weakening.Count > 0, preExisting, stop);
        if (failingTestCount > 0 && weakening.Count == 0)
        {
            var passedNote = buildPassed ? "The build passed, but " : string.Empty;
            var count = failingTestCount == 1 ? "1 test still fails" : $"{failingTestCount} tests still fail";
            headline = $"Needs review: {passedNote}{count} after {testRounds} automatic repair round(s).";
            action = "Use \"Fix failing tests\" to give the AI another attempt with the full list of failures, or describe the fix yourself with Request changes.";
        }

        return new ExecutionVerdictDto(
            Outcome: outcome.ToString(),
            Severity: severity,
            Headline: headline,
            RecommendedAction: action,
            Findings: findings,
            BaselineUnverified: baselineUnverified,
            FlakeConfirmed: flakeConfirmed,
            TestWeakeningSuspected: weakening.Count > 0,
            StaleBase: staleBase,
            CompileRepairRounds: compileRounds,
            TestRepairRounds: testRounds,
            CompactRetries: compactRetries,
            ApplicabilityRepairs: applicabilityRepairs,
            ChecksNotRun: checksNotRun,
            BaseFreshness: freshness?.BaseFreshness,
            BaseBehindCount: freshness?.BaseBehindCount,
            BaseCommitSha: freshness?.BaseCommitSha,
            FailingTestCount: failingTestCount,
            FailingTestGroups: failingTestCount > 0 ? failedTests?.FailingTestGroups : null,
            SuggestedFix: failingTestCount > 0 ? failedTests?.SuggestedFix : null);
    }

    public static ExecutionUsageDto AggregateUsage(IReadOnlyList<ExecutionActivity> activities, AiPricingOptions? pricing = null)
    {
        var parsed = Parse(activities);

        var calls = parsed.Where(p => p.Metadata?.EventKind == "ProviderCall").ToList();
        long input = 0, output = 0, providerMs = 0;
        var withoutTokens = 0;
        decimal costSum = 0;
        var anyCallUnpriced = false;
        foreach (var call in calls)
        {
            var meta = call.Metadata!;
            if (meta.InputTokens.HasValue || meta.OutputTokens.HasValue)
            {
                var callInput = meta.InputTokens ?? 0;
                var callOutput = meta.OutputTokens ?? 0;
                input += callInput;
                output += callOutput;

                // Each call is priced by the model that served it, so mixing models stays accurate.
                if (pricing is not null && pricing.TryGetPrice(meta.Model, out var inputPrice, out var outputPrice))
                {
                    costSum += (callInput * inputPrice + callOutput * outputPrice) / 1_000_000m;
                }
                else
                {
                    anyCallUnpriced = true;
                }
            }
            else
            {
                withoutTokens++;
            }

            providerMs += meta.StageDurationMs ?? 0;
        }

        // One call without a known price would make the total an under-estimate, so report no cost
        // rather than a misleading partial one.
        decimal? cost = pricing is { IsConfigured: true } && !anyCallUnpriced
            ? Math.Round(costSum, 4)
            : null;

        var summary = parsed.LastOrDefault(p => p.Metadata?.EventKind == "GenerationSummary").Metadata;
        var timings = new List<ExecutionStageTimingDto>();
        AddTiming(timings, "Generation", summary?.TotalGenerationTimeMs);
        AddTiming(timings, "Build", SumCheckDurations(parsed, ExecutionStage.Build));
        AddTiming(timings, "Test", SumCheckDurations(parsed, ExecutionStage.Test));
        // An agentic repair that gave up closes with another event kind, so it is recognised by its marker instead.
        AddTiming(timings, "Repair", parsed
            .Where(p => (p.Metadata?.EventKind is "FixingBuildIssue" or "FixingFailingTest" ||
                         p.Metadata?.ProgressResult == "AgenticRepair") &&
                        p.Activity.Status is ExecutionActivityStatus.Completed or ExecutionActivityStatus.Failed &&
                        p.Metadata!.StageDurationMs.HasValue)
            .Sum(p => p.Metadata!.StageDurationMs!.Value));
        // Verification work between a failed test and the repair: stable-failure confirmation and the base-commit comparison.
        foreach (var label in new[] { "Confirmation", "Baseline" })
        {
            AddTiming(timings, label, parsed
                .Where(p => p.Metadata?.EventKind == "VerificationOverhead" &&
                            p.Metadata.ProgressResult == label &&
                            p.Metadata.StageDurationMs.HasValue)
                .Sum(p => p.Metadata!.StageDurationMs!.Value));
        }

        return new ExecutionUsageDto(
            ProviderCalls: calls.Count,
            FailedProviderCalls: calls.Count(c => c.Activity.Status == ExecutionActivityStatus.Failed),
            CallsWithoutTokenData: withoutTokens,
            InputTokens: input,
            OutputTokens: output,
            TotalTokens: input + output,
            ProviderTimeMs: providerMs,
            EstimatedCostUsd: cost,
            StageTimings: timings);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static void AddTiming(List<ExecutionStageTimingDto> timings, string stage, long? durationMs)
    {
        if (durationMs is > 0)
        {
            timings.Add(new ExecutionStageTimingDto(stage, durationMs.Value));
        }
    }

    /// <summary>
    /// Sums the duration of check runs for a stage. When a check was repaired and re-run, the final non-repair
    /// "passed" activity repeats the first run's duration, so it is excluded to avoid double counting.
    /// </summary>
    private static long SumCheckDurations(IReadOnlyList<ParsedActivity> parsed, ExecutionStage stage)
    {
        long total = 0;
        var groups = parsed
            .Where(p => p.Activity.Stage == stage &&
                        p.Metadata?.EventKind is "VerifyingRepository" or "ReadyForReview" &&
                        p.Activity.Status is ExecutionActivityStatus.Completed or ExecutionActivityStatus.Failed &&
                        p.Metadata.StageDurationMs.HasValue &&
                        !string.IsNullOrWhiteSpace(p.Metadata.RepositoryCheckId))
            .GroupBy(p => p.Metadata!.RepositoryCheckId!);

        foreach (var group in groups)
        {
            var items = group.ToList();
            if (items.Any(i => i.Metadata!.RepairKind != null))
            {
                items = items.Where(i => i.Metadata!.RepairKind != null).ToList();
            }

            total += items.Sum(i => i.Metadata!.StageDurationMs!.Value);
        }

        return total;
    }

    private static int CountRepairRounds(IReadOnlyList<ParsedActivity> parsed, string repairKind, string eventKind) =>
        parsed
            .Where(p => p.Metadata?.EventKind == eventKind &&
                        p.Metadata.RepairKind == repairKind &&
                        p.Activity.Status == ExecutionActivityStatus.Started &&
                        p.Metadata.RepairRound.HasValue)
            .Select(p => (p.Metadata!.RepositoryCheckId, p.Metadata.RepairRound))
            .Distinct()
            .Count();

    private static IReadOnlyList<string> ComputeChecksNotRun(IReadOnlyList<ParsedActivity> parsed, ExecutionVerificationOutcome outcome)
    {
        var discovered = parsed
            .LastOrDefault(p => p.Metadata?.EventKind == "RepositoryPreflight").Metadata?.DiscoveredChecks;
        if (discovered == null || discovered.Count == 0)
        {
            return Array.Empty<string>();
        }

        var executed = parsed
            .Where(p => p.Activity.Stage is ExecutionStage.Build or ExecutionStage.Test &&
                        !string.IsNullOrWhiteSpace(p.Metadata?.RepositoryCheckId))
            .Select(p => p.Metadata!.RepositoryCheckId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only meaningful once the run reached verification at all.
        if (outcome is ExecutionVerificationOutcome.Failed or ExecutionVerificationOutcome.Blocked)
        {
            return Array.Empty<string>();
        }

        return discovered.Where(id => !executed.Contains(id)).ToList();
    }

    private static string? FindFailureExplanation(IReadOnlyList<ParsedActivity> parsed)
    {
        var stop = parsed.LastOrDefault(p =>
            p.Activity.Status == ExecutionActivityStatus.Failed &&
            p.Activity.Stage is ExecutionStage.Build or ExecutionStage.Test &&
            (p.Metadata?.EventKind == "StoppedWithEvidence" || p.Metadata?.VerificationOutcome == "NeedsReview"));

        if (stop.Activity == null)
        {
            return null;
        }

        var where = stop.Activity.Stage == ExecutionStage.Build ? "build" : "tests";
        return stop.Metadata?.ProgressResult switch
        {
            "SameFailure" => $"The {where} failure persisted after the focused repair, so repair stopped with no diagnostic progress.",
            "Uncorrelated" => $"The {where} failure could not be tied to a file this task changed, so no safe repair target existed.",
            "NoDiff" => $"The focused repair of the {where} failure produced no change to the working tree.",
            "NewBuildFailure" => "A test repair broke the build.",
            _ => stop.Activity.Message,
        };
    }

    private static (string Severity, string Headline, string? Action) Describe(
        TaskExecution execution,
        ExecutionVerificationOutcome outcome,
        bool baselineUnverified,
        bool weakening,
        int preExisting,
        string? failureReason) =>
        outcome switch
        {
            ExecutionVerificationOutcome.Verified =>
                ("success", "Verified: build and tests passed on the final change.", "Review the diff and approve."),
            ExecutionVerificationOutcome.NoNewRegressions =>
                ("warning", $"No new regressions: {preExisting} pre-existing failure(s) on the base commit remain, none were introduced.",
                    "Review the diff and approve; the pre-existing failures are not caused by this change."),
            ExecutionVerificationOutcome.PartiallyVerified when baselineUnverified =>
                ("warning", "Partially verified: the baseline comparison was inconclusive.",
                    "Run the repository checks on the base commit locally to confirm the failure was not pre-existing, then review the diff."),
            ExecutionVerificationOutcome.PartiallyVerified =>
                ("warning", "Partially verified: the build passed but verification was incomplete (no tests discovered or unresolved checks).",
                    "Review the diff carefully; add or run tests for the changed behavior."),
            ExecutionVerificationOutcome.VerificationUnavailable =>
                ("warning", "Not verified: no trustworthy build or test checks were discovered for this repository.",
                    "Review the diff manually; rely on CI after pushing."),
            ExecutionVerificationOutcome.VerificationInfrastructureError =>
                ("warning", "Not verified: verification could not run because of an infrastructure error.",
                    "Fix the environment (toolchain, Docker, network) and retry, or rely on CI; merge stays blocked locally."),
            ExecutionVerificationOutcome.NeedsReview when weakening =>
                ("danger", "Needs review: an automated test repair may have weakened existing tests.",
                    "Inspect the test diff for removed assertions, skips or deleted tests before approving, or retry."),
            ExecutionVerificationOutcome.NeedsReview =>
                ("danger", failureReason != null
                    ? "Needs review: verification could not be completed automatically."
                    : "Needs review: verification ended with an unresolved failure.",
                    "Inspect the failing evidence below; retry the execution or fix the failing files manually."),
            ExecutionVerificationOutcome.Failed =>
                ("danger",
                    string.IsNullOrWhiteSpace(execution.ErrorMessage)
                        ? "Failed before verification."
                        : $"Failed before verification: {Truncate(execution.ErrorMessage, 200)}",
                    "Retry the execution. If it keeps failing, re-run impact analysis or narrow the plan."),
            ExecutionVerificationOutcome.Blocked =>
                ("neutral", "The execution was cancelled.", "Start a new execution when ready."),
            _ => ("neutral", outcome.ToString(), null),
        };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "…";

    private readonly record struct ParsedActivity(ExecutionActivity Activity, ExecutionActivityMetadata? Metadata);

    private static IReadOnlyList<ParsedActivity> Parse(IReadOnlyList<ExecutionActivity> activities) =>
        activities
            .Select(a => new ParsedActivity(a, ParseMetadata(a.MetadataJson)))
            .ToList();

    private static ExecutionActivityMetadata? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ExecutionActivityMetadata>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
