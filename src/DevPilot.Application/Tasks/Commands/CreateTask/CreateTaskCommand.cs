using DevPilot.Application.Tasks.Dtos;

namespace DevPilot.Application.Tasks.Commands.CreateTask;

public sealed record CreateTaskCommand(CreateTaskDto Dto);

public sealed class CreateTaskResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public TaskDto? Task { get; set; }
}
