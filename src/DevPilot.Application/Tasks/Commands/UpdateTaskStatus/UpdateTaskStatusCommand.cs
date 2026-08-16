using DevPilot.Domain.Enums;

namespace DevPilot.Application.Tasks.Commands.UpdateTaskStatus;

public sealed record UpdateTaskStatusCommand(Guid Id, DevelopmentTaskStatus Status);

public sealed class UpdateTaskStatusResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public bool NotFound { get; set; }
}
