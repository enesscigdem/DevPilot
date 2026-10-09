using DevPilot.Application.Automation;
using DevPilot.Application.Executions.Commands.StartExecution;
using DevPilot.Application.Goals;
using DevPilot.Application.Tasks.Commands.ApproveTask;
using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Goals;

internal sealed class StubPolicyStore : IAutomationPolicyStore
{
    public StubPolicyStore(ConflictMode mode = ConflictMode.Careful) => Policy = new AutomationPolicy { ConflictMode = mode };

    public AutomationPolicy? Policy { get; set; }

    public Task<AutomationPolicy?> GetAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) => Task.FromResult(Policy);

    public Task<AutomationPolicy?> SaveAsync(AutomationPolicy policy, CancellationToken cancellationToken = default) => Task.FromResult<AutomationPolicy?>(policy);

    public Task<IReadOnlyList<AutomationPolicy>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AutomationPolicy>>(Array.Empty<AutomationPolicy>());
}

internal static class GoalFixtures
{
    public static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    public static GoalExecutionState Run(
        TaskExecutionStatus status = TaskExecutionStatus.Running,
        ExecutionReviewStatus review = ExecutionReviewStatus.Pending,
        ExecutionPullRequestStatus pr = ExecutionPullRequestStatus.None,
        ExecutionPullRequestRemoteState remote = ExecutionPullRequestRemoteState.Unknown,
        ExecutionMergeStatus merge = ExecutionMergeStatus.None) =>
        new(Guid.NewGuid(), status, review, ExecutionCommitStatus.None, ExecutionPushStatus.None, pr, remote, merge, null, null, null, Now);

    public static GoalTaskState Task(
        string key,
        DevelopmentTaskStatus status = DevelopmentTaskStatus.Draft,
        string[]? blockedBy = null,
        string[]? areas = null,
        string[]? impacted = null,
        GoalExecutionState? run = null,
        int attempts = 0,
        DateTime? requestedAt = null,
        string? note = null,
        string[]? dependsOn = null) =>
        new(
            Guid.NewGuid(), key, int.Parse(key[1..]), 1, "medium",
            areas ?? Array.Empty<string>(), dependsOn ?? Array.Empty<string>(), blockedBy ?? Array.Empty<string>(),
            attempts, requestedAt, note, Guid.NewGuid(), $"Task {key}", "desc", status, run, impacted ?? Array.Empty<string>());

    public static GoalState Goal(Guid workspaceId, params GoalTaskState[] tasks) =>
        new(Guid.NewGuid(), workspaceId, "goal", "text", GoalStatus.Active, "ai", 0, 0, null, Now, null, tasks);
}

public class GoalPhasesTests
{
    private static GoalPhase Phase(GoalTaskState task) => GoalPhases.Of(task, GoalFixtures.Now);

    [Theory]
    [InlineData(DevelopmentTaskStatus.Draft, GoalPhase.Waiting)]
    [InlineData(DevelopmentTaskStatus.ReadyForAnalysis, GoalPhase.Waiting)]
    [InlineData(DevelopmentTaskStatus.Analyzing, GoalPhase.Analyzing)]
    [InlineData(DevelopmentTaskStatus.AwaitingApproval, GoalPhase.PlanReady)]
    [InlineData(DevelopmentTaskStatus.Approved, GoalPhase.Queued)]
    [InlineData(DevelopmentTaskStatus.Failed, GoalPhase.Failed)]
    [InlineData(DevelopmentTaskStatus.Rejected, GoalPhase.Stopped)]
    public void A_task_without_a_run_is_read_from_its_own_status(DevelopmentTaskStatus status, GoalPhase expected) =>
        Phase(GoalFixtures.Task("t1", status)).Should().Be(expected);

    [Fact]
    public void A_requested_analysis_counts_as_analyzing_until_patience_runs_out()
    {
        Phase(GoalFixtures.Task("t1", requestedAt: GoalFixtures.Now.AddMinutes(-2))).Should().Be(GoalPhase.Analyzing);
        Phase(GoalFixtures.Task("t1", requestedAt: GoalFixtures.Now.AddMinutes(-30))).Should().Be(GoalPhase.Waiting);
    }

