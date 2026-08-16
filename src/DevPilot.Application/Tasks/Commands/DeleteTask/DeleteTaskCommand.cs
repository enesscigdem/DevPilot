namespace DevPilot.Application.Tasks.Commands.DeleteTask;

public sealed record DeleteTaskCommand(Guid Id);

public sealed class DeleteTaskResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public bool NotFound { get; set; }
}
