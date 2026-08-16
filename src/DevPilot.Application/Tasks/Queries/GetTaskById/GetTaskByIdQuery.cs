using DevPilot.Application.Tasks.Dtos;

namespace DevPilot.Application.Tasks.Queries.GetTaskById;

public sealed record GetTaskByIdQuery(Guid Id);

public sealed class GetTaskByIdResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public TaskDto? Task { get; set; }

    public bool NotFound { get; set; }
}