    [Fact]
    public void The_run_decides_once_there_is_one()
    {
        Phase(GoalFixtures.Task("t1", DevelopmentTaskStatus.Executing, run: GoalFixtures.Run())).Should().Be(GoalPhase.Running);
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Pending))).Should().Be(GoalPhase.Running);
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Failed))).Should().Be(GoalPhase.Failed);
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Cancelled))).Should().Be(GoalPhase.Stopped);
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Completed))).Should().Be(GoalPhase.InReview);
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved))).Should().Be(GoalPhase.Delivering);
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Completed, ExecutionReviewStatus.Rejected))).Should().Be(GoalPhase.Stopped);
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(
            TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionPullRequestStatus.Open, ExecutionPullRequestRemoteState.Open)))
            .Should().Be(GoalPhase.PullRequest);
    }

    [Fact]
    public void A_merged_pull_request_is_merged_even_if_the_local_status_lags()
    {
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Completed, merge: ExecutionMergeStatus.Merged))).Should().Be(GoalPhase.Merged);
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(
            TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionPullRequestStatus.Open, ExecutionPullRequestRemoteState.Merged)))
            .Should().Be(GoalPhase.Merged);
        Phase(GoalFixtures.Task("t1", run: GoalFixtures.Run(
            TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionPullRequestStatus.Open, ExecutionPullRequestRemoteState.Closed)))
            .Should().Be(GoalPhase.Stopped);
    }

    [Fact]
    public void Ended_tasks_are_settled_and_started_unfinished_ones_are_in_flight()
    {
        GoalPhases.IsSettled(GoalPhase.Merged).Should().BeTrue();
        GoalPhases.IsSettled(GoalPhase.Failed).Should().BeTrue();
        GoalPhases.IsSettled(GoalPhase.Stopped).Should().BeTrue();
        GoalPhases.IsSettled(GoalPhase.PullRequest).Should().BeFalse();
        GoalPhases.IsInFlight(GoalPhase.Running).Should().BeTrue();
        GoalPhases.IsInFlight(GoalPhase.PullRequest).Should().BeTrue();
        GoalPhases.IsInFlight(GoalPhase.Waiting).Should().BeFalse();
        GoalPhases.IsInFlight(GoalPhase.Merged).Should().BeFalse();
    }
}

