namespace DevPilot.Application.Executions.Dtos;

/// <summary>One step of a requested fix. State: todo | active | done | failed | skipped.</summary>
public sealed record ExecutionRevisionStepDto(string Key, string State, string? Detail = null);

/// <summary>A file the fix looked at or changed. State: Considered | Changed | Created | Deleted | Unchanged.</summary>
public sealed record ExecutionRevisionFileDto(string Path, string State, int? Additions = null, int? Deletions = null);

/// <summary>What the first generation run looked like, kept apart so it is never mistaken for the fix.</summary>
public sealed record ExecutionRevisionInitialRunDto(DateTime? CompletedAt, long? DurationMs, string? Outcome);

/// <summary>
/// The latest "request changes" round of an execution, derived only from what happened since it was requested.
/// State: Running | Applied | NoChange | Failed | Cancelled.
/// </summary>
public sealed record ExecutionRevisionDto(
    int Number,
    string State,
    string Phase,
    string Feedback,
    DateTime RequestedAt,
    DateTime? CompletedAt,
    long? DurationMs,
    string? Result,
    string? Summary,
    string? Unresolved,
    IReadOnlyList<ExecutionRevisionStepDto> Steps,
    IReadOnlyList<ExecutionRevisionFileDto> Files,
    bool FilesAreFinal,
    ExecutionReviewStageStatusDto? Build,
    ExecutionReviewStageStatusDto? Test,
    string? VerificationOutcome,
    int ChangedFileCount,
    int Additions,
    int Deletions,
    bool HasDiff,
    string NextAction,
    ExecutionRevisionInitialRunDto InitialRun);

/// <summary>The unified diff of just the latest fix.</summary>
public sealed record ExecutionRevisionDiffDto(
    IReadOnlyList<ExecutionReviewFileDto> Files,
    string Diff,
    bool Truncated);
