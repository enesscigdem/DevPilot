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
}
