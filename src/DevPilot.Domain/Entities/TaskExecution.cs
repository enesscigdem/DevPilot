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

    public ExecutionReviewStatus ReviewStatus { get; set; } = ExecutionReviewStatus.Pending;

    public DateTime? ReviewDecidedAt { get; set; }

    public string? ReviewRejectionReason { get; set; }

    public string? ApprovedChangeFingerprint { get; set; }

    public string? BaseCommitSha { get; set; }

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
}
