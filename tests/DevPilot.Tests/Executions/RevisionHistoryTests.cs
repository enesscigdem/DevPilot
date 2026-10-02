using System.Text.Json;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.RepositoryWorkspaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

/// <summary>Every requested fix keeps its own view, and a running fix is what the sidebar shows.</summary>
public sealed class RevisionHistoryTests
{
    private static readonly DateTime Start = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstRunDone = Start.AddMinutes(4);
    private static readonly DateTime Request1 = Start.AddHours(1);
    private static readonly DateTime Request2 = Start.AddHours(2);

    [Fact]
    public void EachFixOnlySeesTheActivityBetweenItsOwnRequestAndTheNext()
    {
        var execution = Execution(TaskExecutionStatus.Running, result: null, changeRequestCount: 2, requestedAt: Request2);
        var rows = new[]
        {
            Row(1, "Rename it", Request1, Request1.AddMinutes(3), "Applied: first fix"),
            Row(2, "Add a test", Request2, null, null),
        };
        var activities = new List<ExecutionActivity>
        {
            Act(Request1.AddSeconds(5), ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed, "Reviewer feedback applied.",
                new ExecutionActivityMetadata(EventKind: "ReviewFeedbackApplied", ChangedFiles: new[] { "src/A.cs" }, ChangeSummary: "Renamed A.")),
            Act(Request1.AddSeconds(30), ExecutionStage.Build, ExecutionActivityStatus.Failed, "Build failed.",
                new ExecutionActivityMetadata(BuildPassed: false, RepositoryCheckId: "b", VerificationOutcome: "NeedsReview")),
            Act(Request2.AddSeconds(5), ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Started, "Applying",
                new ExecutionActivityMetadata(EventKind: "ApplyingReviewFeedback", ConsideredFiles: new[] { "src/B.cs" })),
        };

        var all = ExecutionRevisionBuilder.BuildAll(execution, rows, activities, ExecutionVerificationOutcome.Verified, _ => null);

        all.Should().HaveCount(2);
        var first = all[0];
        var second = all[1];
        first.Number.Should().Be(1);
        first.IsLatest.Should().BeFalse();
        first.State.Should().Be("Applied");
        first.Feedback.Should().Be("Rename it");
        first.Summary.Should().Be("Renamed A.");
        first.Files.Select(f => f.Path).Should().Equal("src/A.cs");
        first.Build!.Status.Should().Be("Failed", "the first fix's own build failure stays with the first fix");
        first.NextAction.Should().Be("None");
        first.WindowEnd.Should().Be(Request2);
        first.DurationMs.Should().Be(3 * 60 * 1000);

        second.Number.Should().Be(2);
        second.IsLatest.Should().BeTrue();
        second.State.Should().Be("Running");
        second.Feedback.Should().Be("Add a test");
        second.Summary.Should().BeNull("the first fix's summary must not leak into the active fix");
        second.Build.Should().BeNull("no check of the second fix has run");
        second.Files.Select(f => f.Path).Should().Equal("src/B.cs");
        second.NextAction.Should().Be("Wait");
    }

    [Fact]
    public void AFixCancelledWhileRunning_IsShownAsCancelled()
    {
        var execution = Execution(TaskExecutionStatus.Cancelled, result: "Cancelled: the requested fix was cancelled.", changeRequestCount: 1, requestedAt: Request1);
        var rows = new[] { Row(1, "x", Request1, Request1.AddMinutes(1), "Cancelled: the requested fix was cancelled.") };

        var all = ExecutionRevisionBuilder.BuildAll(execution, rows, Array.Empty<ExecutionActivity>(), ExecutionVerificationOutcome.Verified, _ => null);

        all.Single().State.Should().Be("Cancelled");
        all.Single().NextAction.Should().Be("RefineFeedback");
    }

