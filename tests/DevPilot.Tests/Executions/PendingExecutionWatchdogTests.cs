using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class PendingExecutionWatchdogTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeClock _clock = new(Now);
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly DbContextOptions<DevPilotDbContext> _options =
        new DbContextOptionsBuilder<DevPilotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private PendingExecutionWatchdog Watchdog()
    {
        var services = new ServiceCollection()
            .AddScoped(_ => new DevPilotDbContext(_options))
            .AddSingleton<IExecutionDispatcher>(_dispatcher)
            .BuildServiceProvider();
        return new PendingExecutionWatchdog(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<PendingExecutionWatchdog>.Instance, _clock);
    }

    private Guid Add(TaskExecutionStatus status, TimeSpan age)
    {
        using var db = new DevPilotDbContext(_options);
        var execution = new TaskExecution { Id = Guid.NewGuid(), DevelopmentTaskId = Guid.NewGuid(), Status = status, CreatedAt = Now - age };
        db.TaskExecutions.Add(execution);
        db.SaveChanges();
        return execution.Id;
    }

    [Fact]
    public async Task A_pending_execution_that_waited_too_long_is_queued_again()
    {
        var stuck = Add(TaskExecutionStatus.Pending, TimeSpan.FromMinutes(10));

        var queued = await Watchdog().RunOnceAsync(CancellationToken.None);

        queued.Should().Be(1);
        _dispatcher.Queued.Should().Equal(stuck);
    }

    [Fact]
    public async Task Recent_pending_and_non_pending_executions_are_left_alone()
    {
        Add(TaskExecutionStatus.Pending, TimeSpan.FromSeconds(30));
        Add(TaskExecutionStatus.Running, TimeSpan.FromHours(2));
        Add(TaskExecutionStatus.Failed, TimeSpan.FromHours(2));

        (await Watchdog().RunOnceAsync(CancellationToken.None)).Should().Be(0);
        _dispatcher.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_execution_is_not_queued_again_before_the_backoff_has_passed()
    {
        var stuck = Add(TaskExecutionStatus.Pending, TimeSpan.FromMinutes(10));
        var watchdog = Watchdog();

        await watchdog.RunOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await watchdog.RunOnceAsync(CancellationToken.None);
        _dispatcher.Queued.Should().Equal(stuck);

        _clock.Advance(PendingExecutionWatchdog.RequeueBackoff);
        await watchdog.RunOnceAsync(CancellationToken.None);
        _dispatcher.Queued.Should().Equal(stuck, stuck);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeClock(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingDispatcher : IExecutionDispatcher
    {
        public List<Guid> Queued { get; } = new();

        public void EnqueueProcessExecution(Guid executionId) => Queued.Add(executionId);

        public void EnqueueVerifyExecution(Guid executionId, Guid leaseToken)
        {
        }
    }
}
