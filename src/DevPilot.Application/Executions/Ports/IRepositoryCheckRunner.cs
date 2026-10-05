using DevPilot.Application.Executions.Models;

namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Discovers deterministic repository-owned verification checks and executes only those checks
/// inside the controlled execution worktree boundary.
/// </summary>
public interface IRepositoryCheckRunner
{
    Task<RepositoryProfile> DiscoverAsync(
        RepositoryPreflightRequest request,
        CancellationToken cancellationToken = default);

    Task<RepositoryCheckResult> ExecuteAsync(
        RepositoryCheckExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort early preparation of what a check needs before it runs (e.g. installing dependencies), so the work can
    /// overlap with change generation. Never throws for a failed preparation: it returns null and leaves the workspace
    /// as it was, and the check later prepares itself exactly as before. Returns a token describing the manifests the
    /// preparation was based on, to be passed to <see cref="EnsureEnvironmentFreshAsync"/>.
    /// </summary>
    Task<string?> PrepareEnvironmentAsync(
        RepositoryPreflightRequest request,
        RepositoryCheck check,
        CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    /// <summary>
    /// Re-prepares the environment when the manifests changed after <see cref="PrepareEnvironmentAsync"/> (for example the
    /// generated change edited package.json), so a stale preparation is never used.
    /// </summary>
    Task EnsureEnvironmentFreshAsync(
        RepositoryPreflightRequest request,
        RepositoryCheck check,
        string? preparedToken,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