public class GoalOrchestratorTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private readonly FakeStore _store = new();
    private readonly FakePolicies _policies = new();
    private readonly FakeWork _work = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly FakeApprove _approve = new();
    private readonly FakeStart _start = new();

    private GoalOrchestrator Orchestrator() =>
        new(_store, _policies, _work, _dispatcher, _approve, _start, NullLogger<GoalOrchestrator>.Instance, new FixedClock(GoalFixtures.Now));

    private async Task Run(params GoalTaskState[] tasks)
    {
        var goal = GoalFixtures.Goal(Workspace, tasks);
        _store.Goal = goal;
        await Orchestrator().AdvanceAsync(goal.Id);
    }

    private static AutomationPolicy Policy(AutomationLevel level, bool paused = false, int parallel = 2, ConflictMode mode = ConflictMode.Careful) =>
        new() { RepositoryWorkspaceId = Workspace, Level = level, Paused = paused, MaxParallelExecutions = parallel, ConflictMode = mode };

    [Fact]
    public async Task Tasks_that_wait_for_nothing_get_their_analysis_started_up_to_the_free_slots()
    {
        var t1 = GoalFixtures.Task("t1");
        var t2 = GoalFixtures.Task("t2");
        var t3 = GoalFixtures.Task("t3");
        _policies.Policy = Policy(AutomationLevel.Manual, parallel: 2);

        await Run(t1, t2, t3);

        _dispatcher.Analyzed.Should().Equal(t1.TaskId, t2.TaskId);
        _store.AnalysisRequested.Should().Equal(t1.GoalTaskId, t2.GoalTaskId);
    }

    [Fact]
    public async Task A_task_waits_for_the_earlier_task_it_clashes_with()
    {
        var t1 = GoalFixtures.Task("t1");
        var t2 = GoalFixtures.Task("t2", blockedBy: new[] { "t1" });

        await Run(t1, t2);

        _dispatcher.Analyzed.Should().Equal(t1.TaskId);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    public async Task A_failed_or_stopped_task_does_not_hold_anything_back(TaskExecutionStatus end)
    {
        var t1 = GoalFixtures.Task("t1", run: GoalFixtures.Run(end));
        var t2 = GoalFixtures.Task("t2", blockedBy: new[] { "t1" });

        await Run(t1, t2);

        _dispatcher.Analyzed.Should().Equal(t2.TaskId);
    }


    [Theory]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    public async Task A_task_that_needs_a_failed_prerequisite_waits_and_says_why(TaskExecutionStatus end)
    {
        var t1 = GoalFixtures.Task("t1", run: GoalFixtures.Run(end));
        var t2 = GoalFixtures.Task("t2", blockedBy: new[] { "t1" }, dependsOn: new[] { "t1" });
        var independent = GoalFixtures.Task("t3");

        await Run(t1, t2, independent);

        _dispatcher.Analyzed.Should().Equal(independent.TaskId);
        _store.Notes[t2.GoalTaskId].Should().Contain("Task t1");
    }

    [Fact]
    public async Task A_retried_prerequisite_that_is_merged_releases_the_task_that_needs_it()
    {
        var t1 = GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Completed, merge: ExecutionMergeStatus.Merged));
        var t2 = GoalFixtures.Task("t2", blockedBy: new[] { "t1" }, dependsOn: new[] { "t1" }, note: "old");

        await Run(t1, t2);

        _dispatcher.Analyzed.Should().Equal(t2.TaskId);
        _store.Notes[t2.GoalTaskId].Should().BeNull();
    }
    [Fact]
    public async Task A_merged_task_releases_the_one_behind_it_but_an_open_pull_request_does_not()
    {
        var merged = GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Completed, merge: ExecutionMergeStatus.Merged));
        var open = GoalFixtures.Task("t2", run: GoalFixtures.Run(
            TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionPullRequestStatus.Open, ExecutionPullRequestRemoteState.Open));
        var behindMerged = GoalFixtures.Task("t3", blockedBy: new[] { "t1" });
        var behindOpen = GoalFixtures.Task("t4", blockedBy: new[] { "t2" });

        await Run(merged, open, behindMerged, behindOpen);

        _dispatcher.Analyzed.Should().Equal(behindMerged.TaskId);
    }

    [Fact]
    public async Task A_requested_analysis_is_not_requested_twice_and_counts_against_the_slots()
    {
        var running = GoalFixtures.Task("t1", requestedAt: GoalFixtures.Now.AddSeconds(-5));
        var next = GoalFixtures.Task("t2");
        var after = GoalFixtures.Task("t3");
        _policies.Policy = Policy(AutomationLevel.Manual, parallel: 2);

        await Run(running, next, after);

        _dispatcher.Analyzed.Should().Equal(next.TaskId);
    }

    [Fact]
    public async Task A_task_whose_analysis_keeps_failing_to_start_is_flagged_for_a_person()
    {
        var stuck = GoalFixtures.Task("t1", attempts: GoalOrchestrator.MaxAnalysisAttempts);

        await Run(stuck);

        _dispatcher.Analyzed.Should().BeEmpty();
        _store.Notes.Should().ContainKey(stuck.GoalTaskId);
    }


    [Fact]
    public async Task An_approved_task_without_a_run_is_started_again_without_a_second_approval()
    {
        var stuck = GoalFixtures.Task("t1", DevelopmentTaskStatus.Approved);
        _policies.Policy = Policy(AutomationLevel.SemiAuto);

        await Run(stuck);

        _approve.Approved.Should().BeEmpty();
        _start.Started.Should().Equal(stuck.TaskId);
    }

    [Fact]
    public async Task A_start_that_throws_is_retried_next_pass_and_does_not_block_other_tasks()
    {
        var broken = GoalFixtures.Task("t1", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/a.ts" });
        var fine = GoalFixtures.Task("t2", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/b.ts" });
        _policies.Policy = Policy(AutomationLevel.SemiAuto, parallel: 2);
        _start.Throw.Add(broken.TaskId);

        await Run(broken, fine);
        _start.Throw.Clear();
        await Run(broken with { TaskStatus = DevelopmentTaskStatus.Approved }, fine);

        _start.Started.Should().Contain(new[] { fine.TaskId });
        _approve.Approved.Count(id => id == broken.TaskId).Should().Be(1, "the second pass starts it without approving again");
        _start.Started.Should().Contain(broken.TaskId);
    }
    [Fact]
    public async Task With_automation_on_a_ready_plan_is_approved_and_started()
    {
        var ready = GoalFixtures.Task("t1", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/a.ts" });
        _policies.Policy = Policy(AutomationLevel.SemiAuto);

        await Run(ready);

        _approve.Approved.Should().Equal(ready.TaskId);
        _start.Started.Should().Equal(ready.TaskId);
    }

    [Theory]
    [InlineData(AutomationLevel.Manual, false)]
    [InlineData(AutomationLevel.SemiAuto, true)]
    [InlineData(AutomationLevel.AutoPr, true)]
    public async Task Without_automation_or_while_paused_a_ready_plan_waits_for_a_person(AutomationLevel level, bool paused)
    {
        var ready = GoalFixtures.Task("t1", DevelopmentTaskStatus.AwaitingApproval);
        _policies.Policy = Policy(level, paused);

        await Run(ready);

        _approve.Approved.Should().BeEmpty();
        _start.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_any_policy_nothing_is_approved_automatically()
    {
        _policies.Policy = null;

        await Run(GoalFixtures.Task("t1", DevelopmentTaskStatus.AwaitingApproval));

        _approve.Approved.Should().BeEmpty();
    }

    [Fact]
    public async Task Plans_are_started_only_while_there_is_a_free_slot()
    {
        _policies.Policy = Policy(AutomationLevel.SemiAuto, parallel: 2);
        _work.Active = 1;
        var a = GoalFixtures.Task("t1", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/a.ts" });
        var b = GoalFixtures.Task("t2", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/b.ts" });

        await Run(a, b);

        _start.Started.Should().Equal(a.TaskId);
    }

    [Fact]
    public async Task A_plan_that_really_touches_the_same_file_as_work_in_flight_waits()
    {
        _policies.Policy = Policy(AutomationLevel.SemiAuto);
        var running = GoalFixtures.Task("t1", run: GoalFixtures.Run(), impacted: new[] { "src/Notes/Editor.tsx" });
        var ready = GoalFixtures.Task("t2", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/Notes/Editor.tsx" });
        var other = GoalFixtures.Task("t3", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/Search/Box.tsx" });

        await Run(running, ready, other);

        _start.Started.Should().Equal(other.TaskId);
        _store.Notes[ready.GoalTaskId].Should().Contain("Task t1").And.Contain("Editor.tsx");
    }

    [Fact]
    public async Task Two_ready_plans_that_overlap_do_not_start_in_the_same_pass()
    {
        _policies.Policy = Policy(AutomationLevel.SemiAuto);
        var a = GoalFixtures.Task("t1", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/Editor.tsx" });
        var b = GoalFixtures.Task("t2", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/Editor.tsx", "src/Bar.tsx" });

        await Run(a, b);

        _start.Started.Should().Equal(a.TaskId);
    }

    [Fact]
    public async Task Before_the_analysis_exists_the_planned_areas_stand_in_for_the_real_files()
    {
        _policies.Policy = Policy(AutomationLevel.SemiAuto);
        var running = GoalFixtures.Task("t1", run: GoalFixtures.Run(), areas: new[] { "src/Notes/Editor.tsx" });
        var ready = GoalFixtures.Task("t2", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/Notes/Editor.tsx" });

        await Run(running, ready);

        _start.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_that_cannot_be_approved_gets_the_reason_as_a_note_and_the_others_still_go()
    {
        _policies.Policy = Policy(AutomationLevel.SemiAuto);
        var tooBig = GoalFixtures.Task("t1", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "a.ts" });
        var fine = GoalFixtures.Task("t2", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "b.ts" });
        _approve.Reject[tooBig.TaskId] = "Please decompose the task into smaller focused tasks.";

        await Run(tooBig, fine);

        _store.Notes[tooBig.GoalTaskId].Should().Contain("decompose");
        _start.Started.Should().Equal(fine.TaskId);
    }

    [Fact]
    public async Task An_unchanged_note_is_not_written_again()
    {
        _policies.Policy = Policy(AutomationLevel.SemiAuto);
        var note = "Waits for “Task t1”: both change src/a.ts.";
        var running = GoalFixtures.Task("t1", run: GoalFixtures.Run(), impacted: new[] { "src/a.ts" });
        var ready = GoalFixtures.Task("t2", DevelopmentTaskStatus.AwaitingApproval, impacted: new[] { "src/a.ts" }, note: note);

        await Run(running, ready);

        _store.Notes.Should().NotContainKey(ready.GoalTaskId);
    }

    [Fact]
    public async Task When_every_task_is_merged_or_stopped_the_goal_is_completed()
    {
        await Run(
            GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Completed, merge: ExecutionMergeStatus.Merged)),
            GoalFixtures.Task("t2", run: GoalFixtures.Run(TaskExecutionStatus.Cancelled)));

        _store.Statuses.Should().Equal(GoalStatus.Completed);
    }

    [Fact]
    public async Task A_failed_task_keeps_the_goal_active_so_a_retry_is_still_followed()
    {
        await Run(
            GoalFixtures.Task("t1", run: GoalFixtures.Run(TaskExecutionStatus.Completed, merge: ExecutionMergeStatus.Merged)),
            GoalFixtures.Task("t2", run: GoalFixtures.Run(TaskExecutionStatus.Failed)));

        _store.Statuses.Should().BeEmpty();
    }


    [Fact]
    public async Task A_cancelled_goal_is_left_alone()
    {
        var goal = GoalFixtures.Goal(Workspace, GoalFixtures.Task("t1")) with { Status = GoalStatus.Cancelled };
        _store.Goal = goal;

        await Orchestrator().AdvanceAsync(goal.Id);

        _dispatcher.Analyzed.Should().BeEmpty();
        _store.Statuses.Should().BeEmpty();
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeStore : IGoalStore
    {
        public GoalState? Goal { get; set; }

        public List<Guid> AnalysisRequested { get; } = new();

        public Dictionary<Guid, string?> Notes { get; } = new();

        public List<GoalStatus> Statuses { get; } = new();

        public Task AddAsync(Goal goal, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GoalState?> GetAsync(Guid goalId, Guid? repositoryWorkspaceId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Goal);

        public Task<IReadOnlyList<GoalState>> ListAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalState>>(Array.Empty<GoalState>());

        public Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task MarkAnalysisRequestedAsync(Guid goalTaskId, CancellationToken cancellationToken = default)
        {
            AnalysisRequested.Add(goalTaskId);
            return Task.CompletedTask;
        }

        public Task SetNoteAsync(Guid goalTaskId, string? note, CancellationToken cancellationToken = default)
        {
            Notes[goalTaskId] = note;
            return Task.CompletedTask;
        }

        public Task SetStatusAsync(Guid goalId, GoalStatus status, CancellationToken cancellationToken = default)
        {
            Statuses.Add(status);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePolicies : IAutomationPolicyStore
    {
        public AutomationPolicy? Policy { get; set; } = new() { Level = AutomationLevel.Manual };

        public Task<AutomationPolicy?> GetAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Policy);

        public Task<AutomationPolicy?> SaveAsync(AutomationPolicy policy, CancellationToken cancellationToken = default) =>
            Task.FromResult<AutomationPolicy?>(policy);

        public Task<IReadOnlyList<AutomationPolicy>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AutomationPolicy>>(Array.Empty<AutomationPolicy>());
    }

    private sealed class FakeWork : IAutomationWorkReader
    {
        public int Active { get; set; }

        public Task<IReadOnlyList<DevelopmentTask>> GetTasksReadyToStartAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentTask>>(Array.Empty<DevelopmentTask>());

        public Task<int> CountActiveExecutionsAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Active);

        public Task<int> CountExecutionsForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<Guid>> GetDeliverableExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        public Task<IReadOnlyList<Guid>> GetRetryableFailedExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());


        public Task<IReadOnlyList<Guid>> GetOpenPullRequestExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
    }

    private sealed class RecordingDispatcher : IGoalAnalysisDispatcher
    {
        public List<Guid> Analyzed { get; } = new();

        public void EnqueueAnalysis(Guid taskId) => Analyzed.Add(taskId);
    }

    private sealed class FakeApprove : IApproveTaskCommandHandler
    {
        public List<Guid> Approved { get; } = new();

        public Dictionary<Guid, string> Reject { get; } = new();

        public Task<ApproveTaskResult> HandleAsync(ApproveTaskCommand command, CancellationToken cancellationToken = default)
        {
            if (Reject.TryGetValue(command.TaskId, out var reason))
            {
                return Task.FromResult(new ApproveTaskResult { Success = false, Conflict = true, ErrorMessage = reason });
            }

            Approved.Add(command.TaskId);
            return Task.FromResult(new ApproveTaskResult { Success = true });
        }
    }

    private sealed class FakeStart : IStartExecutionCommandHandler
    {
        public List<Guid> Started { get; } = new();
public HashSet<Guid> Throw { get; } = new();

        public Task<StartExecutionResult> HandleAsync(StartExecutionCommand command, CancellationToken cancellationToken = default)
        {
            if (Throw.Contains(command.TaskId))
            {
                throw new InvalidOperationException("transient");
            }

            Started.Add(command.TaskId);
            return Task.FromResult(new StartExecutionResult { Success = true });
        }
    }
}

public class StartGoalCommandHandlerTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private static GoalTaskInput Input(string key, string title = "T", string[]? areas = null, string[]? dependsOn = null) =>
        new() { Key = key, Title = title + key, Description = "d", Areas = areas?.ToList(), DependsOn = dependsOn?.ToList() };

    private static StartGoalCommand Command(params GoalTaskInput[] tasks) =>
        new(Workspace, "make the notes better", "ai", tasks, 100, 20, 0.5m);

    [Fact]
    public async Task Creates_the_tasks_and_records_what_each_one_waits_for()
    {
        var store = new CapturingStore();
        var create = new CountingCreate();
        var handler = new StartGoalCommandHandler(create, store, new StubPolicyStore(), NullLogger<StartGoalCommandHandler>.Instance);

        var result = await handler.HandleAsync(Command(
            Input("t1", areas: new[] { "src/Editor.tsx" }),
            Input("t2", areas: new[] { "src/Search.tsx" }),
            Input("t3", areas: new[] { "src/Editor.tsx" }),
            Input("t4", areas: new[] { "src/Other.tsx" }, dependsOn: new[] { "t2" })));

        result.Success.Should().BeTrue();
        create.Titles.Should().HaveCount(4);
        var goal = store.Added.Should().ContainSingle().Subject;
        goal.Status.Should().Be(GoalStatus.Active);
        goal.EstimatedUsd.Should().Be(0.5m);
        var tasks = goal.Tasks.OrderBy(t => t.Position).ToList();
        tasks.Select(t => t.Key).Should().Equal("t1", "t2", "t3", "t4");
        tasks[0].BlockedBy.Should().BeEmpty();
        tasks[1].BlockedBy.Should().BeEmpty();
        tasks[2].BlockedBy.Should().Be("t1");
        tasks[3].BlockedBy.Should().Be("t2");
        tasks.Select(t => t.Wave).Should().Equal(1, 1, 2, 2);
        tasks.Select(t => t.DevelopmentTaskId).Distinct().Should().HaveCount(4);
    }

    [Fact]
    public async Task The_order_is_recomputed_from_the_tasks_not_trusted_from_the_screen()
    {
        var store = new CapturingStore();
        var handler = new StartGoalCommandHandler(new CountingCreate(), store, new StubPolicyStore(), NullLogger<StartGoalCommandHandler>.Instance);

        await handler.HandleAsync(Command(
            Input("t1", areas: new[] { "a.ts" }),
            Input("t2", areas: new[] { "a.ts" })));

        store.Added.Single().Tasks.OrderBy(t => t.Position).Select(t => t.Wave).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Invalid_requests_create_nothing()
    {
        var store = new CapturingStore();
        var create = new CountingCreate();
        var handler = new StartGoalCommandHandler(create, store, new StubPolicyStore(), NullLogger<StartGoalCommandHandler>.Instance);

        (await handler.HandleAsync(Command())).Success.Should().BeFalse();
        (await handler.HandleAsync(Command(Input("t1"), Input("t1")))).Success.Should().BeFalse();
        (await handler.HandleAsync(Command(new GoalTaskInput { Key = "t1", Title = " " }))).Success.Should().BeFalse();
        (await handler.HandleAsync(Command(new GoalTaskInput { Key = "t,1", Title = "x" }))).Success.Should().BeFalse();
        (await handler.HandleAsync(Command(new GoalTaskInput { Key = "t1", Title = new string('x', 201) }))).Success.Should().BeFalse();
        (await handler.HandleAsync(new StartGoalCommand(Workspace, " ", "ai", new[] { Input("t1") }, 0, 0, null))).Success.Should().BeFalse();

        create.Titles.Should().BeEmpty();
        store.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_workspace_is_reported_as_not_found()
    {
        var handler = new StartGoalCommandHandler(
            new CountingCreate { Error = "Repository workspace not found." }, new CapturingStore(), new StubPolicyStore(), NullLogger<StartGoalCommandHandler>.Instance);

        var result = await handler.HandleAsync(Command(Input("t1")));

        result.Success.Should().BeFalse();
        result.NotFound.Should().BeTrue();
    }

    private sealed class CapturingStore : IGoalStore
    {
        public List<Goal> Added { get; } = new();

        public Task AddAsync(Goal goal, CancellationToken cancellationToken = default)
        {
            Added.Add(goal);
            return Task.CompletedTask;
        }

        public Task<GoalState?> GetAsync(Guid goalId, Guid? repositoryWorkspaceId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<GoalState?>(null);

        public Task<IReadOnlyList<GoalState>> ListAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalState>>(Array.Empty<GoalState>());

        public Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task MarkAnalysisRequestedAsync(Guid goalTaskId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetNoteAsync(Guid goalTaskId, string? note, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetStatusAsync(Guid goalId, GoalStatus status, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingCreate : ICreateTaskCommandHandler
    {
        public List<string> Titles { get; } = new();

        public string? Error { get; set; }

        public Task<CreateTaskResult> HandleAsync(CreateTaskCommand command, CancellationToken cancellationToken = default)
        {
            if (Error is not null)
            {
                return Task.FromResult(new CreateTaskResult { Success = false, ErrorMessage = Error });
            }

            Titles.Add(command.Dto.Title);
            return Task.FromResult(new CreateTaskResult { Success = true, Task = new TaskDto { Id = Guid.NewGuid(), Title = command.Dto.Title } });
        }
    }
}
