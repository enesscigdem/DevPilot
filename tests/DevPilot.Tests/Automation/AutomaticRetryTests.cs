using DevPilot.Application.Automation;
using DevPilot.Application.Executions.Commands.RetryExecution;
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

public class AutomaticRetryRulesTests
{
    private static ExecutionActivity Act(ExecutionStage stage, ExecutionActivityStatus status) =>
        new() { Id = Guid.NewGuid(), Stage = stage, Status = status, Message = "m" };

    [Theory]
    [InlineData(ExecutionVerificationOutcome.NeedsReview, 3, true)]
    [InlineData(ExecutionVerificationOutcome.Failed, 3, true)]
    [InlineData(ExecutionVerificationOutcome.VerificationUnavailable, 3, true)]
    [InlineData(ExecutionVerificationOutcome.PartiallyVerified, 0, true)]
    [InlineData(ExecutionVerificationOutcome.PartiallyVerified, 3, false)]
    [InlineData(ExecutionVerificationOutcome.Verified, 3, false)]
    public void BrokenOrEmptyRun_IsRestartable_PolicyBlocksAreNot(ExecutionVerificationOutcome outcome, int files, bool expected)
    {
        AutomationRetryRules.IsRestartableOutcome(outcome, files).Should().Be(expected);
    }

    [Fact]
    public void FailureWhileWritingCode_IsAGenerationFailure()
    {
        var activities = new[]
        {
            Act(ExecutionStage.Workspace, ExecutionActivityStatus.Completed),
            Act(ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed),
            Act(ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Failed),
            Act(ExecutionStage.Execution, ExecutionActivityStatus.Failed),
        };

        AutomationRetryRules.IsGenerationFailure(activities).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExecutionStage.Build)]
    [InlineData(ExecutionStage.Test)]
    public void FailureAfterVerificationStarted_IsNotRetried(ExecutionStage reached)
    {
        var activities = new[]
        {
            Act(ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Failed),
            Act(reached, ExecutionActivityStatus.Started),
        };

        AutomationRetryRules.IsGenerationFailure(activities).Should().BeFalse();
    }

    [Fact]
    public void FailureThatNeverTouchedTheDeveloperAgent_IsNotRetried()
    {
        var activities = new[]
        {
            Act(ExecutionStage.Workspace, ExecutionActivityStatus.Failed),
            Act(ExecutionStage.Execution, ExecutionActivityStatus.Failed),
        };

        AutomationRetryRules.IsGenerationFailure(activities).Should().BeFalse();
    }

    [Fact]
    public void NoActivities_IsNotAGenerationFailure()
    {
        AutomationRetryRules.IsGenerationFailure(Array.Empty<ExecutionActivity>()).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(5, false)]
    public void ARetryIsOnlyLeftWhileTheTaskHasRunFewerTimesThanTheLimit(int executionsSoFar, bool expected)
    {
        AutomationRetryRules.HasRetryLeft(executionsSoFar).Should().Be(expected);
        AutomationRetryRules.MaxExecutionsPerTask.Should().Be(2);
    }
}

