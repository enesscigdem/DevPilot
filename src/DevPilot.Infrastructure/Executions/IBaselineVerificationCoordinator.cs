using DevPilot.Application.Executions.Ports;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Thread-safe singleton coordinator that maintains cross-execution cached baseline evidence,
/// single-flight request deduplication, and workspace synchronization locks.
/// </summary>
public interface IBaselineVerificationCoordinator
{
    Task<(BaselineCheckEvidence? Evidence, bool CacheHit, long DurationMs)> GetOrExecuteAsync(
        BaselineVerificationKey key,
        Func<CancellationToken, Task<BaselineCheckEvidence>> factory,
        CancellationToken cancellationToken = default);

    SemaphoreSlim GetWorkspaceLock(string workspaceKey);
}
