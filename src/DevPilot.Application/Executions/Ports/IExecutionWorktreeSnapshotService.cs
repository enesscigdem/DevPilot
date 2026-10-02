namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Records the exact content of an execution's worktree as a git commit that is not on any branch, so the change a
/// requested fix made can be shown later as the difference between two snapshots.
/// </summary>
public interface IExecutionWorktreeSnapshotService
{
    /// <summary>
    /// Returns the SHA of a pinned snapshot commit of the current worktree (tracked and untracked, ignored files
    /// excluded), or null when it could not be taken. Never changes the branch, index or worktree.
    /// </summary>
    Task<string?> CaptureAsync(
        string workspacePath,
        Guid executionId,
        string label,
        CancellationToken cancellationToken = default);
}
