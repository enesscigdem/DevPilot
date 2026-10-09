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

    /// <summary>Tracker the task was imported from (for example "Jira"); null for tasks created in DevPilot.</summary>
    public string? ExternalSource { get; set; }

    /// <summary>Issue key in that tracker, for example ARF-123. Unique per workspace so an issue is never imported twice.</summary>
    public string? ExternalKey { get; set; }

    public string? ExternalUrl { get; set; }

    /// <summary>Connection used to report progress back to the issue. Null when the connection was removed.</summary>
    public Guid? ExternalConnectionId { get; set; }

    /// <summary>
    /// Normalised address of the tracker site the key belongs to (empty when unknown). Together with the key it identifies the
    /// issue, so the same key on another site is a different issue.
    /// </summary>
    public string ExternalOrigin { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
