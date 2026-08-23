using DevPilot.Application.Executions.Ports;

namespace DevPilot.Infrastructure.Executions;

public interface IBaselineVerificationCoordinator
{
    Task<(BaselineCheckEvidence? Evidence, bool CacheHit, long DurationMs)> GetOrExecuteAsync(
        BaselineVerificationKey key,
        Func<CancellationToken, Task<BaselineCheckEvidence>> factory,
        CancellationToken cancellationToken = default);

    SemaphoreSlim GetWorkspaceLock(string workspaceKey);
}
