using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// MVP placeholder <see cref="IExecutionProcessor"/> that validates the
/// execution context and logs what a real Developer Agent would do —
/// without touching any repository files, creating branches, committing,
/// pushing, or calling any AI provider.
/// </summary>
/// <remarks>
/// This processor exists solely to exercise the full execution lifecycle
/// end-to-end (Pending → Running → Completed).  Replace this class with a
/// real implementation when the Developer Agent is wired in.
/// </remarks>
public sealed class NoOpExecutionProcessor : IExecutionProcessor
{
    private readonly ILogger<NoOpExecutionProcessor> _logger;

    public NoOpExecutionProcessor(ILogger<NoOpExecutionProcessor> logger)
    {
        _logger = logger;
    }

    public Task ProcessAsync(
        ExecutionProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[NoOp] Processing execution {ExecutionId} for task '{TaskTitle}' ({TaskId}). " +
            "Workspace: '{WorkspacePath}'. " +
            "Impact summary (first 200 chars): {ImpactSummary}. " +
            "No files were modified, no branches created, no AI provider called.",
            context.ExecutionId,
            context.TaskTitle,
            context.TaskId,
            context.WorkspaceLocalPath,
            context.ImpactAnalysisSummary.Length > 200
                ? context.ImpactAnalysisSummary[..200]
                : context.ImpactAnalysisSummary);

        // Simulate a very short synchronous completion — no I/O, no network.
        return Task.CompletedTask;
    }
}