public class RetryableFailureQueryTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly RepositoryWorkspace _workspace;
    private readonly EfAutomationWorkReader _reader;
    private readonly DateTime _since = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    public RetryableFailureQueryTests()
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

    private DevelopmentTask AddTask(DevelopmentTaskStatus status, DateTime? createdAt = null, bool inGoal = false)
    {
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = _workspace.Id,
            Title = "t",
            Status = status,
            CreatedAt = createdAt ?? _since.AddHours(1),
        };
        _db.DevelopmentTasks.Add(task);
        if (inGoal)
        {
            var goal = new Goal { Id = Guid.NewGuid(), RepositoryWorkspaceId = _workspace.Id, Title = "g", CreatedAt = _since };
            _db.Goals.Add(goal);
            _db.GoalTasks.Add(new GoalTask { Id = Guid.NewGuid(), GoalId = goal.Id, DevelopmentTaskId = task.Id, Key = "t1" });
        }

        _db.SaveChanges();
        return task;
    }

    private TaskExecution AddExecution(DevelopmentTask task, TaskExecutionStatus status, int minutesAfterTask = 1)
    {
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = status,
            CreatedAt = task.CreatedAt.AddMinutes(minutesAfterTask),
        };
        _db.TaskExecutions.Add(execution);
        _db.SaveChanges();
        return execution;
    }

    private Task<IReadOnlyList<Guid>> Query() => _reader.GetRetryableFailedExecutionIdsAsync(_workspace.Id, _since);

    [Fact]
    public async Task AFailedFirstRun_IsReturned()
    {
        var task = AddTask(DevelopmentTaskStatus.Failed);
        var run = AddExecution(task, TaskExecutionStatus.Failed);

        (await Query()).Should().Equal(run.Id);
    }

    [Fact]
    public async Task AGoalTaskIsIncluded_BecauseTheGoalOnlyStartsItAndNeverRetriesIt()
    {
        var task = AddTask(DevelopmentTaskStatus.Failed, inGoal: true);
        var run = AddExecution(task, TaskExecutionStatus.Failed);

        (await Query()).Should().Equal(run.Id);
    }

    [Fact]
    public async Task ATaskThatAlreadyUsedItsRetry_IsLeftForAPerson()
    {
        var task = AddTask(DevelopmentTaskStatus.Failed);
        AddExecution(task, TaskExecutionStatus.Failed, 1);
        AddExecution(task, TaskExecutionStatus.Failed, 2);

        (await Query()).Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyTheNewestRunOfATaskCounts()
    {
        var task = AddTask(DevelopmentTaskStatus.Executing);
        AddExecution(task, TaskExecutionStatus.Failed, 1);
        AddExecution(task, TaskExecutionStatus.Running, 2);

        (await Query()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Completed)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    [InlineData(TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Pending)]
    public async Task OtherOutcomesAreNeverRetriedHere(TaskExecutionStatus status)
    {
        var task = AddTask(DevelopmentTaskStatus.Failed);
        AddExecution(task, status);

        (await Query()).Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedRunWhoseTaskWasMovedOn_IsNotReturned()
    {
        var task = AddTask(DevelopmentTaskStatus.Completed);
        AddExecution(task, TaskExecutionStatus.Failed);

        (await Query()).Should().BeEmpty();
    }

    [Fact]
    public async Task WorkCreatedBeforeAutomationWasSwitchedOn_IsNotTouched()
    {
        var task = AddTask(DevelopmentTaskStatus.Failed, createdAt: _since.AddDays(-3));
        AddExecution(task, TaskExecutionStatus.Failed);

        (await Query()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherWorkspacesFailuresAreNotReturned()
    {
        var other = new RepositoryWorkspace { Id = Guid.NewGuid(), Owner = "x", Repository = "y", Branch = "main" };
        _db.RepositoryWorkspaces.Add(other);
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(), RepositoryWorkspaceId = other.Id, Title = "t",
            Status = DevelopmentTaskStatus.Failed, CreatedAt = _since.AddHours(1),
        };
        _db.DevelopmentTasks.Add(task);
        _db.SaveChanges();
        AddExecution(task, TaskExecutionStatus.Failed);

        (await Query()).Should().BeEmpty();
    }
}

public class AutomaticRetryOrchestrationTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly DateTime _since = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryExecutionRepository _executions = new();
    private readonly FakeActivities _activities = new();
    private readonly FakeWork _work = new();
    private readonly FakeRetry _retry = new();
    private readonly CapturingRecorder _recorder = new();

    private AutomationOrchestrator Orchestrator(IRetryExecutionCommandHandler? retry = null) =>
        new(
            _work,
            approveTask: null!,
            startExecution: null!,
            _executions,
            _activities,
            _recorder,
            fingerprint: null!,
            diffReader: null!,
            approveReview: null!,
            commit: null!,
            push: null!,
            createPullRequest: null!,
            syncPullRequest: null!,
            merge: null!,
            new AutomationDecisionLedger(),
            NullLogger<AutomationOrchestrator>.Instance,
            retry: retry ?? _retry);

    private AutomationPolicy Policy(AutomationLevel level, int parallel = 2) => new()
    {
        RepositoryWorkspaceId = _workspaceId,
        Level = level,
        ActiveSince = _since,
        MaxParallelExecutions = parallel,
    };

    private TaskExecution FailedRun(bool generation = true)
    {
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = Guid.NewGuid(),
            Status = TaskExecutionStatus.Failed,
        };
        _executions.Executions[execution.Id] = execution;
        _work.Retryable.Add(execution.Id);
        _activities.ByExecution[execution.Id] = generation
            ? new[]
            {
                new ExecutionActivity { Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Failed, Message = "Developer Agent failed" },
            }
            : new[]
            {
                new ExecutionActivity { Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Failed, Message = "failed" },
                new ExecutionActivity { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Started, Message = "Build started" },
            };
        return execution;
    }

    [Fact]
    public async Task AGenerationFailure_IsRestartedOnceAndExplainedInTheActivityLog()
    {
        var run = FailedRun();

        await Orchestrator().AdvanceAsync(Policy(AutomationLevel.AutoPr));

        _retry.Commands.Should().ContainSingle().Which.TaskId.Should().Be(run.DevelopmentTaskId);
        _recorder.Messages.Should().ContainSingle(m => m.Contains("restarted") && m.Contains("generation"));
    }

    [Fact]
    public async Task AFailureThatReachedBuildOrTest_IsNotRestarted()
    {
        FailedRun(generation: false);

        await Orchestrator().AdvanceAsync(Policy(AutomationLevel.AutoPr));

        _retry.Commands.Should().BeEmpty();
        _recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task SemiAutoAlsoRestarts_AndManualNeverDoes()
    {
        FailedRun();

        await Orchestrator().AdvanceAsync(Policy(AutomationLevel.Manual));
        _retry.Commands.Should().BeEmpty();

        await Orchestrator().AdvanceAsync(Policy(AutomationLevel.SemiAuto));
        _retry.Commands.Should().HaveCount(1);
    }

    [Fact]
    public async Task APausedPolicy_NeverRestartsAnything()
    {
        FailedRun();
        var policy = Policy(AutomationLevel.FullAuto);
        policy.Paused = true;

        await Orchestrator().AdvanceAsync(policy);

        _retry.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RestartsRespectTheParallelExecutionLimit()
    {
        FailedRun();
        FailedRun();
        FailedRun();
        _work.Active = 1;

        await Orchestrator().AdvanceAsync(Policy(AutomationLevel.AutoPr, parallel: 2));

        _retry.Commands.Should().HaveCount(1);
    }

    [Fact]
    public async Task NothingRestarts_WhenNoCapacityIsLeft()
    {
        FailedRun();
        _work.Active = 2;

        await Orchestrator().AdvanceAsync(Policy(AutomationLevel.AutoPr, parallel: 2));

        _retry.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedRetry_IsNotAnnounced_AndDoesNotUseCapacity()
    {
        var first = FailedRun();
        var second = FailedRun();
        _retry.RefuseTasks.Add(first.DevelopmentTaskId);

        await Orchestrator().AdvanceAsync(Policy(AutomationLevel.AutoPr, parallel: 1));

        _retry.Commands.Select(c => c.TaskId).Should().Equal(first.DevelopmentTaskId, second.DevelopmentTaskId);
        _recorder.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task AThrowingRetry_DoesNotStopTheOthers()
    {
        var first = FailedRun();
        var second = FailedRun();
        _retry.ThrowForTask = first.DevelopmentTaskId;

        await Orchestrator().AdvanceAsync(Policy(AutomationLevel.AutoPr));

        _retry.Commands.Select(c => c.TaskId).Should().Contain(second.DevelopmentTaskId);
        _recorder.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task AnExecutionThatIsNoLongerFailed_IsSkipped()
    {
        var run = FailedRun();
        run.Status = TaskExecutionStatus.Running;

        await Orchestrator().AdvanceAsync(Policy(AutomationLevel.AutoPr));

        _retry.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutARetryHandler_NothingHappensAndNothingBreaks()
    {
        FailedRun();
        var orchestrator = new AutomationOrchestrator(
            _work, null!, null!, _executions, _activities, _recorder, null!, null!, null!, null!, null!, null!, null!, null!,
            new AutomationDecisionLedger(), NullLogger<AutomationOrchestrator>.Instance);

        var act = () => orchestrator.AdvanceAsync(Policy(AutomationLevel.AutoPr));

        await act.Should().NotThrowAsync();
    }

    private sealed class FakeWork : IAutomationWorkReader
    {
        public int Active { get; set; }

        public List<Guid> Retryable { get; } = new();

        public Task<IReadOnlyList<DevelopmentTask>> GetTasksReadyToStartAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentTask>>(Array.Empty<DevelopmentTask>());

        public Task<int> CountActiveExecutionsAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) => Task.FromResult(Active);
public Task<int> CountExecutionsForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<Guid>> GetRetryableFailedExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Retryable.ToList());

        public Task<IReadOnlyList<Guid>> GetDeliverableExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task<IReadOnlyList<Guid>> GetOpenPullRequestExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
    }

    private sealed class FakeActivities : IExecutionActivityRepository
    {
        public Dictionary<Guid, ExecutionActivity[]> ByExecution { get; } = new();

        public Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExecutionActivity>>(ByExecution.TryGetValue(executionId, out var list) ? list : Array.Empty<ExecutionActivity>());
    }

    private sealed class FakeRetry : IRetryExecutionCommandHandler
    {
        public List<RetryExecutionCommand> Commands { get; } = new();

        public HashSet<Guid> RefuseTasks { get; } = new();

        public Guid? ThrowForTask { get; set; }

        public Task<RetryExecutionResult> HandleAsync(RetryExecutionCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (command.TaskId == ThrowForTask)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(RefuseTasks.Contains(command.TaskId)
                ? RetryExecutionResult.ConflictResult("not eligible")
                : new RetryExecutionResult { Success = true });
        }
    }

    private sealed class CapturingRecorder : IExecutionActivityRecorder
    {
        public List<string> Messages { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
