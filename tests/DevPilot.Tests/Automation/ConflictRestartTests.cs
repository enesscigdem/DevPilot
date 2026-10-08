using DevPilot.Application.Automation;
using DevPilot.Application.Executions.Commands.MergeExecution;
using DevPilot.Application.Executions.Commands.RetryExecution;
using DevPilot.Application.Executions.Commands.SyncPullRequest;
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

public class ConflictRestartRulesTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    public void ARestartIsOnlyLeftWhileTheTaskHasRunFewerTimesThanTheCap(int runsSoFar, bool expected)
    {
        AutomationRetryRules.HasConflictRestartLeft(runsSoFar).Should().Be(expected);
    }
}

public class ConflictRestartOrchestrationTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly InMemoryExecutionRepository _executions = new();
    private readonly FakeWork _work = new();
    private readonly FakeSync _sync = new();
    private readonly FakeMerge _merge = new();
    private readonly FakeRetry _retry = new();
    private readonly CapturingRecorder _recorder = new();

    private AutomationOrchestrator Orchestrator(bool withRetry = true) =>
        new(
            _work, null!, null!, _executions, null!, _recorder, null!, null!, null!, null!, null!, null!,
            _sync, _merge, new AutomationDecisionLedger(), NullLogger<AutomationOrchestrator>.Instance,
            retry: withRetry ? _retry : null);

    private AutomationPolicy Policy(int parallel = 2) => new()
    {
        RepositoryWorkspaceId = _workspaceId,
        Level = AutomationLevel.FullAuto,
        ActiveSince = DateTime.UtcNow.AddDays(-1),
        MaxParallelExecutions = parallel,
    };

    private TaskExecution OpenPullRequest(Action<TaskExecution>? tweak = null)
    {
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = Guid.NewGuid(),
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            CommitStatus = ExecutionCommitStatus.Committed,
            PushStatus = ExecutionPushStatus.Pushed,
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            PullRequestNumber = 22,
            PullRequestRemoteState = ExecutionPullRequestRemoteState.Open,
            PullRequestIntegrityStatus = ExecutionPullRequestIntegrityStatus.Valid,
            CiStatus = ExecutionCiStatus.Success,
            MergeStatus = ExecutionMergeStatus.None,
        };
        tweak?.Invoke(execution);
        _executions.Executions[execution.Id] = execution;
        _work.OpenPullRequests.Add(execution.Id);
        _work.RunsByTask[execution.DevelopmentTaskId] = 1;
        return execution;
    }

    [Fact]
    public async Task AMergeRefusedForConflicts_RestartsTheTaskOnTheCurrentBase_AndExplainsIt()
    {
        var run = OpenPullRequest();
        _merge.Result = MergeExecutionResult.BaseConflict("GitHub refused the merge: Pull Request is not mergeable");

        await Orchestrator().AdvanceAsync(Policy());

        var command = _retry.Commands.Should().ContainSingle().Subject;
        command.TaskId.Should().Be(run.DevelopmentTaskId);
        command.ConflictedExecutionId.Should().Be(run.Id);
        _recorder.Messages.Should().ContainSingle(m =>
            m.Contains("redid this task on the current base") && m.Contains("#22") && m.Contains("stays open"));
    }

    [Fact]
    public async Task AMergeRefusedForAnotherReason_NeverRestartsAnything()
    {
        OpenPullRequest();
        _merge.Result = MergeExecutionResult.Conflict("Required status check is expected");

        await Orchestrator().AdvanceAsync(Policy());

        _retry.Commands.Should().BeEmpty();
        _recorder.Messages.Should().ContainSingle(m => m.Contains("left this for a person"));
    }

    [Fact]
    public async Task ATaskThatAlreadyRanEnoughTimes_IsLeftForAPerson()
    {
        var run = OpenPullRequest();
        _work.RunsByTask[run.DevelopmentTaskId] = 1 + AutomationRetryRules.MaxConflictRestarts;
        _merge.Result = MergeExecutionResult.BaseConflict("not mergeable");

        await Orchestrator().AdvanceAsync(Policy());

        _retry.Commands.Should().BeEmpty();
        _recorder.Messages.Should().ContainSingle(m => m.Contains("left this for a person") && m.Contains("already ran"));
    }

    [Fact]
    public async Task ARestartTheCommandRefuses_IsReportedWithItsReason_NotAnnouncedAsDone()
    {
        var run = OpenPullRequest();
        _merge.Result = MergeExecutionResult.BaseConflict("not mergeable");
        _retry.RefuseTasks.Add(run.DevelopmentTaskId);

        await Orchestrator().AdvanceAsync(Policy());

        _retry.Commands.Should().HaveCount(1);
        _recorder.Messages.Should().ContainSingle(m => m.Contains("could not be restarted") && m.Contains("not eligible"));
        _recorder.Messages.Should().NotContain(m => m.Contains("redid this task"));
    }

    [Fact]
    public async Task WithoutARetryHandler_TheConflictIsReportedForAPerson()
    {
        OpenPullRequest();
        _merge.Result = MergeExecutionResult.BaseConflict("not mergeable");

        await Orchestrator(withRetry: false).AdvanceAsync(Policy());

        _recorder.Messages.Should().ContainSingle(m => m.Contains("conflicts with the base branch"));
    }

    [Fact]
    public async Task TheParallelLimitDoesNotBlockTheRestart_BecauseNothingWouldEverRetryIt()
    {
        OpenPullRequest();
        _merge.Result = MergeExecutionResult.BaseConflict("not mergeable");
        _work.Active = 5;

        await Orchestrator().AdvanceAsync(Policy(parallel: 1));

        _retry.Commands.Should().HaveCount(1);
    }

    [Fact]
    public async Task AMergeThatSucceeds_DoesNotRestartAnything()
    {
        OpenPullRequest();
        _merge.Result = MergeExecutionResult.Ok(new MergeExecutionResponseDto(Guid.NewGuid(), "Merged", 22, null, "main", "b", "sha", "m", DateTime.UtcNow, "merge"));

        await Orchestrator().AdvanceAsync(Policy());

        _retry.Commands.Should().BeEmpty();
        _recorder.Messages.Should().ContainSingle(m => m.Contains("merged the pull request"));
    }

    [Fact]
    public async Task AMergeStuckInProgressWithAStaleLease_IsTriedAgain()
    {
        var run = OpenPullRequest(e =>
        {
            e.MergeStatus = ExecutionMergeStatus.InProgress;
            e.MergeClaimedAt = DateTime.UtcNow.AddMinutes(-12);
        });
        _merge.Result = MergeExecutionResult.BaseConflict("not mergeable");

        await Orchestrator().AdvanceAsync(Policy());

        _merge.Commands.Should().ContainSingle().Which.ExecutionId.Should().Be(run.Id);
        _retry.Commands.Should().HaveCount(1);
    }

    [Fact]
    public async Task AMergeThatIsStillRunning_IsLeftAlone()
    {
        OpenPullRequest(e =>
        {
            e.MergeStatus = ExecutionMergeStatus.InProgress;
            e.MergeClaimedAt = DateTime.UtcNow.AddSeconds(-10);
        });

        await Orchestrator().AdvanceAsync(Policy());

        _merge.Commands.Should().BeEmpty();
        _retry.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task AMergeThatWasAlreadyRefused_IsNotAttemptedAgain()
    {
        OpenPullRequest(e => e.MergeStatus = ExecutionMergeStatus.Failed);

        await Orchestrator().AdvanceAsync(Policy());

        _merge.Commands.Should().BeEmpty();
        _retry.Commands.Should().BeEmpty();
    }

    private sealed class FakeWork : IAutomationWorkReader
    {
        public int Active { get; set; }

        public List<Guid> OpenPullRequests { get; } = new();

        public Dictionary<Guid, int> RunsByTask { get; } = new();

        public Task<IReadOnlyList<DevelopmentTask>> GetTasksReadyToStartAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentTask>>(Array.Empty<DevelopmentTask>());

        public Task<int> CountActiveExecutionsAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) => Task.FromResult(Active);

        public Task<int> CountExecutionsForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(RunsByTask.TryGetValue(taskId, out var runs) ? runs : 0);

        public Task<IReadOnlyList<Guid>> GetRetryableFailedExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task<IReadOnlyList<Guid>> GetDeliverableExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task<IReadOnlyList<Guid>> GetOpenPullRequestExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(OpenPullRequests.ToList());
    }

    private sealed class FakeSync : ISyncPullRequestCommandHandler
    {
        public Task<SyncPullRequestResult> HandleAsync(SyncPullRequestCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(SyncPullRequestResult.Conflict("not needed by this test"));
    }

    private sealed class FakeMerge : IMergeExecutionCommandHandler
    {
        public List<MergeExecutionCommand> Commands { get; } = new();

        public MergeExecutionResult Result { get; set; } = MergeExecutionResult.Conflict("unset");

        public Task<MergeExecutionResult> HandleAsync(MergeExecutionCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeRetry : IRetryExecutionCommandHandler
    {
        public List<RetryExecutionCommand> Commands { get; } = new();

        public HashSet<Guid> RefuseTasks { get; } = new();

        public Task<RetryExecutionResult> HandleAsync(RetryExecutionCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
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

public class ConflictRestartQueryTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly RepositoryWorkspace _workspace;
    private readonly EfAutomationWorkReader _reader;
    private readonly DateTime _since = DateTime.UtcNow.AddDays(-1);

    public ConflictRestartQueryTests()
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

    private TaskExecution Add(DevelopmentTask? task, Action<TaskExecution> configure)
    {
        task ??= new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = _workspace.Id,
            Title = "t",
            Status = DevelopmentTaskStatus.Completed,
            CreatedAt = _since.AddHours(1),
        };
        if (_db.DevelopmentTasks.Find(task.Id) is null)
        {
            _db.DevelopmentTasks.Add(task);
        }

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Completed,
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            CreatedAt = _since.AddHours(2),
        };
        configure(execution);
        _db.TaskExecutions.Add(execution);
        _db.SaveChanges();
        return execution;
    }

    private Task<IReadOnlyList<Guid>> OpenPullRequests() => _reader.GetOpenPullRequestExecutionIdsAsync(_workspace.Id, _since);

    [Fact]
    public async Task AMergeWhoseLeaseWentStale_IsReturnedAgain()
    {
        var stuck = Add(null, e =>
        {
            e.MergeStatus = ExecutionMergeStatus.InProgress;
            e.MergeClaimedAt = DateTime.UtcNow.AddMinutes(-10);
        });

        (await OpenPullRequests()).Should().Equal(stuck.Id);
    }

    [Fact]
    public async Task AMergeThatIsStillRunning_IsNotReturned()
    {
        Add(null, e =>
        {
            e.MergeStatus = ExecutionMergeStatus.InProgress;
            e.MergeClaimedAt = DateTime.UtcNow.AddSeconds(-10);
        });

        (await OpenPullRequests()).Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedOrFinishedMerge_IsNotReturned()
    {
        Add(null, e => e.MergeStatus = ExecutionMergeStatus.Failed);
        Add(null, e => e.MergeStatus = ExecutionMergeStatus.Merged);

        (await OpenPullRequests()).Should().BeEmpty();
    }

    [Fact]
    public async Task ARunNeverTriedToMerge_IsStillReturned()
    {
        var fresh = Add(null, _ => { });

        (await OpenPullRequests()).Should().Equal(fresh.Id);
    }

    [Fact]
    public async Task TheRunCountOfATaskIncludesEveryOutcome()
    {
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = _workspace.Id,
            Title = "t",
            Status = DevelopmentTaskStatus.Completed,
            CreatedAt = _since.AddHours(1),
        };
        Add(task, e => e.Status = TaskExecutionStatus.Failed);
        Add(task, e => e.Status = TaskExecutionStatus.Cancelled);
        Add(task, e => e.Status = TaskExecutionStatus.Completed);
        Add(null, _ => { });

        (await _reader.CountExecutionsForTaskAsync(task.Id)).Should().Be(3);
        (await _reader.CountExecutionsForTaskAsync(Guid.NewGuid())).Should().Be(0);
    }
}
