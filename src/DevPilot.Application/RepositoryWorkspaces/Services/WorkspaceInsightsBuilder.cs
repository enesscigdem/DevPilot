using DevPilot.Application.Executions.Services;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.RepositoryWorkspaces.Services;

/// <summary>Pure aggregation of execution snapshots into repository-level engineering insights.</summary>
public static class WorkspaceInsightsBuilder
{
    private const int RecentRows = 20;
    private const int MaxRetriedTasks = 8;
    private const int MaxFailureReasons = 5;

    public static WorkspaceInsightsDto Build(IReadOnlyList<InsightExecutionInput> inputs, DateTime now)
    {
        var terminal = inputs
            .Where(i => i.Status is TaskExecutionStatus.Completed or TaskExecutionStatus.Failed or TaskExecutionStatus.Cancelled)
            .ToList();
        var measured = terminal.Where(i => i.Status != TaskExecutionStatus.Cancelled).ToList();

        static ExecutionVerificationOutcome ParseOutcome(InsightExecutionInput i) =>
            Enum.TryParse<ExecutionVerificationOutcome>(i.Snapshot.Verdict.Outcome, out var o) ? o : ExecutionVerificationOutcome.Failed;

        static int Repairs(InsightExecutionInput i) =>
            i.Snapshot.Verdict.CompileRepairRounds + i.Snapshot.Verdict.TestRepairRounds;

        static bool Ready(InsightExecutionInput i) =>
            ExecutionVerificationEvaluator.IsDeliveryEligible(ParseOutcome(i));

        static int? Duration(InsightExecutionInput i) =>
            i.StartedAt.HasValue && i.CompletedAt.HasValue
                ? (int)Math.Max(0, (i.CompletedAt.Value - i.StartedAt.Value).TotalSeconds)
                : null;

        var ready = measured.Count(Ready);
        var firstPass = measured.Count(i => Ready(i) && Repairs(i) == 0 && i.Snapshot.Verdict.ApplicabilityRepairs == 0);
        var repaired = measured.Where(i => Repairs(i) > 0 || i.Snapshot.Verdict.ApplicabilityRepairs > 0).ToList();
        var repairedRecovered = repaired.Count(Ready);
        var verified = measured.Count(i => ParseOutcome(i) is ExecutionVerificationOutcome.Verified or ExecutionVerificationOutcome.NoNewRegressions);

        var durations = measured.Select(Duration).Where(d => d.HasValue).Select(d => d!.Value).OrderBy(d => d).ToList();
        int? median = durations.Count == 0 ? null : durations[durations.Count / 2];
        int? avgDuration = durations.Count == 0 ? null : (int)Math.Round(durations.Average());

        var withCalls = measured.Where(i => i.Snapshot.Usage.ProviderCalls > 0).ToList();
        var totalTokens = measured.Sum(i => i.Snapshot.Usage.TotalTokens);
        var costs = measured.Select(i => i.Snapshot.Usage.EstimatedCostUsd).Where(c => c.HasValue).Select(c => c!.Value).ToList();

        var totals = new WorkspaceInsightsTotalsDto(
            Executions: terminal.Count,
            Cancelled: terminal.Count - measured.Count,
            Measured: measured.Count,
            DeliveryReady: ready,
            DeliveryReadyRate: Rate(ready, measured.Count),
            VerifiedRate: Rate(verified, measured.Count),
            FirstPass: firstPass,
            FirstPassRate: Rate(firstPass, measured.Count),
            Repaired: repaired.Count,
            RepairedRecovered: repairedRecovered,
            RepairRecoveryRate: Rate(repairedRecovered, repaired.Count),
            AvgDurationSeconds: avgDuration,
            MedianDurationSeconds: median,
            TotalTokens: totalTokens,
            AvgTokensPerExecution: withCalls.Count == 0 ? null : withCalls.Sum(i => i.Snapshot.Usage.TotalTokens) / withCalls.Count,
            AvgProviderCalls: withCalls.Count == 0 ? null : Math.Round(withCalls.Average(i => (double)i.Snapshot.Usage.ProviderCalls), 1),
            TotalCostUsd: costs.Count == 0 ? null : costs.Sum());

        var outcomes = measured
            .GroupBy(i => i.Snapshot.Verdict.Outcome)
            .Select(g => new InsightOutcomeCountDto(g.Key, g.Count()))
            .OrderByDescending(o => o.Count)
            .ToList();

        var signals = new List<InsightSignalDto>
        {
            new("PreExistingSeparated", "Pre-existing failures separated",
                "Runs where failures already on the base commit were proven not to be caused by the change.",
                measured.Count(i => ParseOutcome(i) == ExecutionVerificationOutcome.NoNewRegressions)),
            new("RepairedAutomatically", "Recovered by repair",
                "Runs that hit a build, test or edit-applicability problem and still reached a deliverable state.",
                repairedRecovered),
            new("FlakeAbsorbed", "Flaky failures absorbed",
                "A failing test passed on a confirmation rerun, so no repair was spent on it.",
                measured.Count(i => i.Snapshot.Verdict.FlakeConfirmed)),
            new("TestWeakeningCaught", "Weakened tests caught",
                "A test repair removed assertions, added skips or deleted tests and was sent to review.",
                measured.Count(i => i.Snapshot.Verdict.TestWeakeningSuspected)),
            new("BaselineUnverified", "Baseline unverified",
                "Runs repaired while the baseline comparison was inconclusive; never reported as fully verified.",
                measured.Count(i => i.Snapshot.Verdict.BaselineUnverified)),
            new("StaleBase", "Stale base detected",
                "Runs started while the local base was behind origin.",
                measured.Count(i => i.Snapshot.Verdict.StaleBase)),
        };

        var reasons = measured
            .Where(i => ParseOutcome(i) is ExecutionVerificationOutcome.NeedsReview or ExecutionVerificationOutcome.Failed)
            .Select(i => i.Snapshot.Verdict.Findings.FirstOrDefault(f => f.Kind == "FailureReason")?.Message ?? i.Snapshot.Verdict.Headline)
            .GroupBy(r => r)
            .Select(g => new InsightFailureReasonDto(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .Take(MaxFailureReasons)
            .ToList();

        var attemptByExecution = terminal
            .GroupBy(i => i.TaskId)
            .SelectMany(g => g.OrderBy(i => i.CreatedAt).Select((i, idx) => (i.ExecutionId, Attempt: idx + 1)))
            .ToDictionary(x => x.ExecutionId, x => x.Attempt);

        var recent = terminal
            .OrderByDescending(i => i.CreatedAt)
            .Take(RecentRows)
            .Select(i => new InsightExecutionRowDto(
                i.ExecutionId,
                i.TaskId,
                i.TaskTitle,
                attemptByExecution[i.ExecutionId],
                i.Status.ToString(),
                i.Snapshot.Verdict.Outcome,
                i.Snapshot.Verdict.Headline,
                i.CreatedAt,
                Duration(i),
                Repairs(i),
                i.Snapshot.Usage.ProviderCalls,
                i.Snapshot.Usage.TotalTokens,
                i.Snapshot.Usage.EstimatedCostUsd))
            .ToList();

        var retried = terminal
            .GroupBy(i => i.TaskId)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var ordered = g.OrderBy(i => i.CreatedAt).ToList();
                var first = ordered.First();
                var last = ordered.Last();
                return new InsightRetriedTaskDto(
                    g.Key,
                    last.TaskTitle,
                    ordered.Count,
                    first.ExecutionId,
                    last.ExecutionId,
                    first.Snapshot.Verdict.Outcome,
                    last.Snapshot.Verdict.Outcome,
                    !Ready(first) && Ready(last));
            })
            .OrderByDescending(r => r.Attempts)
            .Take(MaxRetriedTasks)
            .ToList();

        return new WorkspaceInsightsDto(now, inputs.Count, totals, outcomes, signals, reasons, recent, retried);
    }

    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : Math.Round(numerator / (double)denominator, 3);
}
