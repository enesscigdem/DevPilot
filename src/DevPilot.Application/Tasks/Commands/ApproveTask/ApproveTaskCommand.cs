using DevPilot.Application.Tasks.Dtos;

namespace DevPilot.Application.Tasks.Commands.ApproveTask;

public sealed record ApproveTaskCommand(Guid TaskId);

public sealed class ApproveTaskResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>True when the task was not found.</summary>
    public bool NotFound { get; set; }

    /// <summary>True when the transition is invalid (wrong status, missing analysis, etc.).</summary>
    public bool Conflict { get; set; }

    public TaskDto? Task { get; set; }
}
