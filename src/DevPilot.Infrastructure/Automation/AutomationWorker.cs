using DevPilot.Application.Automation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Automation;

/// <summary>
/// Polls the workspaces that have automation switched on and lets the orchestrator move their work forward.
/// Like the model comparison coordinator it polls instead of hooking every place an execution can change state,
/// so it stays correct after crashes and restarts: the database is the only source of truth.
/// </summary>
internal sealed class AutomationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutomationWorker> _logger;
    private readonly TimeSpan _interval;

    public AutomationWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<AutomationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue<int?>("Automation:PollSeconds") ?? 20));
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
            using var listScope = _scopeFactory.CreateScope();
            var policies = await listScope.ServiceProvider
                .GetRequiredService<IAutomationPolicyStore>()
                .ListActiveAsync(stoppingToken)
                .ConfigureAwait(false);

            foreach (var policy in policies)
            {
                // One scope per workspace: a failure or a stale DbContext in one never affects the others.
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider
                        .GetRequiredService<IAutomationOrchestrator>()
                        .AdvanceAsync(policy, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Automation tick failed for workspace {WorkspaceId}.", policy.RepositoryWorkspaceId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Typically the database is unreachable or not migrated yet; keep trying quietly.
            _logger.LogWarning(ex, "Automation worker tick failed.");
        }
    }
}
