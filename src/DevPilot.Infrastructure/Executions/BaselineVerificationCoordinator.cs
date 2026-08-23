using System.Collections.Concurrent;
using System.Diagnostics;
using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Singleton coordinator for baseline checks. Maintains thread-safe single-flight execution deduplication,
/// in-memory baseline check result caching across tasks and executions, and per-repository workspace lock concurrency control.
/// </summary>
public sealed class BaselineVerificationCoordinator : IBaselineVerificationCoordinator
{
    private readonly ConcurrentDictionary<BaselineVerificationKey, Task<BaselineCheckEvidence>> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<BaselineVerificationCoordinator> _logger;

    public BaselineVerificationCoordinator(ILogger<BaselineVerificationCoordinator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<(BaselineCheckEvidence? Evidence, bool CacheHit, long DurationMs)> GetOrExecuteAsync(
        BaselineVerificationKey key,
        Func<CancellationToken, Task<BaselineCheckEvidence>> factory,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var cacheHit = true;

        var task = _cache.GetOrAdd(key, _ =>
        {
            cacheHit = false;
            return factory(cancellationToken);
        });

        try
        {
            var evidence = await task.ConfigureAwait(false);
            sw.Stop();
            return (evidence, cacheHit, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // If the baseline execution task faulted or was cancelled, remove from cache so future callers can retry.
            _cache.TryRemove(key, out _);
            sw.Stop();
            _logger.LogWarning(ex, "Baseline check execution failed for key {Key}", key);
            return (null, false, sw.ElapsedMilliseconds);
        }
    }

    public SemaphoreSlim GetWorkspaceLock(string workspaceKey)
    {
        return _workspaceLocks.GetOrAdd(workspaceKey, _ => new SemaphoreSlim(1, 1));
    }
}
