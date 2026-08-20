using System.Collections.Concurrent;
using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class ExecutionCancellationRegistry : IExecutionCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _registry = new();
    private readonly ILogger<ExecutionCancellationRegistry> _logger;

    public ExecutionCancellationRegistry(ILogger<ExecutionCancellationRegistry> logger)
    {
        _logger = logger;
    }

    public CancellationToken Register(Guid executionId, CancellationToken parentToken = default)
    {
        var cts = parentToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentToken)
            : new CancellationTokenSource();

        _registry.AddOrUpdate(
            executionId,
            cts,
            (_, existing) =>
            {
                try { existing.Cancel(); existing.Dispose(); } catch { }
                return cts;
            });

        return cts.Token;
    }

    public bool TryCancel(Guid executionId)
    {
        if (_registry.TryGetValue(executionId, out var cts))
        {
            try
            {
                cts.Cancel();
                _logger.LogInformation("ExecutionCancellationRegistry: triggered in-memory cancellation for execution {ExecutionId}.", executionId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ExecutionCancellationRegistry: failed to cancel CTS for execution {ExecutionId}.", executionId);
            }
        }

        return false;
    }

    public void Unregister(Guid executionId)
    {
        if (_registry.TryRemove(executionId, out var cts))
        {
            try { cts.Dispose(); } catch { }
        }
    }
}
