using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class ExecutionHeartbeatService : IExecutionHeartbeatService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExecutionHeartbeatService> _logger;

    public ExecutionHeartbeatService(
        IServiceScopeFactory scopeFactory,
        ILogger<ExecutionHeartbeatService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public IAsyncDisposable StartHeartbeat(
        Guid executionId,
        Guid leaseToken,
        TimeSpan interval,
        TimeSpan leaseDuration,
        CancellationTokenSource linkedCts)
    {
        return new HeartbeatSession(
            _scopeFactory,
            _logger,
            executionId,
            leaseToken,
            interval,
            leaseDuration,
            linkedCts);
    }

    private sealed class HeartbeatSession : IAsyncDisposable
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger _logger;
        private readonly Guid _executionId;
        private readonly Guid _leaseToken;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _leaseDuration;
        private readonly CancellationTokenSource _linkedCts;
        private readonly CancellationTokenSource _sessionCts;
        private readonly Task _heartbeatLoop;

        public HeartbeatSession(
            IServiceScopeFactory scopeFactory,
            ILogger logger,
            Guid executionId,
            Guid leaseToken,
            TimeSpan interval,
            TimeSpan leaseDuration,
            CancellationTokenSource linkedCts)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _executionId = executionId;
            _leaseToken = leaseToken;
            _interval = interval;
            _leaseDuration = leaseDuration;
            _linkedCts = linkedCts;
            _sessionCts = new CancellationTokenSource();

            _heartbeatLoop = Task.Run(RunLoopAsync);
        }

        private async Task RunLoopAsync()
        {
            _logger.LogDebug(
                "ExecutionHeartbeatSession: started heartbeat for execution {ExecutionId} with lease {LeaseToken} (Interval: {Interval}s, Duration: {Duration}s).",
                _executionId,
                _leaseToken,
                _interval.TotalSeconds,
                _leaseDuration.TotalSeconds);

            using var timer = new PeriodicTimer(_interval);

            try
            {
                while (await timer.WaitForNextTickAsync(_sessionCts.Token).ConfigureAwait(false))
                {
                    if (_sessionCts.IsCancellationRequested) break;

                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var repo = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();

                        var renewed = await repo.RenewHeartbeatAsync(
                            _executionId,
                            _leaseToken,
                            _leaseDuration,
                            _sessionCts.Token).ConfigureAwait(false);

                        if (!renewed)
                        {
                            _logger.LogWarning(
                                "ExecutionHeartbeatSession: lease renewal returned false for execution {ExecutionId} (lease {LeaseToken} may have expired or changed). Triggering cancellation.",
                                _executionId,
                                _leaseToken);

                            _linkedCts.Cancel();
                            break;
                        }

                        _logger.LogTrace("ExecutionHeartbeatSession: successfully renewed lease for execution {ExecutionId}.", _executionId);
                    }
                    catch (OperationCanceledException) when (_sessionCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ExecutionHeartbeatSession: transient error renewing lease for execution {ExecutionId}.", _executionId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown on disposal
            }
        }

        public async ValueTask DisposeAsync()
        {
            _sessionCts.Cancel();
            try
            {
                await _heartbeatLoop.ConfigureAwait(false);
            }
            catch
            {
                // ignore cancellation exceptions
            }
            finally
            {
                _sessionCts.Dispose();
            }
        }
    }
}
