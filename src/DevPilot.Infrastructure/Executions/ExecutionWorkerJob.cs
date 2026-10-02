using DevPilot.Application.Executions.Commands.ProcessExecution;
using DevPilot.Application.Executions.Commands.VerifyExecution;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Hangfire background job that drives a single <see cref="Domain.Entities.TaskExecution"/>
/// through its lifecycle by delegating to <see cref="IProcessExecutionCommandHandler"/>.
/// </summary>
/// <remarks>
/// Automatic Hangfire retries are disabled (Attempts = 0) because the
/// <see cref="IProcessExecutionCommandHandler"/> persists the execution as
/// <c>Failed</c> when processing fails.  A re-delivered job would call
/// <c>ClaimAsRunningAsync</c>, find the execution is no longer <c>Pending</c>,
/// and exit silently — leaving the true failure hidden in the Hangfire dashboard
/// rather than surfaced on the execution record.  A controlled failure is more
/// truthful than a silent skip.
/// </remarks>
[AutomaticRetry(Attempts = 0)]
public sealed class ExecutionWorkerJob
{
    private readonly IProcessExecutionCommandHandler _handler;
    private readonly IVerifyExecutionCommandHandler _verifyHandler;
    private readonly ILogger<ExecutionWorkerJob> _logger;

    public ExecutionWorkerJob(
        IProcessExecutionCommandHandler handler,
        IVerifyExecutionCommandHandler verifyHandler,
        ILogger<ExecutionWorkerJob> logger)
    {
        _handler = handler;
        _verifyHandler = verifyHandler;
        _logger = logger;
    }

    /// <summary>
    /// Entry point invoked by Hangfire.  The <paramref name="executionId"/> is
    /// serialised into the Hangfire job payload at enqueue time.
    /// </summary>
    public async Task ExecuteAsync(Guid executionId)
    {
        _logger.LogInformation(
            "ExecutionWorkerJob: starting job for execution {ExecutionId}.",
            executionId);

        var result = await _handler
            .HandleAsync(new ProcessExecutionCommand(executionId))
            .ConfigureAwait(false);

        if (result.Skipped)
        {
            _logger.LogInformation(
                "ExecutionWorkerJob: execution {ExecutionId} was skipped (already processed or not found).",
                executionId);
            return;
        }

        if (!result.Success)
        {
            // The execution has already been persisted as Failed by the handler.
            // Throwing here marks the Hangfire job itself as failed in the dashboard,
            // which is informational only — retries are disabled (Attempts = 0).
            _logger.LogError(
                "ExecutionWorkerJob: execution {ExecutionId} failed: {Error}",
                executionId,
                result.ErrorMessage);

            throw new InvalidOperationException(
                $"Execution {executionId} failed: {result.ErrorMessage}");
        }

        _logger.LogInformation(
            "ExecutionWorkerJob: execution {ExecutionId} finished successfully.",
            executionId);
    }

    public async Task VerifyAsync(Guid executionId, Guid leaseToken)
    {
        _logger.LogInformation(
            "ExecutionWorkerJob: starting re-verification for execution {ExecutionId}.",
            executionId);

        var result = await _verifyHandler
            .ExecuteAsync(executionId, leaseToken)
            .ConfigureAwait(false);

        if (result.Skipped)
        {
            _logger.LogInformation(
                "ExecutionWorkerJob: re-verification for {ExecutionId} was skipped.",
                executionId);
            return;
        }

        if (result.Status == VerifyExecutionResultStatus.Failed)
        {
            _logger.LogError(
                "ExecutionWorkerJob: re-verification for {ExecutionId} failed: {Error}",
                executionId,
                result.ErrorMessage);
            throw new InvalidOperationException(
                $"Re-verification {executionId} failed: {result.ErrorMessage}");
        }

        _logger.LogInformation(
            "ExecutionWorkerJob: re-verification for {ExecutionId} finished.",
            executionId);
    }
}
