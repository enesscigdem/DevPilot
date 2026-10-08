using DevPilot.Application.Goals;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Automation;
using DevPilot.Infrastructure.Goals;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevPilot.Tests.Goals;

public class EfGoalStoreTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly RepositoryWorkspace _workspace;

    public EfGoalStoreTests()
    {
        _db = new DevPilotDbContext(new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        _workspace = new RepositoryWorkspace { Id = Guid.NewGuid(), Owner = "o", Repository = "r", Branch = "main" };
        _db.RepositoryWorkspaces.Add(_workspace);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private (Goal Goal, DevelopmentTask[] Tasks) AddGoal(int taskCount = 2)
    {
        var tasks = Enumerable.Range(1, taskCount)
            .Select(i => new DevelopmentTask
            {
                Id = Guid.NewGuid(),
                RepositoryWorkspaceId = _workspace.Id,
                Title = $"Task {i}",
                Description = $"Description {i}",
                Status = DevelopmentTaskStatus.Draft,
                CreatedAt = DateTime.UtcNow
            })
            .ToArray();
        _db.DevelopmentTasks.AddRange(tasks);

        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = _workspace.Id,
            Title = "goal",
            Text = "text",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        for (var i = 0; i < taskCount; i++)
        {
            goal.Tasks.Add(new GoalTask
            {
                Id = Guid.NewGuid(),
                GoalId = goal.Id,
                DevelopmentTaskId = tasks[i].Id,
                Key = $"t{i + 1}",
                Position = i,
                Wave = i + 1,
                Areas = "src/a.ts\nsrc/b.ts",
                DependsOn = "",
                BlockedBy = i == 0 ? "" : "t1"
            });
        }

        _db.Goals.Add(goal);
        _db.SaveChanges();
        return (goal, tasks);
    }

    [Fact]
    public async Task A_saved_goal_is_read_back_with_its_tasks_in_order()
    {
        var (goal, tasks) = AddGoal(3);

        var state = await new EfGoalStore(_db).GetAsync(goal.Id);

        state.Should().NotBeNull();
        state!.Tasks.Select(t => t.Key).Should().Equal("t1", "t2", "t3");
        state.Tasks[1].Title.Should().Be("Task 2");
        state.Tasks[1].BlockedBy.Should().Equal("t1");
        state.Tasks[0].Areas.Should().Equal("src/a.ts", "src/b.ts");
        state.Tasks[0].TaskId.Should().Be(tasks[0].Id);
        state.Tasks.Should().OnlyContain(t => t.Execution == null && t.ImpactedFiles.Count == 0);
    }

    [Fact]
    public async Task The_latest_run_and_the_latest_completed_analysis_are_joined_in()
    {
        var (goal, tasks) = AddGoal(1);
        var old = new TaskExecution { Id = Guid.NewGuid(), DevelopmentTaskId = tasks[0].Id, Status = TaskExecutionStatus.Failed, CreatedAt = DateTime.UtcNow.AddHours(-2) };
        var latest = new TaskExecution
        {
            Id = Guid.NewGuid(), DevelopmentTaskId = tasks[0].Id, Status = TaskExecutionStatus.Completed, CreatedAt = DateTime.UtcNow,
            PullRequestStatus = ExecutionPullRequestStatus.Open, PullRequestNumber = 12, PullRequestUrl = "https://example/pr/12"
        };
        _db.TaskExecutions.AddRange(old, latest);
        _db.TaskImpactAnalyses.Add(new TaskImpactAnalysis
        {
            Id = Guid.NewGuid(), DevelopmentTaskId = tasks[0].Id, Status = ImpactAnalysisStatus.Completed, CreatedAt = DateTime.UtcNow,
            StructuredResult = new ImpactAnalysisResultData { ImpactedFiles = new List<ImpactedFile> { new() { FilePath = "src/Real.ts" } } }
        });
        _db.TaskImpactAnalyses.Add(new TaskImpactAnalysis
        {
            Id = Guid.NewGuid(), DevelopmentTaskId = tasks[0].Id, Status = ImpactAnalysisStatus.Failed, CreatedAt = DateTime.UtcNow.AddMinutes(5)
        });
        _db.SaveChanges();

        var task = (await new EfGoalStore(_db).GetAsync(goal.Id))!.Tasks.Single();

        task.Execution!.Id.Should().Be(latest.Id);
        task.Execution.PullRequestNumber.Should().Be(12);
        task.ImpactedFiles.Should().Equal("src/Real.ts");
        GoalPhases.Of(task, DateTime.UtcNow).Should().Be(GoalPhase.PullRequest);
    }

    [Fact]
    public async Task A_goal_of_another_workspace_is_not_found()
    {
        var (goal, _) = AddGoal();

        (await new EfGoalStore(_db).GetAsync(goal.Id, Guid.NewGuid())).Should().BeNull();
        (await new EfGoalStore(_db).GetAsync(goal.Id, _workspace.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Requesting_an_analysis_counts_the_attempt_and_notes_are_stored_and_cleared()
    {
        var (goal, _) = AddGoal(1);
        var store = new EfGoalStore(_db);
        var id = goal.Tasks.Single().Id;

        await store.MarkAnalysisRequestedAsync(id);
        await store.MarkAnalysisRequestedAsync(id);
        await store.SetNoteAsync(id, "waiting");

        var task = (await store.GetAsync(goal.Id))!.Tasks.Single();
        task.AnalysisAttempts.Should().Be(2);
        task.AnalysisRequestedAt.Should().NotBeNull();
        task.Note.Should().Be("waiting");

        await store.SetNoteAsync(id, null);
        (await store.GetAsync(goal.Id))!.Tasks.Single().Note.Should().BeNull();
    }

    [Fact]
    public async Task Completing_or_cancelling_a_goal_takes_it_out_of_the_active_list()
    {
        var (goal, _) = AddGoal();
        var other = AddGoal().Goal;
        var store = new EfGoalStore(_db);

        (await store.ListActiveIdsAsync()).Should().BeEquivalentTo(new[] { goal.Id, other.Id });

        await store.SetStatusAsync(goal.Id, GoalStatus.Completed);
        await store.SetStatusAsync(other.Id, GoalStatus.Cancelled);

        (await store.ListActiveIdsAsync()).Should().BeEmpty();
        var done = await store.GetAsync(goal.Id);
        done!.Status.Should().Be(GoalStatus.Completed);
        done.CompletedAt.Should().NotBeNull();
        (await store.GetAsync(other.Id))!.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Goals_are_listed_newest_first_for_their_workspace_only()
    {
        var first = AddGoal().Goal;
        first.CreatedAt = DateTime.UtcNow.AddHours(-1);
        var second = AddGoal().Goal;
        _db.SaveChanges();

        var list = await new EfGoalStore(_db).ListAsync(_workspace.Id);
        (await new EfGoalStore(_db).ListAsync(Guid.NewGuid())).Should().BeEmpty();

        list.Select(g => g.Id).Should().Equal(second.Id, first.Id);
    }

    [Fact]
    public async Task Automation_never_starts_tasks_that_belong_to_a_goal()
    {
        var (_, goalTasks) = AddGoal(1);
        var loose = new DevelopmentTask
        {
            Id = Guid.NewGuid(), RepositoryWorkspaceId = _workspace.Id, Title = "loose",
            Status = DevelopmentTaskStatus.AwaitingApproval, CreatedAt = DateTime.UtcNow
        };
        goalTasks[0].Status = DevelopmentTaskStatus.AwaitingApproval;
        _db.DevelopmentTasks.Add(loose);
        _db.SaveChanges();

        var ready = await new EfAutomationWorkReader(_db).GetTasksReadyToStartAsync(_workspace.Id, DateTime.UtcNow.AddDays(-1));

        ready.Select(t => t.Id).Should().Equal(loose.Id);
    }
}
