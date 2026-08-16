using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

public class ExecutionCiCheck
{
    public Guid Id { get; set; }

    public Guid TaskExecutionId { get; set; }

    public TaskExecution TaskExecution { get; set; } = null!;

    public long ExternalId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public ExecutionCiCheckType CheckType { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? Conclusion { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
