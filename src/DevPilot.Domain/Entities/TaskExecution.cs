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
}
