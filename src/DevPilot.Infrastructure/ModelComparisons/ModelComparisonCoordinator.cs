using DevPilot.Application.ModelComparisons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.ModelComparisons;

/// <summary>
/// Polls open model comparisons and starts the next run as soon as the previous execution has ended.
/// Polling (rather than hooking every place an execution can finish) keeps this correct after
/// crashes, restarts and cancellations: the database is the only source of truth.
/// </summary>
internal sealed class ModelComparisonCoordinator : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ModelComparisonCoordinator> _logger;

    public ModelComparisonCoordinator(IServiceScopeFactory scopeFactory, ILogger<ModelComparisonCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IModelComparisonService>();
                    await service.AdvanceAllAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Typically the database is unreachable or not migrated yet; keep trying quietly.
                    _logger.LogWarning(ex, "Model comparison coordinator tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
