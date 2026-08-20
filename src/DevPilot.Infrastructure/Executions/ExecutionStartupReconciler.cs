using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Background hosted service that runs once on startup to reconcile stale Running executions
/// whose workers crashed or stopped unexpectedly without releasing their lease.
/// </summary>
public sealed class ExecutionStartupReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExecutionStartupReconciler> _logger;

    public ExecutionStartupReconciler(
        IServiceScopeFactory scopeFactory,
        ILogger<ExecutionStartupReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("ExecutionStartupReconciler: checking for stale running executions from prior worker instances...");

            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();

            // Cutoff for legacy running executions without lease timestamps: 5 minutes prior
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            var reconciled = await repo.ReconcileStaleRunningExecutionsAsync(cutoff, cancellationToken).ConfigureAwait(false);

            if (reconciled > 0)
            {
                _logger.LogInformation("ExecutionStartupReconciler: safely reconciled {Count} stale running execution(s) to Failed.", reconciled);
            }
            else
            {
                _logger.LogInformation("ExecutionStartupReconciler: no stale running executions found.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecutionStartupReconciler: error during startup execution reconciliation.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
