using DevPilot.Application.Tasks.Dtos;

namespace DevPilot.Application.Tasks.Commands.UpdateTask;

public sealed record UpdateTaskCommand(Guid Id, UpdateTaskDto Dto);

public sealed class UpdateTaskResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public TaskDto? Task { get; set; }

    public bool NotFound { get; set; }

    /// <summary>The task is in a state where its text cannot be changed.</summary>
    public bool Conflict { get; set; }

    /// <summary>The change made an earlier analysis or approval void; the task has to be analysed and approved again.</summary>
    public bool ApprovalReset { get; set; }
}
