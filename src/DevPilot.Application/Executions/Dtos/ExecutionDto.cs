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
