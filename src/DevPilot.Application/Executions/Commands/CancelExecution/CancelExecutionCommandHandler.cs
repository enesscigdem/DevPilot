using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.CancelExecution;

public sealed class CancelExecutionCommandHandler : ICancelExecutionCommandHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionCancellationRegistry _cancellationRegistry;
    private readonly ILogger<CancelExecutionCommandHandler> _logger;

    public CancelExecutionCommandHandler(
        IExecutionRepository executionRepository,
        IExecutionCancellationRegistry cancellationRegistry,
        ILogger<CancelExecutionCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _cancellationRegistry = cancellationRegistry;
        _logger = logger;
    }

    public async Task<CancelExecutionResult> HandleAsync(
        CancelExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return CancelExecutionResult.NotFound();
        }

        if (command.RepositoryWorkspaceId.HasValue &&
            execution.DevelopmentTask != null &&
            execution.DevelopmentTask.RepositoryWorkspaceId != command.RepositoryWorkspaceId.Value)
        {
            return CancelExecutionResult.NotFound();
        }

        // Check if already in terminal state
        if (execution.Status is TaskExecutionStatus.Completed
            or TaskExecutionStatus.Failed
            or TaskExecutionStatus.Cancelled)
        {
            return CancelExecutionResult.Conflict($"Execution is already in a terminal state ({execution.Status}).");
        }

        // Check if reached irreversible Git operations
        if (execution.CommitStatus != ExecutionCommitStatus.None ||
            execution.PushStatus != ExecutionPushStatus.None ||
            execution.PullRequestStatus != ExecutionPullRequestStatus.None)
        {
            return CancelExecutionResult.Conflict("Execution has reached an irreversible stage (commit/push/PR) and cannot be cancelled.");
        }

        var requested = await _executionRepository
            .RequestCancellationAsync(command.ExecutionId, command.Reason, cancellationToken)
            .ConfigureAwait(false);

        // Also signal fast in-process registry
        _cancellationRegistry.TryCancel(command.ExecutionId);

        _logger.LogInformation(
            "CancelExecutionCommandHandler: cancellation requested for execution {ExecutionId} (Persisted: {Persisted}).",
            command.ExecutionId,
            requested);

        return CancelExecutionResult.Succeeded();
    }
}
