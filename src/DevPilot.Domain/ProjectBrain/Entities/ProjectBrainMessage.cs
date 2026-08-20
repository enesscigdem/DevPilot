namespace DevPilot.Domain.ProjectBrain.Entities;

public sealed class ProjectBrainMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    public string Role { get; set; } = "user";

    public string Content { get; set; } = string.Empty;

    public int? Confidence { get; set; }

    public string? Elapsed { get; set; }

    public string? CitationsJson { get; set; }

    public string? ContextFilesJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ProjectBrainConversation? Conversation { get; set; }
}
