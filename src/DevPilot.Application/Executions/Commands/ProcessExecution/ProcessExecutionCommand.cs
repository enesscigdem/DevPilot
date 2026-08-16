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
    private readonly ILogger<ProcessExecutionCommandHandler> _logger;

    public ProcessExecutionCommandHandler(
        IExecutionRepository executionRepository,
        IImpactAnalysisRepository impactAnalysisRepository,
        IExecutionProcessor processor,
        ILogger<ProcessExecutionCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _impactAnalysisRepository = impactAnalysisRepository;
        _processor = processor;
        _logger = logger;
    }

    public async Task<ProcessExecutionResult> HandleAsync(
        ProcessExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        var executionId = command.ExecutionId;

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

        // ── 2. Atomic claim: Pending → Running (idempotency guard) ───────────────
        var claimed = await _executionRepository
            .ClaimAsRunningAsync(executionId, cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            _logger.LogInformation(
                "ProcessExecution: execution {ExecutionId} is not Pending (status={Status}) — skipping (idempotent).",
                executionId,
                execution.Status);
            return new ProcessExecutionResult { Success = true, Skipped = true };
        }

        _logger.LogInformation(
            "ProcessExecution: execution {ExecutionId} claimed as Running for task '{TaskTitle}' ({TaskId}).",
            executionId,
            task.Title,
            task.Id);

        // ── 3. Verify workspace local path ────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(workspace.LocalPath))
        {
            const string pathError = "RepositoryWorkspace has no local path configured.";
            _logger.LogError(
                "ProcessExecution: {Error} WorkspaceId={WorkspaceId}",
                pathError,
                workspace.Id);
            await FailExecutionAsync(executionId, pathError, cancellationToken).ConfigureAwait(false);
            return new ProcessExecutionResult { Success = false, ErrorMessage = pathError };
        }

        if (!Directory.Exists(workspace.LocalPath))
        {
            var pathError = $"Workspace local path does not exist on disk: '{workspace.LocalPath}'.";
            _logger.LogError(
                "ProcessExecution: {Error} WorkspaceId={WorkspaceId}",
                pathError,
                workspace.Id);
            await FailExecutionAsync(executionId, pathError, cancellationToken).ConfigureAwait(false);
            return new ProcessExecutionResult { Success = false, ErrorMessage = pathError };
        }

        // ── 4. Load completed impact analysis ─────────────────────────────────────
        var analysis = await _impactAnalysisRepository
            .GetLatestByTaskIdAsync(task.Id, cancellationToken)
            .ConfigureAwait(false);

        if (analysis is null || analysis.Status != ImpactAnalysisStatus.Completed)
        {
            const string analysisError = "No completed impact analysis found for the task.";
            _logger.LogError(
                "ProcessExecution: {Error} TaskId={TaskId}",
                analysisError,
                task.Id);
            await FailExecutionAsync(executionId, analysisError, cancellationToken).ConfigureAwait(false);
            return new ProcessExecutionResult { Success = false, ErrorMessage = analysisError };
        }

        // ── 5. Execute via processor (no-op in MVP) ───────────────────────────────
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

            await _processor.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var processorError = $"Processor raised an exception: {ex.Message}";
            _logger.LogError(ex,
                "ProcessExecution: processor failed for execution {ExecutionId}.",
                executionId);
            await FailExecutionAsync(executionId, processorError, cancellationToken).ConfigureAwait(false);
            return new ProcessExecutionResult { Success = false, ErrorMessage = processorError };
        }

        // ── 6. Persist completion ─────────────────────────────────────────────────
        await _executionRepository.CompleteAsync(executionId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "ProcessExecution: execution {ExecutionId} completed successfully.",
            executionId);

        return new ProcessExecutionResult { Success = true };
    }

    private Task FailExecutionAsync(
        Guid executionId,
        string errorMessage,
        CancellationToken cancellationToken) =>
        _executionRepository.FailAsync(executionId, errorMessage, cancellationToken);
}
