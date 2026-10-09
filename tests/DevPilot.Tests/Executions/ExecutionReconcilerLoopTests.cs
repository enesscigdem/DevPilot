using DevPilot.Application.Executions.Ports;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class ExecutionReconcilerLoopTests
{
    [Fact]
    public async Task Repeated_passes_do_not_throw()
    {
        var repo = new InMemoryExecutionRepository();
        var services = new ServiceCollection().AddSingleton<IExecutionRepository>(repo).BuildServiceProvider();
        var reconciler = new ExecutionStartupReconciler(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExecutionStartupReconciler>.Instance);

        var act = async () =>
        {
            await reconciler.ReconcileOnceAsync(CancellationToken.None);
            await reconciler.ReconcileOnceAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_pass_with_no_repository_registered_does_not_throw()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var reconciler = new ExecutionStartupReconciler(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExecutionStartupReconciler>.Instance);

        await reconciler.Invoking(r => r.ReconcileOnceAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_service_checks_at_startup_and_stops_when_asked()
    {
        var repo = new CountingRepository();
        var services = new ServiceCollection().AddSingleton<IExecutionRepository>(repo).BuildServiceProvider();
        var reconciler = new ExecutionStartupReconciler(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExecutionStartupReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);
        SpinWait.SpinUntil(() => repo.Passes >= 1, TimeSpan.FromSeconds(5)).Should().BeTrue();
        await reconciler.StopAsync(CancellationToken.None);

        repo.Passes.Should().BeGreaterThanOrEqualTo(1);
    }

    private sealed class CountingRepository : InMemoryExecutionRepository
    {
        public int Passes;

        public override Task<int> ReconcileStaleRunningExecutionsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Passes);
            return Task.FromResult(0);
        }
    }
}
