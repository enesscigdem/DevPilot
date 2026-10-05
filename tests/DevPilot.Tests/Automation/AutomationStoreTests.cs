using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Automation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevPilot.Tests.Automation;

public class AutomationStoreTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly RepositoryWorkspace _workspace;

    public AutomationStoreTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new DevPilotDbContext(options);
        _workspace = new RepositoryWorkspace { Id = Guid.NewGuid(), Owner = "o", Repository = "r", Branch = "main" };
        _db.RepositoryWorkspaces.Add(_workspace);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private AutomationPolicy Request(AutomationLevel level, bool paused = false) =>
        new() { RepositoryWorkspaceId = _workspace.Id, Level = level, Paused = paused, ProtectedPaths = "a/**" };

    private DevelopmentTask AddTask(DevelopmentTaskStatus status, DateTime createdAt, DevelopmentTaskPriority priority = DevelopmentTaskPriority.Medium)
    {
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = _workspace.Id,
            Title = "t",
            Status = status,
            Priority = priority,
            CreatedAt = createdAt
        };
        _db.DevelopmentTasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    private TaskExecution AddExecution(DevelopmentTask task, TaskExecutionStatus status, Action<TaskExecution>? configure = null)
    {
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = status,
            CreatedAt = task.CreatedAt.AddMinutes(1)
        };
        configure?.Invoke(execution);
        _db.TaskExecutions.Add(execution);
        _db.SaveChanges();
        return execution;
    }

    [Fact]
    public async Task Saving_for_an_unknown_workspace_returns_null()
    {
        var store = new EfAutomationPolicyStore(_db);

        var saved = await store.SaveAsync(new AutomationPolicy { RepositoryWorkspaceId = Guid.NewGuid(), Level = AutomationLevel.AutoPr });

        saved.Should().BeNull();
    }

    [Fact]
    public async Task Switching_automation_on_starts_a_new_window_and_changing_level_keeps_it()
    {
        var store = new EfAutomationPolicyStore(_db);

        var manual = await store.SaveAsync(Request(AutomationLevel.Manual));
        manual!.ActiveSince.Should().BeNull();

        var semi = await store.SaveAsync(Request(AutomationLevel.SemiAuto));
        semi!.ActiveSince.Should().NotBeNull();
        var started = semi.ActiveSince;

        var full = await store.SaveAsync(Request(AutomationLevel.FullAuto));
        full!.ActiveSince.Should().Be(started);

        var off = await store.SaveAsync(Request(AutomationLevel.Manual));
        off!.ActiveSince.Should().BeNull();

        var again = await store.SaveAsync(Request(AutomationLevel.AutoPr));
        again!.ActiveSince.Should().BeOnOrAfter(started!.Value);
        (await _db.AutomationPolicies.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Only_automatic_unpaused_policies_are_active()
    {
        var store = new EfAutomationPolicyStore(_db);

        await store.SaveAsync(Request(AutomationLevel.AutoPr, paused: true));
        (await store.ListActiveAsync()).Should().BeEmpty();

        await store.SaveAsync(Request(AutomationLevel.AutoPr, paused: false));
        (await store.ListActiveAsync()).Should().ContainSingle();

        await store.SaveAsync(Request(AutomationLevel.Manual));
        (await store.ListActiveAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Tasks_created_before_automation_was_enabled_are_left_alone()
    {
        var since = DateTime.UtcNow;
        AddTask(DevelopmentTaskStatus.AwaitingApproval, since.AddHours(-1));
        var fresh = AddTask(DevelopmentTaskStatus.AwaitingApproval, since.AddMinutes(1));

        var ready = await new EfAutomationWorkReader(_db).GetTasksReadyToStartAsync(_workspace.Id, since);

        ready.Select(t => t.Id).Should().Equal(fresh.Id);
    }

    [Fact]
    public async Task A_cancelled_task_returned_to_approved_is_not_restarted_but_a_never_run_one_is()
    {
        var since = DateTime.UtcNow.AddHours(-1);
        var cancelled = AddTask(DevelopmentTaskStatus.Approved, since.AddMinutes(5));
        AddExecution(cancelled, TaskExecutionStatus.Cancelled);
        var neverRun = AddTask(DevelopmentTaskStatus.Approved, since.AddMinutes(6));

        var ready = await new EfAutomationWorkReader(_db).GetTasksReadyToStartAsync(_workspace.Id, since);

        ready.Select(t => t.Id).Should().Equal(neverRun.Id);
    }

    [Fact]
    public async Task Ready_tasks_come_highest_priority_first_and_other_statuses_are_ignored()
    {
        var since = DateTime.UtcNow.AddHours(-1);
        var low = AddTask(DevelopmentTaskStatus.AwaitingApproval, since.AddMinutes(1), DevelopmentTaskPriority.Low);
        var critical = AddTask(DevelopmentTaskStatus.AwaitingApproval, since.AddMinutes(2), DevelopmentTaskPriority.Critical);
        AddTask(DevelopmentTaskStatus.Draft, since.AddMinutes(3), DevelopmentTaskPriority.Critical);
        AddTask(DevelopmentTaskStatus.Rejected, since.AddMinutes(4), DevelopmentTaskPriority.Critical);
        AddTask(DevelopmentTaskStatus.Executing, since.AddMinutes(5), DevelopmentTaskPriority.Critical);

        var ready = await new EfAutomationWorkReader(_db).GetTasksReadyToStartAsync(_workspace.Id, since);

        ready.Select(t => t.Id).Should().Equal(critical.Id, low.Id);
    }

    [Fact]
    public async Task Active_executions_are_counted_for_the_capacity_limit()
    {
        var task = AddTask(DevelopmentTaskStatus.Executing, DateTime.UtcNow.AddHours(-1));
        AddExecution(task, TaskExecutionStatus.Running);
        AddExecution(task, TaskExecutionStatus.Pending);
        AddExecution(task, TaskExecutionStatus.Completed);
        AddExecution(task, TaskExecutionStatus.Failed);

        (await new EfAutomationWorkReader(_db).CountActiveExecutionsAsync(_workspace.Id)).Should().Be(2);
    }

    [Fact]
    public async Task Deliverable_executions_exclude_rejected_failed_in_flight_and_already_opened()
    {
        var since = DateTime.UtcNow.AddHours(-1);
        var task = AddTask(DevelopmentTaskStatus.Completed, since.AddMinutes(1));

        var pending = AddExecution(task, TaskExecutionStatus.Completed);
        var approvedMidway = AddExecution(task, TaskExecutionStatus.Completed, e =>
        {
            e.ReviewStatus = ExecutionReviewStatus.Approved;
            e.CommitStatus = ExecutionCommitStatus.Committed;
        });
        AddExecution(task, TaskExecutionStatus.Completed, e => e.ReviewStatus = ExecutionReviewStatus.Rejected);
        AddExecution(task, TaskExecutionStatus.Completed, e => e.CommitStatus = ExecutionCommitStatus.Failed);
        AddExecution(task, TaskExecutionStatus.Completed, e => e.PushStatus = ExecutionPushStatus.InProgress);
        AddExecution(task, TaskExecutionStatus.Completed, e => e.PullRequestStatus = ExecutionPullRequestStatus.Open);
        AddExecution(task, TaskExecutionStatus.Running);

        var ids = await new EfAutomationWorkReader(_db).GetDeliverableExecutionIdsAsync(_workspace.Id, since);

        ids.Should().BeEquivalentTo(new[] { pending.Id, approvedMidway.Id });
    }

    [Fact]
    public async Task Only_open_unmerged_pull_requests_are_candidates_for_merging()
    {
        var since = DateTime.UtcNow.AddHours(-1);
        var task = AddTask(DevelopmentTaskStatus.Completed, since.AddMinutes(1));

        var open = AddExecution(task, TaskExecutionStatus.Completed, e => e.PullRequestStatus = ExecutionPullRequestStatus.Open);
        AddExecution(task, TaskExecutionStatus.Completed, e =>
        {
            e.PullRequestStatus = ExecutionPullRequestStatus.Open;
            e.MergeStatus = ExecutionMergeStatus.Merged;
        });
        AddExecution(task, TaskExecutionStatus.Completed);

        var ids = await new EfAutomationWorkReader(_db).GetOpenPullRequestExecutionIdsAsync(_workspace.Id, since);

        ids.Should().Equal(open.Id);
    }
}
