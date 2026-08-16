using DevPilot.Application.Tasks.Dtos;

namespace DevPilot.Application.Tasks.Commands.UpdateTask;

public sealed record UpdateTaskCommand(Guid Id, UpdateTaskDto Dto);

public sealed class UpdateTaskResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public TaskDto? Task { get; set; }
}
