using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
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

        // Same rule the UI reads from the execution DTO. An active revision of a delivered execution is cancellable;
        // only a delivery step that is executing right now is protected.
        var blocked = ExecutionCancellationPolicy.DescribeWhyCannotCancel(execution);
        if (blocked is not null)
        {
            return CancelExecutionResult.Conflict(blocked);
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
