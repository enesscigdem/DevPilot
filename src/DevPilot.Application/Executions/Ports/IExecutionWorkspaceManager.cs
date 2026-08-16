namespace DevPilot.Application.Executions.Ports;

public sealed record ExecutionWorkspaceResult(
    string WorkspacePath,
    string BranchName,
    bool Success,
    string? ErrorMessage = null);

public sealed record WorkspaceVerificationResult(
    bool IsValid,
    bool WorkspaceExists,
    bool BranchMatches,
    bool IsClean,
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

    /// <summary>
    /// Verifies that an execution workspace exists on disk, is checked out on the expected branch,
    /// and optionally has a clean worktree (no uncommitted or untracked changes).
    /// </summary>
    Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(
        string workspacePath,
        string expectedBranchName,
        bool requireClean = true,
        CancellationToken cancellationToken = default);
}
