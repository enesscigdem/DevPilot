using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

public class TaskExecution
{
    public Guid Id { get; set; }

    public Guid DevelopmentTaskId { get; set; }

    public DevelopmentTask DevelopmentTask { get; set; } = null!;

    public TaskExecutionStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    public string? WorkspacePath { get; set; }

    public string? BranchName { get; set; }

    public string? Model { get; set; }

    /// <summary>
    /// When set, every AI call of this execution uses this registered model instead of the per-stage
    /// assignments. Used by model comparisons so each run is attributable to exactly one model.
    /// </summary>
    public Guid? PinnedAiModelId { get; set; }

    /// <summary>Name of the pinned model when the run was created (survives deleting the model).</summary>
    public string? PinnedAiModelName { get; set; }

    /// <summary>The comparison run this execution belongs to, if any.</summary>
    public Guid? ModelComparisonRunId { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTime? HeartbeatAt { get; set; }

    public DateTime? LeaseExpiresAt { get; set; }

    public DateTime? CancellationRequestedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancellationReason { get; set; }

    public ExecutionReviewStatus ReviewStatus { get; set; } = ExecutionReviewStatus.Pending;

    public DateTime? ReviewDecidedAt { get; set; }

    public string? ReviewRejectionReason { get; set; }

    public string? ApprovedChangeFingerprint { get; set; }

    public string? BaseCommitSha { get; set; }

    /// <summary>
    /// Repository base the execution branched from. <see cref="BaseCommitSha"/> moves to the previously delivered commit
    /// when a revision is committed on top of it, so baseline comparisons keep using this one.
    /// </summary>
    public string? InitialBaseCommitSha { get; set; }

    /// <summary>Number of times the reviewer asked for a fix on this execution's branch after the first delivery or review.</summary>
    public int RevisionCount { get; set; }

    /// <summary>The reviewer's latest "request changes" feedback (the instruction the AI was given).</summary>
    public string? LastChangeRequest { get; set; }

    public DateTime? LastChangeRequestAt { get; set; }

    /// <summary>Short outcome of the latest requested fix (null while it is still running).</summary>
    public string? LastChangeRequestResult { get; set; }

    public ExecutionCommitStatus CommitStatus { get; set; } = ExecutionCommitStatus.None;

    public Guid? CommitAttemptId { get; set; }

    public DateTime? CommitClaimedAt { get; set; }

    public string? CommitSha { get; set; }

    public DateTime? CommittedAt { get; set; }

    public ExecutionPushStatus PushStatus { get; set; } = ExecutionPushStatus.None;

    public Guid? PushAttemptId { get; set; }

    public DateTime? PushClaimedAt { get; set; }

    public string? RemoteBranchName { get; set; }

    public string? RemoteCommitSha { get; set; }

    public DateTime? PushedAt { get; set; }

    public ExecutionPullRequestStatus PullRequestStatus { get; set; } = ExecutionPullRequestStatus.None;

    public Guid? PullRequestAttemptId { get; set; }

    public DateTime? PullRequestClaimedAt { get; set; }

    public int? PullRequestNumber { get; set; }

    public string? PullRequestUrl { get; set; }

    public DateTime? PullRequestCreatedAt { get; set; }

    public string? PullRequestBaseBranch { get; set; }

    public ExecutionPullRequestRemoteState PullRequestRemoteState { get; set; } = ExecutionPullRequestRemoteState.Unknown;

    public ExecutionPullRequestIntegrityStatus PullRequestIntegrityStatus { get; set; } = ExecutionPullRequestIntegrityStatus.Unknown;

    public DateTime? PullRequestLastSyncedAt { get; set; }

    public DateTime? PullRequestLastSyncAttemptAt { get; set; }

    public DateTime? PullRequestMergedAt { get; set; }

    public DateTime? PullRequestClosedAt { get; set; }

    public Guid? PullRequestSyncAttemptId { get; set; }

    public DateTime? PullRequestSyncClaimedAt { get; set; }

    public ExecutionCiStatus CiStatus { get; set; } = ExecutionCiStatus.Unknown;

    public DateTime? CiLastSyncedAt { get; set; }

    public ICollection<ExecutionCiCheck> CiChecks { get; set; } = new List<ExecutionCiCheck>();

    public ExecutionMergeStatus MergeStatus { get; set; } = ExecutionMergeStatus.None;

    public Guid? MergeAttemptId { get; set; }

    public DateTime? MergeClaimedAt { get; set; }

    public string? MergeCommitSha { get; set; }

    public DateTime? MergedAt { get; set; }

    public string? MergeMethod { get; set; }

    /// <summary>Terminal verification outcome captured when the execution finished (history; null for older rows).</summary>
    public string? VerificationOutcome { get; set; }

    /// <summary>JSON of the terminal verdict + usage snapshot (null for older rows).</summary>
    public string? VerificationSnapshotJson { get; set; }
}
