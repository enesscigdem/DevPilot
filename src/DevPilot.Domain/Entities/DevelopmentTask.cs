using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

public class DevelopmentTask
{
    public Guid Id { get; set; }

    public Guid RepositoryWorkspaceId { get; set; }

    public RepositoryWorkspace RepositoryWorkspace { get; set; } = null!;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? AcceptanceCriteria { get; set; }

    public DevelopmentTaskPriority Priority { get; set; }

    public DevelopmentTaskStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
