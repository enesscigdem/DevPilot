namespace DevPilot.Domain.ProjectBrain.Entities;

public sealed class ProjectBrainConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RepositoryWorkspaceId { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ProjectBrainMessage> Messages { get; set; } = new();
}