    [Fact]
    public void AnExecutionFixedBeforeHistoryRowsExisted_StillShowsItsLatestFix()
    {
        var execution = Execution(TaskExecutionStatus.Completed, result: "Applied: ok", changeRequestCount: 1, requestedAt: Request1);

        var all = ExecutionRevisionBuilder.BuildAll(execution, Array.Empty<ExecutionRevision>(), Array.Empty<ExecutionActivity>(), ExecutionVerificationOutcome.Verified, _ => null);

        all.Should().ContainSingle().Which.Number.Should().Be(1);
    }

    [Fact]
    public async Task TheSidebarShowsARunningFixAsTheAgentsWork_EvenWhenTheExecutionWasRejectedOrHadFailingCi()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("RevisionSidebar_" + Guid.NewGuid().ToString("N"))
            .Options;
        using var db = new DevPilotDbContext(options);
        var ws = new RepositoryWorkspace { Id = Guid.NewGuid(), Owner = "o", Repository = "r", Branch = "main", Status = RepositoryWorkspaceStatus.Completed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.RepositoryWorkspaces.Add(ws);
        var task = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Fix", Status = DevelopmentTaskStatus.Executing, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.DevelopmentTasks.Add(task);
        var requested = DateTime.UtcNow.AddMinutes(-2);
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Running,
            LeaseToken = Guid.NewGuid(),
            LeaseExpiresAt = DateTime.UtcNow.AddSeconds(40),
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            StartedAt = DateTime.UtcNow.AddHours(-1),
            // Everything the first run and its delivery left behind, which used to make it "terminal".
            ReviewStatus = ExecutionReviewStatus.Rejected,
            CiStatus = ExecutionCiStatus.Failure,
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            ChangeRequestCount = 1,
            LastChangeRequest = "Fix the failing check",
            LastChangeRequestAt = requested,
        };
        db.TaskExecutions.Add(execution);
        db.ExecutionActivities.AddRange(
            new ExecutionActivity { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed, Message = "Build passed.", CreatedAt = DateTime.UtcNow.AddMinutes(-50) },
            new ExecutionActivity { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed, Message = "Tests passed.", CreatedAt = DateTime.UtcNow.AddMinutes(-49) },
            new ExecutionActivity { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Workspace, Status = ExecutionActivityStatus.Started, Message = "Requested changes started on the existing worktree.", CreatedAt = requested.AddSeconds(2) });
        await db.SaveChangesAsync();

        var overview = await new EfWorkspaceOverviewReader(db, NullLogger<EfWorkspaceOverviewReader>.Instance).ReadOverviewAsync(ws.Id);

        var active = overview!.ActiveAgentExecution;
        active.Should().NotBeNull("a fix that is running right now is the current work of the agent");
        active!.StartedAt.Should().Be(requested, "the elapsed time is the fix's, not the first run's");
        active.Stages.Single(s => s.StageKey == "build").State.Should().NotBe(WorkspaceStageState.Done, "the first run's green build is not this fix's");
        active.Stages.Single(s => s.StageKey == "review").State.Should().Be(WorkspaceStageState.Todo);
        active.Stages.Single(s => s.StageKey == "pr").State.Should().Be(WorkspaceStageState.Todo);
    }

    private static TaskExecution Execution(TaskExecutionStatus status, string? result, int changeRequestCount, DateTime requestedAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = status,
            StartedAt = Start,
            CompletedAt = status == TaskExecutionStatus.Running ? null : requestedAt.AddMinutes(3),
            InitialRunCompletedAt = FirstRunDone,
            LastChangeRequest = "x",
            LastChangeRequestAt = requestedAt,
            LastChangeRequestResult = result,
            ChangeRequestCount = changeRequestCount,
        };

    private static ExecutionRevision Row(int number, string feedback, DateTime requestedAt, DateTime? completedAt, string? result) =>
        new() { Id = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), Number = number, Feedback = feedback, RequestedAt = requestedAt, CompletedAt = completedAt, Result = result };

    private static ExecutionActivity Act(DateTime at, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? meta = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Stage = stage,
            Status = status,
            Message = message,
            CreatedAt = at,
            MetadataJson = meta == null ? null : JsonSerializer.Serialize(meta),
        };
}
