using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// An execution is saved as Pending first and its job is queued afterwards, in a separate step. If the process dies in
/// between, the execution waits forever and its unique active-run index blocks every new start. This service finds
/// executions that stayed Pending too long and queues their job again. That is safe because the worker claims
/// Pending to Running atomically: a second job for the same execution finds it claimed and exits without doing anything.
/// </summary>
public sealed class PendingExecutionWatchdog : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>A job normally starts within seconds; this long without being claimed means it was probably never queued.</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(3);

    /// <summary>Do not queue the same execution again before this has passed, so a busy queue is not flooded.</summary>
    public static readonly TimeSpan RequeueBackoff = TimeSpan.FromMinutes(5);

    private const int MaxPerPass = 20;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<PendingExecutionWatchdog> _logger;
    private readonly Dictionary<Guid, DateTime> _lastQueued = new();

    public PendingExecutionWatchdog(
        IServiceScopeFactory scopeFactory,
        ILogger<PendingExecutionWatchdog> logger,
        TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass. Returns how many executions were queued again. Never throws except on cancellation.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DevPilotDbContext>();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IExecutionDispatcher>();

            var now = _time.GetUtcNow().UtcDateTime;
            var waiting = await db.TaskExecutions
                .AsNoTracking()
                .Where(e => e.Status == TaskExecutionStatus.Pending && e.CreatedAt < now - PendingGrace)
                .OrderBy(e => e.CreatedAt)
                .Select(e => e.Id)
                .Take(MaxPerPass)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Forget executions that are no longer waiting so the memory does not grow.
            foreach (var gone in _lastQueued.Keys.Where(id => !waiting.Contains(id)).ToList())
            {
                _lastQueued.Remove(gone);
            }

            var queued = 0;
            foreach (var id in waiting)
            {
                if (_lastQueued.TryGetValue(id, out var last) && now - last < RequeueBackoff)
                {
                    continue;
                }

                dispatcher.EnqueueProcessExecution(id);
                _lastQueued[id] = now;
                queued++;
                _logger.LogWarning("PendingExecutionWatchdog: execution {ExecutionId} was still Pending; queued its job again.", id);
            }

            return queued;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PendingExecutionWatchdog: error while checking pending executions.");
            return 0;
        }
    }
}
