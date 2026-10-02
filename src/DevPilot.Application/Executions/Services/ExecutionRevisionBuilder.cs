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

    /// <summary>One requested fix as the builder sees it: from a history row, or from the execution's own latest-fix fields.</summary>
    public sealed record RevisionRecord(
        int Number,
        string Feedback,
        DateTime RequestedAt,
        DateTime? CompletedAt,
        string? Result,
        string? BaseSnapshotSha,
        string? ResultSnapshotSha);

    public static RevisionRecord FromEntity(ExecutionRevision row) =>
        new(row.Number, row.Feedback, row.RequestedAt, row.CompletedAt, row.Result, row.BaseSnapshotSha, row.ResultSnapshotSha);

    /// <summary>Executions fixed before history rows existed still have their latest fix on the execution itself.</summary>
    public static RevisionRecord FromExecution(TaskExecution execution) =>
        new(
            execution.ChangeRequestCount,
            execution.LastChangeRequest ?? string.Empty,
            execution.LastChangeRequestAt!.Value,
            null,
            execution.LastChangeRequestResult,
            execution.RevisionBaseSnapshotSha,
            execution.RevisionResultSnapshotSha);

    /// <summary>The latest fix only (kept for callers that need just that one).</summary>
    public static ExecutionRevisionDto? Build(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> allActivities,
        ExecutionVerificationOutcome overallOutcome,
        ExecutionGitDiffResult? snapshotDiff) =>
        !ExecutionRevisionScope.HasRevision(execution)
            ? null
            : BuildOne(execution, FromExecution(execution), true, null, allActivities, overallOutcome, snapshotDiff);

    /// <summary>
    /// Every requested fix, oldest first. Each one only sees the activity between its own request and the next
    /// request, so an older fix never shows a newer fix's files, checks or events (and the other way round).
    /// </summary>
    public static IReadOnlyList<ExecutionRevisionDto> BuildAll(
        TaskExecution execution,
        IReadOnlyList<ExecutionRevision> rows,
        IReadOnlyList<ExecutionActivity> allActivities,
        ExecutionVerificationOutcome overallOutcome,
        Func<RevisionRecord, ExecutionGitDiffResult?> diffFor)
    {
        if (!ExecutionRevisionScope.HasRevision(execution))
        {
            return Array.Empty<ExecutionRevisionDto>();
        }

        var records = rows.OrderBy(r => r.Number).Select(FromEntity).ToList();
        // The latest fix of an execution that predates history rows is described by the execution itself.
        if (records.Count == 0 || records[^1].Number < execution.ChangeRequestCount)
        {
            var latest = FromExecution(execution);
            records.RemoveAll(r => r.Number == latest.Number);
            records.Add(latest);
        }
        else
        {
            // The execution mirrors the live latest fix, which is more current than its row while it runs.
            var last = records[^1];
            records[^1] = last with
            {
                Result = execution.LastChangeRequestResult ?? last.Result,
                BaseSnapshotSha = execution.RevisionBaseSnapshotSha ?? last.BaseSnapshotSha,
                ResultSnapshotSha = execution.RevisionResultSnapshotSha ?? last.ResultSnapshotSha,
            };
        }

        var dtos = new List<ExecutionRevisionDto>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            var isLatest = i == records.Count - 1;
            DateTime? windowEnd = isLatest ? null : records[i + 1].RequestedAt;
            dtos.Add(BuildOne(execution, records[i], isLatest, windowEnd, allActivities, overallOutcome, diffFor(records[i])));
        }

        return dtos;
    }

    private static ExecutionRevisionDto BuildOne(
        TaskExecution execution,
        RevisionRecord record,
        bool isLatest,
        DateTime? windowEnd,
        IReadOnlyList<ExecutionActivity> allActivities,
        ExecutionVerificationOutcome overallOutcome,
        ExecutionGitDiffResult? snapshotDiff)
    {
        var requestedAt = record.RequestedAt;
        var scoped = allActivities
            .Where(a => a.CreatedAt >= requestedAt && (windowEnd == null || a.CreatedAt < windowEnd))
            .ToList();
        var events = scoped.Select(a => new Event(a, ParseMetadata(a.MetadataJson))).ToList();

        var active = isLatest && ExecutionRevisionScope.IsActive(execution);
        var result = record.Result;
        var state = active
            ? "Running"
            : result is not null && result.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase)
                ? "Cancelled"
                : isLatest && execution.Status == TaskExecutionStatus.Cancelled
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

        DateTime? completedAt = active ? null : record.CompletedAt ?? (isLatest ? execution.CompletedAt : null);
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
            Number: record.Number,
            State: state,
            Phase: phase,
            Feedback: record.Feedback,
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
                     !string.IsNullOrWhiteSpace(record.BaseSnapshotSha) &&
                     !string.IsNullOrWhiteSpace(record.ResultSnapshotSha),
            NextAction: isLatest ? NextAction(execution, state, overallOutcome) : "None",
            InitialRun: initialRun,
            IsLatest: isLatest,
            WindowEnd: windowEnd);
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
