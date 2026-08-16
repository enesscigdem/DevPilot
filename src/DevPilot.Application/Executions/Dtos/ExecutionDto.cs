using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Dtos;

public sealed class ExecutionDto
{
    public Guid Id { get; set; }

    public Guid DevelopmentTaskId { get; set; }

    public string TaskTitle { get; set; } = string.Empty;

    public Guid RepositoryWorkspaceId { get; set; }

    public string RepositoryOwner { get; set; } = string.Empty;

    public string RepositoryName { get; set; } = string.Empty;

    public TaskExecutionStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    public string ReviewStatus { get; set; } = ExecutionReviewStatus.Pending.ToString();

    public string CommitStatus { get; set; } = ExecutionCommitStatus.None.ToString();

    public string? CommitSha { get; set; }

    public DateTime? CommittedAt { get; set; }

    public string PushStatus { get; set; } = ExecutionPushStatus.None.ToString();

    public string? RemoteBranchName { get; set; }

    public string? RemoteCommitSha { get; set; }

    public DateTime? PushedAt { get; set; }

    public bool CanRequestPush { get; set; }

    public string PullRequestStatus { get; set; } = ExecutionPullRequestStatus.None.ToString();

    public int? PullRequestNumber { get; set; }

    public string? PullRequestUrl { get; set; }

    public DateTime? PullRequestCreatedAt { get; set; }

    public bool CanRequestPullRequest { get; set; }

    public string PullRequestRemoteState { get; set; } = ExecutionPullRequestRemoteState.Unknown.ToString();

    public string PullRequestIntegrityStatus { get; set; } = ExecutionPullRequestIntegrityStatus.Unknown.ToString();

    public DateTime? PullRequestLastSyncedAt { get; set; }

    public string CiStatus { get; set; } = ExecutionCiStatus.Unknown.ToString();

    public IReadOnlyList<DevPilot.Application.Executions.Commands.SyncPullRequest.ExecutionCiCheckDto> CiChecks { get; set; } =
        Array.Empty<DevPilot.Application.Executions.Commands.SyncPullRequest.ExecutionCiCheckDto>();

    public string MergeStatus { get; set; } = ExecutionMergeStatus.None.ToString();

    public string? MergeCommitSha { get; set; }

    public DateTime? MergedAt { get; set; }

    public bool CanRequestMerge { get; set; }
}

public sealed class ExecutionListItemDto
{
    public Guid Id { get; set; }

    public Guid DevelopmentTaskId { get; set; }

    public string TaskTitle { get; set; } = string.Empty;

    public string RepositoryName { get; set; } = string.Empty;

    public TaskExecutionStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
}
