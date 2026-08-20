namespace DevPilot.Application.Executions.Commands.CancelExecution;

public sealed record CancelExecutionCommand(
    Guid ExecutionId,
    Guid? RepositoryWorkspaceId = null,
    string? Reason = null);

public enum CancelExecutionResultStatus
{
    Success,
    NotFound,
    Conflict
}

public sealed class CancelExecutionResult
{
    public CancelExecutionResultStatus Status { get; set; }
    public string? ErrorMessage { get; set; }

    public static CancelExecutionResult Succeeded() =>
        new() { Status = CancelExecutionResultStatus.Success };

    public static CancelExecutionResult NotFound(string message = "Execution not found.") =>
        new() { Status = CancelExecutionResultStatus.NotFound, ErrorMessage = message };

    public static CancelExecutionResult Conflict(string message) =>
        new() { Status = CancelExecutionResultStatus.Conflict, ErrorMessage = message };
}

public interface ICancelExecutionCommandHandler
{
    Task<CancelExecutionResult> HandleAsync(
        CancelExecutionCommand command,
        CancellationToken cancellationToken = default);
}
