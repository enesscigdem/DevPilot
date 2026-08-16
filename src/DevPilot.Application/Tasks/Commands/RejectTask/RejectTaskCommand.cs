using DevPilot.Application.Tasks.Dtos;

namespace DevPilot.Application.Tasks.Commands.RejectTask;

public sealed record RejectTaskCommand(Guid TaskId);

public sealed class RejectTaskResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>True when the task was not found.</summary>
    public bool NotFound { get; set; }

    /// <summary>True when the transition is invalid (wrong status, etc.).</summary>
    public bool Conflict { get; set; }

    public TaskDto? Task { get; set; }
}
