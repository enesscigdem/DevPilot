using DevPilot.Application.Automation;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Commands.PushExecution;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Automation;
using DevPilot.Tests.Executions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Automation;

public class AutomationDeliveryRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ALeaseClaimedAMomentAgo_IsStillRespected()
    {
        AutomationDeliveryRules.IsLeaseStale(Now.AddSeconds(-20), Now).Should().BeFalse();
        AutomationDeliveryRules.IsLeaseStale(Now.AddMinutes(-1), Now).Should().BeFalse();
    }

    [Fact]
    public void ALeaseOlderThanTheCommandLeaseTimeout_IsStale()
    {
        AutomationDeliveryRules.IsLeaseStale(Now - AutomationDeliveryRules.StaleLeaseAfter, Now).Should().BeTrue();
        AutomationDeliveryRules.IsLeaseStale(Now.AddMinutes(-30), Now).Should().BeTrue();
    }

    [Fact]
    public void ALeaseWithoutAClaimTime_IsNotTreatedAsStale()
    {
        AutomationDeliveryRules.IsLeaseStale(null, Now).Should().BeFalse();
    }

    [Fact]
    public void TheStaleThresholdIsTheOneTheCommitAndPushCommandsEnforce()
    {
        AutomationDeliveryRules.StaleLeaseAfter.Should().Be(TimeSpan.FromMinutes(2));
    }
}

public class StuckDeliveryQueryTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly RepositoryWorkspace _workspace;
    private readonly EfAutomationWorkReader _reader;
    private readonly DateTime _since = DateTime.UtcNow.AddDays(-1);

    public StuckDeliveryQueryTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new DevPilotDbContext(options);
        _workspace = new RepositoryWorkspace { Id = Guid.NewGuid(), Owner = "o", Repository = "r", Branch = "main" };
        _db.RepositoryWorkspaces.Add(_workspace);
        _db.SaveChanges();
        _reader = new EfAutomationWorkReader(_db);
    }

    public void Dispose() => _db.Dispose();

    private TaskExecution Add(Action<TaskExecution> configure)
    {
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = _workspace.Id,
            Title = "t",
            Status = DevelopmentTaskStatus.Completed,
            CreatedAt = _since.AddHours(1),
        };
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            CreatedAt = _since.AddHours(2),
        };
        configure(execution);
        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(execution);
        _db.SaveChanges();
        return execution;
    }

    private Task<IReadOnlyList<Guid>> Deliverable() => _reader.GetDeliverableExecutionIdsAsync(_workspace.Id, _since);

    [Fact]
    public async Task ACommitWhoseLeaseWentStale_IsPickedUpAgain()
    {
        var stuck = Add(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.InProgress;
            e.CommitClaimedAt = DateTime.UtcNow.AddMinutes(-10);
        });

        (await Deliverable()).Should().Equal(stuck.Id);
    }

    [Fact]
    public async Task ACommitThatIsStillRunning_IsLeftAlone()
    {
        Add(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.InProgress;
            e.CommitClaimedAt = DateTime.UtcNow.AddSeconds(-15);
        });

        (await Deliverable()).Should().BeEmpty();
    }

    [Fact]
    public async Task APushWhoseLeaseWentStale_IsPickedUpAgain()
    {
        var stuck = Add(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.Committed;
            e.PushStatus = ExecutionPushStatus.InProgress;
            e.PushClaimedAt = DateTime.UtcNow.AddMinutes(-10);
        });

        (await Deliverable()).Should().Equal(stuck.Id);
    }

    [Fact]
    public async Task APushThatIsStillRunning_IsLeftAlone()
    {
        Add(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.Committed;
            e.PushStatus = ExecutionPushStatus.InProgress;
            e.PushClaimedAt = DateTime.UtcNow.AddSeconds(-15);
        });

        (await Deliverable()).Should().BeEmpty();
    }

    [Fact]
    public async Task APullRequestWhoseLeaseWentStale_IsPickedUpAgain()
    {
        var stuck = Add(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.Committed;
            e.PushStatus = ExecutionPushStatus.Pushed;
            e.PullRequestStatus = ExecutionPullRequestStatus.InProgress;
            e.PullRequestClaimedAt = DateTime.UtcNow.AddMinutes(-10);
        });

        (await Deliverable()).Should().Equal(stuck.Id);
    }

    [Fact]
    public async Task APullRequestThatIsStillBeingOpened_IsLeftAlone()
    {
        Add(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.Committed;
            e.PushStatus = ExecutionPushStatus.Pushed;
            e.PullRequestStatus = ExecutionPullRequestStatus.InProgress;
            e.PullRequestClaimedAt = DateTime.UtcNow.AddSeconds(-15);
        });

        (await Deliverable()).Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedCommitIsStillNotRetriedAutomatically()
    {
        Add(e => e.CommitStatus = ExecutionCommitStatus.Failed);

        (await Deliverable()).Should().BeEmpty();
    }

    [Fact]
    public async Task ARunThatNeverStartedDelivering_IsStillReturned()
    {
        var fresh = Add(_ => { });

        (await Deliverable()).Should().Equal(fresh.Id);
    }
}

public class StuckDeliveryOrchestrationTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly InMemoryExecutionRepository _executions = new();
    private readonly FakeWork _work = new();
    private readonly FakeCommit _commit = new();
    private readonly FakePush _push = new();

    private AutomationOrchestrator Orchestrator() =>
        new(
            _work, null!, null!, _executions, null!, new NullRecorder(), null!, null!, null!,
            _commit, _push, null!, null!, null!,
            new AutomationDecisionLedger(), NullLogger<AutomationOrchestrator>.Instance);

    private AutomationPolicy Policy() => new()
    {
        RepositoryWorkspaceId = _workspaceId,
        Level = AutomationLevel.AutoPr,
        ActiveSince = DateTime.UtcNow.AddDays(-1),
    };

    private TaskExecution Stuck(Action<TaskExecution> configure)
    {
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = Guid.NewGuid(),
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
        };
        configure(execution);
        _executions.Executions[execution.Id] = execution;
        _work.Deliverable.Add(execution.Id);
        return execution;
    }

    [Fact]
    public async Task ACommitStuckInProgress_IsHandedBackToTheCommitStepSoItCanRecover()
    {
        var run = Stuck(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.InProgress;
            e.CommitClaimedAt = DateTime.UtcNow.AddMinutes(-9);
        });

        await Orchestrator().AdvanceAsync(Policy());

        _commit.Commands.Should().ContainSingle().Which.ExecutionId.Should().Be(run.Id);
    }

    [Fact]
    public async Task AFreshCommitLease_IsNeverTouched()
    {
        Stuck(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.InProgress;
            e.CommitClaimedAt = DateTime.UtcNow.AddSeconds(-5);
        });

        await Orchestrator().AdvanceAsync(Policy());

        _commit.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task APushStuckInProgress_IsHandedBackToThePushStep()
    {
        var run = Stuck(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.Committed;
            e.PushStatus = ExecutionPushStatus.InProgress;
            e.PushClaimedAt = DateTime.UtcNow.AddMinutes(-9);
        });

        await Orchestrator().AdvanceAsync(Policy());

        _push.Commands.Should().ContainSingle().Which.ExecutionId.Should().Be(run.Id);
        _commit.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task AFreshPushLease_IsNeverTouched()
    {
        Stuck(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.Committed;
            e.PushStatus = ExecutionPushStatus.InProgress;
            e.PushClaimedAt = DateTime.UtcNow.AddSeconds(-5);
        });

        await Orchestrator().AdvanceAsync(Policy());

        _push.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ACommitThatRecoversAndMarksTheRecordCommitted_ContinuesToThePush()
    {
        var run = Stuck(e =>
        {
            e.CommitStatus = ExecutionCommitStatus.InProgress;
            e.CommitClaimedAt = DateTime.UtcNow.AddMinutes(-9);
        });
        _commit.OnHandle = e =>
        {
            e.CommitStatus = ExecutionCommitStatus.Committed;
            e.CommitSha = "abc";
        };

        await Orchestrator().AdvanceAsync(Policy());

        _commit.Commands.Should().HaveCount(1);
        _push.Commands.Should().ContainSingle().Which.ExecutionId.Should().Be(run.Id);
    }

    private sealed class FakeWork : IAutomationWorkReader
    {
        public List<Guid> Deliverable { get; } = new();

        public Task<IReadOnlyList<DevelopmentTask>> GetTasksReadyToStartAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentTask>>(Array.Empty<DevelopmentTask>());

        public Task<int> CountActiveExecutionsAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) => Task.FromResult(0);
public Task<int> CountExecutionsForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<Guid>> GetRetryableFailedExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task<IReadOnlyList<Guid>> GetDeliverableExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Deliverable.ToList());

        public Task<IReadOnlyList<Guid>> GetOpenPullRequestExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
    }

    private sealed class FakeCommit : ICommitExecutionCommandHandler
    {
        public List<CommitExecutionCommand> Commands { get; } = new();

        public Action<TaskExecution>? OnHandle { get; set; }

        public Task<CommitExecutionResult> HandleAsync(CommitExecutionCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            OnHandle?.Invoke(_executionsLookup(command.ExecutionId));
            return Task.FromResult(CommitExecutionResult.Ok(new CommitExecutionResponseDto(command.ExecutionId, "b", "Committed", "abc", DateTime.UtcNow)));
        }

        private Func<Guid, TaskExecution> _executionsLookup = _ => new TaskExecution();

        public void Bind(InMemoryExecutionRepository repo) => _executionsLookup = id => repo.Executions[id];
    }

    private sealed class FakePush : IPushExecutionCommandHandler
    {
        public List<PushExecutionCommand> Commands { get; } = new();

        public Task<PushExecutionResult> HandleAsync(PushExecutionCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(PushExecutionResult.Conflict("stop here"));
        }
    }

    private sealed class NullRecorder : IExecutionActivityRecorder
    {
        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public StuckDeliveryOrchestrationTests()
    {
        _commit.Bind(_executions);
    }
}
