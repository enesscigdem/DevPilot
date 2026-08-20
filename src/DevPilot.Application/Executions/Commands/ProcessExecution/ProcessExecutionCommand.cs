using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.ProcessExecution;

/// <summary>Command carrying the execution to be processed.</summary>
public sealed record ProcessExecutionCommand(Guid ExecutionId);

public sealed class ProcessExecutionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>True when the execution was not found or already in a terminal state (idempotency).</summary>
    public bool Skipped { get; set; }
}

public interface IProcessExecutionCommandHandler
{
    Task<ProcessExecutionResult> HandleAsync(
        ProcessExecutionCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates the full execution lifecycle for a single <see cref="Domain.Entities.TaskExecution"/>.
/// <para>
/// Lifecycle: Pending → Running → Completed | Failed
/// </para>
/// <para>
/// This handler is safe to call multiple times for the same execution.  If the
/// execution is not in <c>Pending</c> status, <see cref="ClaimAsRunningAsync"/>
/// returns <c>false</c> and the handler exits without side-effects.
/// </para>
/// </summary>
public sealed class ProcessExecutionCommandHandler : IProcessExecutionCommandHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IImpactAnalysisRepository _impactAnalysisRepository;
    private readonly IExecutionProcessor _processor;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly IExecutionHeartbeatService _heartbeatService;
    private readonly IExecutionCancellationRegistry _cancellationRegistry;
    private readonly ILogger<ProcessExecutionCommandHandler> _logger;

    public ProcessExecutionCommandHandler(
        IExecutionRepository executionRepository,
        IImpactAnalysisRepository impactAnalysisRepository,
        IExecutionProcessor processor,
        IExecutionActivityRecorder activityRecorder,
        IExecutionHeartbeatService heartbeatService,
        IExecutionCancellationRegistry cancellationRegistry,
        ILogger<ProcessExecutionCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _impactAnalysisRepository = impactAnalysisRepository;
        _processor = processor;
        _activityRecorder = activityRecorder;
        _heartbeatService = heartbeatService;
        _cancellationRegistry = cancellationRegistry;
        _logger = logger;
    }

    public async Task<ProcessExecutionResult> HandleAsync(
        ProcessExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        var executionId = command.ExecutionId;
        var leaseToken = Guid.NewGuid();

        // ── 1. Load execution (with Task + Workspace eagerly loaded) ─────────────
        var execution = await _executionRepository
            .GetByIdAsync(executionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            _logger.LogWarning(
                "ProcessExecution: execution {ExecutionId} not found — skipping.",
                executionId);
            return new ProcessExecutionResult { Success = false, Skipped = true, ErrorMessage = "Execution not found." };
        }

        var task = execution.DevelopmentTask;
        var workspace = task.RepositoryWorkspace;

        // ── 2. Atomic claim: Pending → Running with unique lease token ────────────
        var claimed = await _executionRepository
            .ClaimAsRunningAsync(executionId, leaseToken, cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            _logger.LogInformation(
                "ProcessExecution: execution {ExecutionId} is not Pending (status={Status}) — skipping (idempotent).",
                executionId,
                execution.Status);
            return new ProcessExecutionResult { Success = true, Skipped = true };
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var executionToken = _cancellationRegistry.Register(executionId, linkedCts.Token);

        await using var heartbeatSession = _heartbeatService.StartHeartbeat(
            executionId,
            leaseToken,
            interval: TimeSpan.FromSeconds(15),
            leaseDuration: TimeSpan.FromSeconds(45),
            linkedCts);

        try
        {
            _logger.LogInformation(
                "ProcessExecution: execution {ExecutionId} claimed as Running with lease {LeaseToken} for task '{TaskTitle}' ({TaskId}).",
                executionId,
                leaseToken,
                task.Title,
                task.Id);

            // Record Execution Started ONLY AFTER successful claim
            await SafeRecordActivityAsync(
                executionId,
                ExecutionStage.Execution,
                ExecutionActivityStatus.Started,
                "Execution started.",
                cancellationToken: executionToken).ConfigureAwait(false);

            // Check cancellation checkpoint 1
            if (executionToken.IsCancellationRequested || await _executionRepository.IsCancellationRequestedAsync(executionId, CancellationToken.None).ConfigureAwait(false))
            {
                return await HandleCancellationAsync(executionId, leaseToken).ConfigureAwait(false);
            }

            // ── 3. Verify workspace local path ────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(workspace.LocalPath))
            {
                const string pathError = "RepositoryWorkspace has no local path configured.";
                _logger.LogError(
                    "ProcessExecution: {Error} WorkspaceId={WorkspaceId}",
                    pathError,
                    workspace.Id);
                await FailExecutionAsync(executionId, leaseToken, pathError, CancellationToken.None).ConfigureAwait(false);
                return new ProcessExecutionResult { Success = false, ErrorMessage = pathError };
            }

            if (!Directory.Exists(workspace.LocalPath))
            {
                var pathError = $"Workspace local path does not exist on disk: '{workspace.LocalPath}'.";
                _logger.LogError(
                    "ProcessExecution: {Error} WorkspaceId={WorkspaceId}",
                    pathError,
                    workspace.Id);
                await FailExecutionAsync(executionId, leaseToken, pathError, CancellationToken.None).ConfigureAwait(false);
                return new ProcessExecutionResult { Success = false, ErrorMessage = pathError };
            }

            // ── 4. Load completed impact analysis ─────────────────────────────────────
            var analysis = await _impactAnalysisRepository
                .GetLatestByTaskIdAsync(task.Id, executionToken)
                .ConfigureAwait(false);

            if (analysis is null || analysis.Status != ImpactAnalysisStatus.Completed)
            {
                const string analysisError = "No completed impact analysis found for the task.";
                _logger.LogError(
                    "ProcessExecution: {Error} TaskId={TaskId}",
                    analysisError,
                    task.Id);
                await FailExecutionAsync(executionId, leaseToken, analysisError, CancellationToken.None).ConfigureAwait(false);
                return new ProcessExecutionResult { Success = false, ErrorMessage = analysisError };
            }

            // Check cancellation checkpoint 2
            if (executionToken.IsCancellationRequested || await _executionRepository.IsCancellationRequestedAsync(executionId, CancellationToken.None).ConfigureAwait(false))
            {
                return await HandleCancellationAsync(executionId, leaseToken).ConfigureAwait(false);
            }

            // ── 5. Execute via processor ──────────────────────────────────────────────
            try
            {
                var context = new ExecutionProcessingContext(
                    ExecutionId: executionId,
                    TaskId: task.Id,
                    TaskTitle: task.Title,
                    TaskDescription: task.Description,
                    AcceptanceCriteria: task.AcceptanceCriteria,
                    WorkspaceId: workspace.Id,
                    WorkspaceLocalPath: workspace.LocalPath,
                    ImpactAnalysisSummary: analysis.Summary);

                await _processor.ProcessAsync(context, executionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (executionToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return await HandleCancellationAsync(executionId, leaseToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (await _executionRepository.IsCancellationRequestedAsync(executionId, CancellationToken.None).ConfigureAwait(false))
                {
                    return await HandleCancellationAsync(executionId, leaseToken).ConfigureAwait(false);
                }

                var sanitizedError = SanitizeErrorMessage(ex.Message);
                _logger.LogError(ex,
                    "ProcessExecution: processor failed for execution {ExecutionId}.",
                    executionId);
                await FailExecutionAsync(executionId, leaseToken, sanitizedError, CancellationToken.None).ConfigureAwait(false);
                return new ProcessExecutionResult { Success = false, ErrorMessage = sanitizedError };
            }

            // Check cancellation checkpoint 3
            if (executionToken.IsCancellationRequested || await _executionRepository.IsCancellationRequestedAsync(executionId, CancellationToken.None).ConfigureAwait(false))
            {
                return await HandleCancellationAsync(executionId, leaseToken).ConfigureAwait(false);
            }

            // ── 6. Persist completion with lease fence ────────────────────────────────
            var completed = await _executionRepository.CompleteWithLeaseAsync(executionId, leaseToken, CancellationToken.None).ConfigureAwait(false);
            if (!completed)
            {
                _logger.LogWarning("ProcessExecution: could not complete execution {ExecutionId} because lease {LeaseToken} was expired or changed.", executionId, leaseToken);
                return new ProcessExecutionResult { Success = false, ErrorMessage = "Execution lease lost during completion." };
            }

            // ...THEN record final Execution Completed activity
            await SafeRecordActivityAsync(
                executionId,
                ExecutionStage.Execution,
                ExecutionActivityStatus.Completed,
                "Execution completed.",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "ProcessExecution: execution {ExecutionId} completed successfully.",
                executionId);

            return new ProcessExecutionResult { Success = true };
        }
        finally
        {
            _cancellationRegistry.Unregister(executionId);
        }
    }

    private async Task<ProcessExecutionResult> HandleCancellationAsync(Guid executionId, Guid leaseToken)
    {
        _logger.LogInformation("ProcessExecution: execution {ExecutionId} cancellation acknowledged by worker.", executionId);

        await _executionRepository.AcknowledgeCancellationWithLeaseAsync(executionId, leaseToken, CancellationToken.None).ConfigureAwait(false);

        await SafeRecordActivityAsync(
            executionId,
            ExecutionStage.Execution,
            ExecutionActivityStatus.Completed,
            "Execution cancelled.",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        return new ProcessExecutionResult { Success = false, ErrorMessage = "Execution was cancelled." };
    }

    public static string SanitizeErrorMessage(string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return "Execution failed with an unspecified error.";
        }

        var firstLine = rawMessage.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                        ?? rawMessage.Trim();

        if (firstLine.Length > 500)
        {
            firstLine = firstLine[..500].TrimEnd();
        }

        return firstLine;
    }

    private async Task FailExecutionAsync(
        Guid executionId,
        Guid leaseToken,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var sanitized = SanitizeErrorMessage(errorMessage);

        // First persist TaskExecution / DevelopmentTask failed state with lease fence...
        await _executionRepository.FailWithLeaseAsync(executionId, leaseToken, sanitized, cancellationToken).ConfigureAwait(false);

        // ...THEN record final Execution Failed activity
        await SafeRecordActivityAsync(
            executionId,
            ExecutionStage.Execution,
            ExecutionActivityStatus.Failed,
            $"Execution failed: {sanitized}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task SafeRecordActivityAsync(
        Guid executionId,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        ExecutionActivityMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _activityRecorder.RecordActivityAsync(
                executionId, stage, status, message, metadata, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ProcessExecutionCommandHandler: unexpected error recording activity for execution {ExecutionId}.",
                executionId);
        }
    }
}
