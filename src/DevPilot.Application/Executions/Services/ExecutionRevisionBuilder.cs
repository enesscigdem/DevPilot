using System.Text.Json;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

/// <summary>Builds the "latest requested fix" view from the activity recorded since it was requested.</summary>
public static class ExecutionRevisionBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record Event(ExecutionActivity Activity, ExecutionActivityMetadata? Meta);

    public static ExecutionRevisionDto? Build(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> allActivities,
        ExecutionVerificationOutcome overallOutcome,
        ExecutionGitDiffResult? snapshotDiff)
    {
        if (!ExecutionRevisionScope.HasRevision(execution))
        {
            return null;
        }

        var requestedAt = execution.LastChangeRequestAt!.Value;
        var scoped = ExecutionRevisionScope.Since(execution, allActivities);
        var events = scoped.Select(a => new Event(a, ParseMetadata(a.MetadataJson))).ToList();

        var active = ExecutionRevisionScope.IsActive(execution);
        var result = execution.LastChangeRequestResult;
        var state = active
            ? "Running"
            : execution.Status == TaskExecutionStatus.Cancelled
                ? "Cancelled"
                : result is null
                    ? "Failed"
                    : result.StartsWith("Applied", StringComparison.OrdinalIgnoreCase)
                        ? "Applied"
                        : result.StartsWith("No change", StringComparison.OrdinalIgnoreCase)
                            ? "NoChange"
                            : "Failed";

        var applyStarted = events.Any(e => e.Meta?.EventKind == "ApplyingReviewFeedback");
        var applied = events.Any(e => e.Meta?.EventKind == "ReviewFeedbackApplied");
        var noChange = events.Any(e => e.Meta?.EventKind == "ReviewFeedbackNoChange");
        var failedEvent = events.LastOrDefault(e => e.Meta?.EventKind == "ReviewFeedbackFailed");

        // Build and test of THIS round only: the scoped activities, never the first run's.
        var (build, test) = ExecutionReviewStageClassifier.Classify(execution, scoped);
        var ranChecks = scoped.Any(a => a.Stage is ExecutionStage.Build or ExecutionStage.Test);
        var scopedOutcome = ranChecks
            ? ExecutionVerificationEvaluator.DetermineOutcome(execution, scoped).ToString()
            : null;

        var steps = BuildSteps(events, active, applyStarted, applied, noChange, failedEvent != null, build, test, state);
        var phase = active
            ? steps.FirstOrDefault(s => s.State == "active")?.Key ?? "prepare"
            : "finished";

        var summary = events.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Meta?.ChangeSummary))?.Meta?.ChangeSummary;
        var unresolved = events.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Meta?.UnresolvedNote))?.Meta?.UnresolvedNote;

        var files = BuildFiles(events, snapshotDiff, active);
        var filesAreFinal = !active && snapshotDiff is { Success: true };
        var changed = files.Where(f => f.State is not ("Considered" or "Unchanged")).ToList();

        DateTime? completedAt = active ? null : execution.CompletedAt;
        long? durationMs = completedAt.HasValue
            ? (long)Math.Max(0, (completedAt.Value - requestedAt).TotalMilliseconds)
            : null;

        var initialCutoff = execution.InitialRunCompletedAt;
        string? initialOutcome = null;
        if (initialCutoff.HasValue)
        {
            var initialActivities = allActivities.Where(a => a.CreatedAt <= initialCutoff.Value).ToList();
            initialOutcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, initialActivities).ToString();
        }

        var initialRun = new ExecutionRevisionInitialRunDto(
            initialCutoff,
            initialCutoff.HasValue && execution.StartedAt.HasValue
                ? (long)Math.Max(0, (initialCutoff.Value - execution.StartedAt.Value).TotalMilliseconds)
                : null,
            initialOutcome);

        return new ExecutionRevisionDto(
            Number: execution.ChangeRequestCount,
            State: state,
            Phase: phase,
            Feedback: execution.LastChangeRequest ?? string.Empty,
            RequestedAt: requestedAt,
            CompletedAt: completedAt,
            DurationMs: durationMs,
            Result: StripResultPrefix(result),
            Summary: summary,
            Unresolved: unresolved,
            Steps: steps,
            Files: files,
            FilesAreFinal: filesAreFinal,
            Build: ranChecks ? build : null,
            Test: ranChecks ? test : null,
            VerificationOutcome: scopedOutcome,
            ChangedFileCount: changed.Count,
            Additions: changed.Sum(f => f.Additions ?? 0),
            Deletions: changed.Sum(f => f.Deletions ?? 0),
            HasDiff: filesAreFinal && changed.Count > 0 &&
                     !string.IsNullOrWhiteSpace(execution.RevisionBaseSnapshotSha) &&
                     !string.IsNullOrWhiteSpace(execution.RevisionResultSnapshotSha),
            NextAction: NextAction(execution, state, overallOutcome),
            InitialRun: initialRun);
    }

    public static string NextAction(TaskExecution execution, string state, ExecutionVerificationOutcome overallOutcome)
    {
        switch (state)
        {
            case "Running":
                return "Wait";
            case "Cancelled":
            case "Failed":
            case "NoChange":
                return "RefineFeedback";
        }

        if (execution.ReviewStatus == ExecutionReviewStatus.Rejected)
        {
            return "RefineFeedback";
        }

        if (execution.ReviewStatus == ExecutionReviewStatus.Pending)
        {
            return overallOutcome is ExecutionVerificationOutcome.NeedsReview
                or ExecutionVerificationOutcome.Failed
                or ExecutionVerificationOutcome.Blocked
                ? "FixChecks"
                : "Review";
        }

        if (execution.CommitStatus != ExecutionCommitStatus.Committed)
        {
            return "Commit";
        }

        if (execution.PushStatus != ExecutionPushStatus.Pushed)
        {
            return "Push";
        }

        return execution.PullRequestStatus == ExecutionPullRequestStatus.Open ? "PullRequestUpdated" : "OpenPullRequest";
    }

    private static IReadOnlyList<ExecutionRevisionStepDto> BuildSteps(
        IReadOnlyList<Event> events,
        bool active,
        bool applyStarted,
        bool applied,
        bool noChange,
        bool applyFailed,
        ExecutionReviewStageStatusDto build,
        ExecutionReviewStageStatusDto test,
        string state)
    {
        var workspaceReady = events.Any(e => e.Activity.Stage == ExecutionStage.Workspace && e.Activity.Status == ExecutionActivityStatus.Completed);
        var workspaceFailed = events.Any(e => e.Activity.Stage == ExecutionStage.Workspace && e.Activity.Status == ExecutionActivityStatus.Failed);

        var prepare = workspaceFailed ? "failed" : workspaceReady || applyStarted ? "done" : active ? "active" : "skipped";

        string apply;
        string? applyDetail = null;
        if (applyFailed)
        {
            apply = "failed";
        }
        else if (applied)
        {
            apply = "done";
        }
        else if (noChange)
        {
            apply = "done";
            applyDetail = "NoChange";
        }
        else if (applyStarted)
        {
            apply = "active";
        }
        else
        {
            apply = prepare == "done" && active ? "active" : active ? "todo" : "skipped";
        }

        string Check(string status) => status switch
        {
            "Passed" or "NoNewRegressions" => "done",
            "Failed" => "failed",
            "Running" => "active",
            _ => active ? "todo" : "skipped",
        };

        var buildState = Check(build.Status);
        var testState = Check(test.Status);

        // Between the edit being applied and the first check starting, the checks are being prepared.
        if (active && applied && buildState == "todo")
        {
            buildState = "active";
        }

        var ready = active ? "todo" : state == "Applied" ? "done" : "skipped";

        return new List<ExecutionRevisionStepDto>
        {
            new("prepare", prepare),
            new("apply", apply, applyDetail),
            new("build", buildState, build.Status == "NoNewRegressions" ? "NoNewRegressions" : null),
            new("test", testState, test.Status == "NoNewRegressions" ? "NoNewRegressions" : null),
            new("ready", ready),
        };
    }

    private static IReadOnlyList<ExecutionRevisionFileDto> BuildFiles(
        IReadOnlyList<Event> events,
        ExecutionGitDiffResult? snapshotDiff,
        bool active)
    {
        var considered = events
            .Where(e => e.Meta?.EventKind == "ApplyingReviewFeedback" && e.Meta.ConsideredFiles != null)
            .SelectMany(e => e.Meta!.ConsideredFiles!)
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!active && snapshotDiff is { Success: true })
        {
            var files = new List<ExecutionRevisionFileDto>();
            foreach (var file in snapshotDiff.ChangedFiles ?? Array.Empty<ExecutionReviewFileDto>())
            {
                files.Add(new ExecutionRevisionFileDto(Normalize(file.Path), StateFor(file.ChangeType), file.Additions, file.Deletions));
            }

            var changedPaths = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            files.AddRange(considered
                .Where(p => !changedPaths.Contains(p))
                .Select(p => new ExecutionRevisionFileDto(p, "Unchanged")));
            return files;
        }

        // Still running (or no snapshots): only what the recorded events prove.
        var changed = events
            .Where(e => e.Meta?.EventKind == "ReviewFeedbackApplied" && e.Meta.ChangedFiles != null)
            .SelectMany(e => e.Meta!.ChangedFiles!)
            .Concat(events
                .Where(e => e.Meta?.RepairFiles != null &&
                            string.Equals(e.Meta.ProgressResult, "Changed", StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => e.Meta!.RepairFiles!))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var live = changed.Select(p => new ExecutionRevisionFileDto(p, "Changed")).ToList();
        live.AddRange(considered
            .Where(p => !changed.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Select(p => new ExecutionRevisionFileDto(p, "Considered")));
        return live;
    }

    private static string StateFor(string? changeType)
    {
        var type = changeType ?? string.Empty;
        if (type.StartsWith("Add", StringComparison.OrdinalIgnoreCase) || type.StartsWith("Creat", StringComparison.OrdinalIgnoreCase))
        {
            return "Created";
        }

        return type.StartsWith("Delet", StringComparison.OrdinalIgnoreCase) ? "Deleted" : "Changed";
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string? StripResultPrefix(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var colon = result.IndexOf(':');
        return colon > 0 && colon < 14 ? result[(colon + 1)..].Trim() : result;
    }

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
