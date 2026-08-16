using DevPilot.Domain.Enums;

namespace DevPilot.Application.Tasks.Dtos;

public sealed class TaskDto
{
    public Guid Id { get; set; }

    public Guid RepositoryWorkspaceId { get; set; }

    public string RepositoryWorkspaceName { get; set; } = string.Empty;

    public string RepositoryOwner { get; set; } = string.Empty;

    public string RepositoryName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? AcceptanceCriteria { get; set; }

    public DevelopmentTaskPriority Priority { get; set; }

    public DevelopmentTaskStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed class CreateTaskDto
{
    public Guid RepositoryWorkspaceId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? AcceptanceCriteria { get; set; }

    public DevelopmentTaskPriority Priority { get; set; } = DevelopmentTaskPriority.Medium;
}

public sealed class UpdateTaskDto
{
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? AcceptanceCriteria { get; set; }

    public DevelopmentTaskPriority Priority { get; set; } = DevelopmentTaskPriority.Medium;
}

public sealed class UpdateTaskStatusDto
{
    public DevelopmentTaskStatus Status { get; set; }
}

public sealed class TaskListItemDto
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string RepositoryName { get; set; } = string.Empty;

    public DevelopmentTaskStatus Status { get; set; }

    public DevelopmentTaskPriority Priority { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed class TaskQueryFilterDto
{
    public DevelopmentTaskStatus? Status { get; set; }

    public DevelopmentTaskPriority? Priority { get; set; }

    public Guid? RepositoryWorkspaceId { get; set; }
}
