namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Performs the actual developer-agent work for a single execution.
/// In the MVP this is a no-op placeholder.  Future iterations will
/// implement real AI-driven code modifications here.
/// </summary>
/// <remarks>
/// Implementations MUST NOT modify repository source files, create
/// branches, commit, push, or call any AI provider until the full
/// Developer Agent is wired in.
/// </remarks>
public interface IExecutionProcessor
{
    /// <summary>
    /// Executes the work for the given execution context.
    /// Throw an exception to signal failure; return normally to signal success.
    /// </summary>
    Task ProcessAsync(
        ExecutionProcessingContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable context passed to <see cref="IExecutionProcessor"/> containing
/// all data loaded by the orchestrator before processing begins.
/// </summary>
public sealed record ExecutionProcessingContext(
    Guid ExecutionId,
    Guid TaskId,
    string TaskTitle,
    string TaskDescription,
    string? AcceptanceCriteria,
    Guid WorkspaceId,
    string WorkspaceLocalPath,
    string ImpactAnalysisSummary);
