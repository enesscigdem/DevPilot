using DevPilot.Application.Goals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Goals;

/// <summary>
/// Moves every active goal forward every few seconds. Like the other coordinators it polls the database, the
/// only source of truth, so it stays correct after crashes, restarts and cancellations.
/// </summary>
internal sealed class GoalWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GoalWorker> _logger;
    private readonly TimeSpan _interval;

    public GoalWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<GoalWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue<int?>("Goals:PollSeconds") ?? 10));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            IReadOnlyList<Guid> ids;
            using (var listScope = _scopeFactory.CreateScope())
            {
                ids = await listScope.ServiceProvider.GetRequiredService<IGoalStore>()
                    .ListActiveIdsAsync(stoppingToken).ConfigureAwait(false);
            }

            foreach (var id in ids)
            {
                // One scope per goal: a failure in one never affects the others.
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IGoalOrchestrator>()
                        .AdvanceAsync(id, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Goal tick failed for goal {GoalId}.", id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Typically the database is unreachable or not migrated yet; keep trying quietly.
            _logger.LogWarning(ex, "Goal worker tick failed.");
        }
    }
}
