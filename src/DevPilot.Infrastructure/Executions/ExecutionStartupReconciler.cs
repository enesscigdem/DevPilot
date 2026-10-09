using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Background service that reconciles stale Running executions whose workers crashed or stopped without releasing their
/// lease. It runs once at startup and then keeps checking: a lease that was still valid when the process started expires
/// later, and a worker can die at any time, not only before a restart.
/// </summary>
public sealed class ExecutionStartupReconciler : BackgroundService
{
    /// <summary>How often leases are checked. Short against the 45 second lease, long against the cost of the query.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExecutionStartupReconciler> _logger;

    public ExecutionStartupReconciler(
        IServiceScopeFactory scopeFactory,
        ILogger<ExecutionStartupReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass. Never throws: a failed check must not stop the checks that follow.</summary>
    public async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();

            // Cutoff for legacy running executions without lease timestamps: 5 minutes prior
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            var reconciled = await repo.ReconcileStaleRunningExecutionsAsync(cutoff, cancellationToken).ConfigureAwait(false);

            if (reconciled > 0)
            {
                _logger.LogInformation("ExecutionStartupReconciler: reconciled {Count} stale running execution(s).", reconciled);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecutionStartupReconciler: error during execution reconciliation.");
        }
    }
}
