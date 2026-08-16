using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

public class ExecutionActivity
{
    public Guid Id { get; set; }

    public Guid ExecutionId { get; set; }

    public TaskExecution? Execution { get; set; }

    public ExecutionStage Stage { get; set; }

    public ExecutionActivityStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? MetadataJson { get; set; }
}
