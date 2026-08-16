namespace DevPilot.Application.Executions.Ports;

public sealed record ExecutionWorkspaceResult(
    string WorkspacePath,
    string BranchName,
    bool Success,
    string? ErrorMessage = null);

public interface IExecutionWorkspaceManager
{
    /// <summary>
    /// Prepares an isolated Git worktree workspace and dedicated branch for an execution.
    /// Does not modify the original managed repository checkout or branch.
    /// </summary>
    Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(
        Guid executionId,
        Guid taskId,
        string sourceRepositoryLocalPath,
        string? sourceBranch = null,
        CancellationToken cancellationToken = default);
}
