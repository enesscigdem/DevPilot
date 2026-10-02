using System.Text.Json;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class RevisionBuilderTests
{
    private static readonly DateTime Start = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstRunDone = Start.AddMinutes(4);
    private static readonly DateTime Requested = Start.AddHours(1);

    [Fact]
    public void WithoutARequestedFix_ThereIsNoRevision()
    {
        var e = Execution();
        e.ChangeRequestCount = 0;
        e.LastChangeRequestAt = null;

        ExecutionRevisionBuilder.Build(e, Array.Empty<ExecutionActivity>(), ExecutionVerificationOutcome.Verified, null)
            .Should().BeNull();
    }

    [Fact]
    public void WhileRunning_TheFirstRunsGreenChecksAndFilesAreNotShownAsTheFix()
    {
        var e = Execution(status: TaskExecutionStatus.Running, result: null);
        var activities = FirstRun().Concat(new[]
        {
            Act(1, ExecutionStage.Workspace, ExecutionActivityStatus.Started, "Requested changes started on the existing worktree."),
            Act(2, ExecutionStage.Workspace, ExecutionActivityStatus.Completed, "Existing worktree reused."),
            Act(3, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Started, "Applying reviewer feedback (revision 1).",
                new ExecutionActivityMetadata(EventKind: "ApplyingReviewFeedback", ConsideredFiles: new[] { "src/A.cs", "src/B.cs" })),
        }).ToList();

        var revision = ExecutionRevisionBuilder.Build(e, activities, ExecutionVerificationOutcome.Verified, null)!;

        revision.State.Should().Be("Running");
        revision.Phase.Should().Be("apply");
        revision.Build.Should().BeNull("no check of this round has run yet");
        revision.Test.Should().BeNull();
        revision.VerificationOutcome.Should().BeNull();
        revision.Steps.Select(s => (s.Key, s.State)).Should().Equal(
            ("prepare", "done"), ("apply", "active"), ("build", "todo"), ("test", "todo"), ("ready", "todo"));
        revision.Files.Should().OnlyContain(f => f.State == "Considered");
        revision.CompletedAt.Should().BeNull();
        revision.NextAction.Should().Be("Wait");
        revision.InitialRun.CompletedAt.Should().Be(FirstRunDone);
        revision.InitialRun.DurationMs.Should().Be(4 * 60 * 1000);
        revision.InitialRun.Outcome.Should().Be("Verified");
    }

    [Fact]
    public void AfterTheEditIsApplied_TheChangedFilesAreProven_AndChecksArePending()
    {
        var e = Execution(status: TaskExecutionStatus.Running, result: null);
        var activities = new List<ExecutionActivity>
        {
            Act(1, ExecutionStage.Workspace, ExecutionActivityStatus.Completed, "Existing worktree reused."),
            Act(2, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Started, "Applying",
                new ExecutionActivityMetadata(EventKind: "ApplyingReviewFeedback", ConsideredFiles: new[] { "src/A.cs", "src/B.cs" })),
            Act(3, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed, "Reviewer feedback applied.",
                new ExecutionActivityMetadata(EventKind: "ReviewFeedbackApplied", ChangedFiles: new[] { "src/A.cs" },
                    ChangeSummary: "Used a constant.", UnresolvedNote: "Could not add a test.")),
        };

        var revision = ExecutionRevisionBuilder.Build(e, activities, ExecutionVerificationOutcome.Verified, null)!;

        revision.Phase.Should().Be("build");
        revision.Summary.Should().Be("Used a constant.");
        revision.Unresolved.Should().Be("Could not add a test.");
        revision.Files.Select(f => (f.Path, f.State)).Should().Equal(("src/A.cs", "Changed"), ("src/B.cs", "Considered"));
        revision.FilesAreFinal.Should().BeFalse();
    }

    [Fact]
    public void WhenFinished_FilesAndTotalsComeFromTheSnapshotDiff_AndChecksFromThisRoundOnly()
    {
        var e = Execution(status: TaskExecutionStatus.Completed, result: "Applied: code updated.");
        e.CompletedAt = Requested.AddSeconds(95);
        e.RevisionBaseSnapshotSha = "base";
        e.RevisionResultSnapshotSha = "result";
        var activities = FirstRun().Concat(new[]
        {
            Act(2, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Started, "Applying",
                new ExecutionActivityMetadata(EventKind: "ApplyingReviewFeedback", ConsideredFiles: new[] { "src/A.cs", "src/B.cs" })),
            Act(3, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed, "Reviewer feedback applied.",
                new ExecutionActivityMetadata(EventKind: "ReviewFeedbackApplied", ChangedFiles: new[] { "src/A.cs" })),
            Act(4, ExecutionStage.Build, ExecutionActivityStatus.Completed, "Build passed.",
                new ExecutionActivityMetadata(BuildPassed: true, RepositoryCheckId: "dotnet:build", VerificationOutcome: "Verified")),
            Act(5, ExecutionStage.Test, ExecutionActivityStatus.Failed, "Test failed.",
                new ExecutionActivityMetadata(TestPassed: false, RepositoryCheckId: "dotnet:test", VerificationOutcome: "NeedsReview")),
        }).ToList();
        var diff = new ExecutionGitDiffResult(true, ChangedFiles: new[]
        {
            new ExecutionReviewFileDto("src/A.cs", "Modified", 7, 2),
            new ExecutionReviewFileDto("src/New.cs", "Added", 10, 0),
        }, DiffText: "diff");

        var revision = ExecutionRevisionBuilder.Build(e, activities, ExecutionVerificationOutcome.NeedsReview, diff)!;

        revision.State.Should().Be("Applied");
        revision.FilesAreFinal.Should().BeTrue();
        revision.Files.Select(f => (f.Path, f.State)).Should().Equal(
            ("src/A.cs", "Changed"), ("src/New.cs", "Created"), ("src/B.cs", "Unchanged"));
        revision.ChangedFileCount.Should().Be(2);
        revision.Additions.Should().Be(17);
        revision.Deletions.Should().Be(2);
        revision.HasDiff.Should().BeTrue();
        revision.Build!.Status.Should().Be("Passed");
        revision.Test!.Status.Should().Be("Failed");
        revision.VerificationOutcome.Should().Be("NeedsReview");
        revision.DurationMs.Should().Be(95_000);
        revision.NextAction.Should().Be("FixChecks");
        revision.Steps.Select(s => s.State).Should().Equal("done", "done", "done", "failed", "done");
    }

    [Fact]
    public void AFailedFix_ShowsWhyAndAsksForBetterFeedback()
    {
        var e = Execution(status: TaskExecutionStatus.Completed, result: "Failed: The AI answer was not valid JSON.");
        var activities = new List<ExecutionActivity>
        {
            Act(1, ExecutionStage.Workspace, ExecutionActivityStatus.Completed, "Existing worktree reused."),
            Act(2, ExecutionStage.Review, ExecutionActivityStatus.Failed, "Requested fix could not be applied",
                new ExecutionActivityMetadata(EventKind: "ReviewFeedbackFailed", UnresolvedNote: "The AI answer was not valid JSON.")),
        };

        var revision = ExecutionRevisionBuilder.Build(e, activities, ExecutionVerificationOutcome.Verified, null)!;

        revision.State.Should().Be("Failed");
        revision.Unresolved.Should().Contain("not valid JSON");
        revision.Steps.Select(s => s.State).Should().Equal("done", "failed", "skipped", "skipped", "skipped");
        revision.NextAction.Should().Be("RefineFeedback");
    }

    [Fact]
    public void ANoChangeFix_IsNotReportedAsApplied()
    {
        var e = Execution(status: TaskExecutionStatus.Completed, result: "No change: the AI did not change any code.");
        var activities = new List<ExecutionActivity>
        {
            Act(1, ExecutionStage.Review, ExecutionActivityStatus.Completed, "The AI made no code change",
                new ExecutionActivityMetadata(EventKind: "ReviewFeedbackNoChange", UnresolvedNote: "Already uses a constant.")),
        };

        var revision = ExecutionRevisionBuilder.Build(e, activities, ExecutionVerificationOutcome.Verified, null)!;

        revision.State.Should().Be("NoChange");
        revision.Unresolved.Should().Be("Already uses a constant.");
        revision.NextAction.Should().Be("RefineFeedback");
        revision.HasDiff.Should().BeFalse();
    }

    [Theory]
    [InlineData(ExecutionReviewStatus.Pending, ExecutionCommitStatus.None, ExecutionPushStatus.None, "Review")]
    [InlineData(ExecutionReviewStatus.Approved, ExecutionCommitStatus.None, ExecutionPushStatus.None, "Commit")]
    [InlineData(ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.None, "Push")]
    [InlineData(ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed, "PullRequestUpdated")]
    [InlineData(ExecutionReviewStatus.Rejected, ExecutionCommitStatus.None, ExecutionPushStatus.None, "RefineFeedback")]
    public void NextAction_FollowsTheDeliveryState(
        ExecutionReviewStatus review,
        ExecutionCommitStatus commit,
        ExecutionPushStatus push,
        string expected)
    {
        var e = Execution();
        e.ReviewStatus = review;
        e.CommitStatus = commit;
        e.PushStatus = push;
        e.PullRequestStatus = ExecutionPullRequestStatus.Open;

        ExecutionRevisionBuilder.NextAction(e, "Applied", ExecutionVerificationOutcome.Verified).Should().Be(expected);
    }

    [Fact]
    public void FailingChecksSendThePendingReviewToFixingThemFirst()
    {
        var e = Execution();
        e.ReviewStatus = ExecutionReviewStatus.Pending;

        ExecutionRevisionBuilder.NextAction(e, "Applied", ExecutionVerificationOutcome.NeedsReview).Should().Be("FixChecks");
    }

    [Fact]
    public void ScopeIsActiveOnlyWhileTheResultIsMissing_AndReviewAndPrStagesAreReset()
    {
        var running = Execution(status: TaskExecutionStatus.Running, result: null);
        var done = Execution(status: TaskExecutionStatus.Completed, result: "Applied: x");

        ExecutionRevisionScope.IsActive(running).Should().BeTrue();
        ExecutionRevisionScope.IsActive(done).Should().BeFalse();

        var stages = new List<ExecutionStageStepDto>
        {
            new() { StageKey = "implement", State = ExecutionStageStepState.Done },
            new() { StageKey = "review", State = ExecutionStageStepState.Done },
            new() { StageKey = "pr", State = ExecutionStageStepState.Done },
        };
        ExecutionRevisionScope.ForActiveRevision(stages).Select(s => s.State).Should().Equal(
            ExecutionStageStepState.Done, ExecutionStageStepState.Todo, ExecutionStageStepState.Todo);
    }

    private static TaskExecution Execution(TaskExecutionStatus status = TaskExecutionStatus.Completed, string? result = "Applied: ok") =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = status,
            StartedAt = Start,
            CompletedAt = FirstRunDone,
            InitialRunCompletedAt = FirstRunDone,
            LastChangeRequest = "Use a constant.",
            LastChangeRequestAt = Requested,
            LastChangeRequestResult = result,
            ChangeRequestCount = 1,
        };

    private static ExecutionActivity Act(
        int secondsAfterRequest,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        ExecutionActivityMetadata? meta = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Stage = stage,
            Status = status,
            Message = message,
            CreatedAt = Requested.AddSeconds(secondsAfterRequest),
            MetadataJson = meta == null ? null : JsonSerializer.Serialize(meta),
        };

    /// <summary>The first run, finished before the fix was requested, all green.</summary>
    private static IEnumerable<ExecutionActivity> FirstRun() => new[]
    {
        FirstRunAct(1, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed, "Developer Agent completed.", null),
        FirstRunAct(2, ExecutionStage.Build, ExecutionActivityStatus.Completed, "Build passed.",
            new ExecutionActivityMetadata(BuildPassed: true, RepositoryCheckId: "dotnet:build", VerificationOutcome: "Verified")),
        FirstRunAct(3, ExecutionStage.Test, ExecutionActivityStatus.Completed, "Tests passed.",
            new ExecutionActivityMetadata(TestPassed: true, RepositoryCheckId: "dotnet:test", VerificationOutcome: "Verified")),
    };

    private static ExecutionActivity FirstRunAct(
        int minute,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        ExecutionActivityMetadata? meta) =>
        new()
        {
            Id = Guid.NewGuid(),
            Stage = stage,
            Status = status,
            Message = message,
            CreatedAt = Start.AddMinutes(minute),
            MetadataJson = meta == null ? null : JsonSerializer.Serialize(meta),
        };
}
