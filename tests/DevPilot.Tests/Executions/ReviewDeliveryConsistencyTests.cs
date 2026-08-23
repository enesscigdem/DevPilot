using System.Diagnostics;
using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Queries.GetExecutionById;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class ReviewDeliveryConsistencyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceDir;

    public ReviewDeliveryConsistencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilot_ReviewDelivery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceDir = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceDir);
        InitGitRepo(_workspaceDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public async Task PartiallyVerified_NoTestSuite_AllowNoChecksTrue_CanRequestMerge()
    {
        var (execution, activities) = CreateMergeReadyNoTestSuite();

        ExecutionVerificationEvaluator.DetermineOutcome(execution, activities)
            .Should().Be(ExecutionVerificationOutcome.PartiallyVerified);

        var eligibility = ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true);
        eligibility.CanMerge.Should().BeTrue();
        eligibility.BlockedReason.Should().BeNull();

        var review = await GetReviewAsync(execution, activities, allowNoChecks: true);
        review.CanRequestMerge.Should().BeTrue();
        review.MergeBlockedReason.Should().BeNull();
        review.VerificationOutcome.Should().Be("PartiallyVerified");
        review.Build.Status.Should().Be("Passed");
        review.Test.Status.Should().Be("Unknown");
    }

    [Fact]
    public async Task PartiallyVerified_NoTestSuite_AllowNoChecksFalse_MergeBlocked()
    {
        var (execution, activities) = CreateMergeReadyNoTestSuite();

        var eligibility = ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: false);
        eligibility.CanMerge.Should().BeFalse();
        eligibility.BlockedReason.Should().Contain("CI checks were not found");

        var review = await GetReviewAsync(execution, activities, allowNoChecks: false);
        review.CanRequestMerge.Should().BeFalse();
        review.MergeBlockedReason.Should().Contain("CI checks were not found");
    }

    [Fact]
    public async Task ActualTestFailure_MergeBlocked()
    {
        var (execution, activities) = CreateMergeReady(CreateFailedTestActivities);

        ExecutionVerificationEvaluator.DetermineOutcome(execution, activities)
            .Should().Be(ExecutionVerificationOutcome.NeedsReview);

        var eligibility = ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true);
        eligibility.CanMerge.Should().BeFalse();
        eligibility.BlockedReason.Should().Contain("NeedsReview");

        var review = await GetReviewAsync(execution, activities, allowNoChecks: true);
        review.CanRequestMerge.Should().BeFalse();
        review.Test.Status.Should().Be("Failed");
    }

    [Fact]
    public void TestsUnknown_BecauseExecutionStoppedBeforeTests_MergeBlocked()
    {
        var execution = CreateMergeReadyExecution();
        var activities = new List<ExecutionActivity>
        {
            Activity(execution.Id, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed, "Developer Agent completed.")
        };

        ExecutionVerificationEvaluator.DetermineOutcome(execution, activities)
            .Should().Be(ExecutionVerificationOutcome.VerificationUnavailable);

        var stages = ExecutionReviewStageClassifier.Classify(execution, activities);
        stages.Test.Status.Should().Be("Unknown");

        var eligibility = ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true);
        eligibility.CanMerge.Should().BeFalse();
        eligibility.BlockedReason.Should().NotContain("Local tests did not pass");
        eligibility.BlockedReason.Should().Match(reason =>
            reason!.Contains("Local build did not pass", StringComparison.Ordinal) ||
            reason.Contains("inconclusive", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("did not run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerificationInfrastructureError_RemainsDeliveryEligibleButMergeBlocked()
    {
        var execution = CreateMergeReadyExecution();
        var activities = new List<ExecutionActivity>
        {
            Activity(execution.Id, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed),
            Activity(
                execution.Id,
                ExecutionStage.Build,
                ExecutionActivityStatus.Completed,
                metadata: "{\"RepositoryCheckId\":\"build\",\"VerificationOutcome\":\"VerificationInfrastructureError\"}")
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.VerificationInfrastructureError);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeTrue();

        var eligibility = ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true);
        eligibility.CanMerge.Should().BeFalse();
        eligibility.BlockedReason.Should().Contain("inconclusive");
    }

    [Fact]
    public void NeedsReview_MergeBlocked()
    {
        var (execution, activities) = CreateMergeReady(CreateNeedsReviewBuildActivities);

        ExecutionVerificationEvaluator.DetermineOutcome(execution, activities)
            .Should().Be(ExecutionVerificationOutcome.NeedsReview);

        ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true)
            .CanMerge.Should().BeFalse();
    }

    [Fact]
    public void Failed_MergeBlocked()
    {
        var execution = CreateMergeReadyExecution();
        execution.Status = TaskExecutionStatus.Failed;
        var activities = new List<ExecutionActivity>
        {
            Activity(execution.Id, ExecutionStage.Workspace, ExecutionActivityStatus.Failed, "Workspace failed.")
        };

        ExecutionVerificationEvaluator.DetermineOutcome(execution, activities)
            .Should().Be(ExecutionVerificationOutcome.Failed);

        var eligibility = ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true);
        eligibility.CanMerge.Should().BeFalse();
        eligibility.BlockedReason.Should().Contain("Failed");
    }

    [Fact]
    public void Verified_NoChecksAllowed_MergeRemainsEligible()
    {
        var execution = CreateMergeReadyExecution();
        var activities = CreateVerifiedActivities(execution.Id);

        ExecutionVerificationEvaluator.DetermineOutcome(execution, activities)
            .Should().Be(ExecutionVerificationOutcome.Verified);

        ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true)
            .CanMerge.Should().BeTrue();
    }

    [Fact]
    public void NoNewRegressions_NoChecksAllowed_MergeRemainsEligible()
    {
        var execution = CreateMergeReadyExecution();
        var activities = new List<ExecutionActivity>
        {
            Activity(execution.Id, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed),
            Activity(execution.Id, ExecutionStage.Build, ExecutionActivityStatus.Completed, metadata: "{\"RepositoryCheckId\":\"build\"}"),
            Activity(
                execution.Id,
                ExecutionStage.Test,
                ExecutionActivityStatus.Completed,
                message: "No new regressions introduced. 2 pre-existing repository failure(s) matched clean baseline.",
                metadata: "{\"RepositoryCheckId\":\"test\",\"VerificationOutcome\":\"NoNewRegressions\",\"BaselineClassification\":\"PreExisting\",\"PreExistingFailureCount\":2}")
        };

        ExecutionVerificationEvaluator.DetermineOutcome(execution, activities)
            .Should().Be(ExecutionVerificationOutcome.NoNewRegressions);

        ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true)
            .CanMerge.Should().BeTrue();
    }

    [Fact]
    public async Task NoTestSuite_DoesNotProduceLocalTestsDidNotPass()
    {
        var (execution, activities) = CreateMergeReadyNoTestSuite();
        var stages = ExecutionReviewStageClassifier.Classify(execution, activities);

        stages.Test.Status.Should().Be("Unknown");
        stages.Test.DetailSummary.Should().Be("No local test suite was discovered.");
        stages.Test.DetailSummary.Should().NotContain("Local tests did not pass");

        var eligibility = ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true);
        (eligibility.BlockedReason ?? string.Empty).Should().NotContain("Local tests did not pass");

        var review = await GetReviewAsync(execution, activities, allowNoChecks: true);
        review.Test.DetailSummary.Should().Be("No local test suite was discovered.");
        (review.MergeBlockedReason ?? string.Empty).Should().NotContain("Local tests did not pass");
    }

    [Fact]
    public async Task RealTestFailure_StillProducesFailureWording()
    {
        var (execution, activities) = CreateMergeReady(CreateFailedTestActivities);
        var stages = ExecutionReviewStageClassifier.Classify(execution, activities);

        stages.Test.Status.Should().Be("Failed");
        stages.Test.DetailSummary.Should().Contain("failed");
        stages.Test.DetailSummary.Should().NotBe("No local test suite was discovered.");

        var review = await GetReviewAsync(execution, activities, allowNoChecks: true);
        review.Test.Status.Should().Be("Failed");
        review.Test.DetailSummary.Should().Contain("failed");
    }

    [Fact]
    public async Task CompletedNeedsReview_ExposesRetryAndRemainsNonApprovable()
    {
        var execution = CreateReviewableExecution();
        var activities = CreateNeedsReviewBuildActivities(execution.Id);

        var review = await GetReviewAsync(execution, activities, allowNoChecks: true);
        review.CanRetry.Should().BeTrue();
        review.CanRequestMerge.Should().BeFalse();
        review.VerificationOutcome.Should().Be("NeedsReview");

        var byId = await GetExecutionByIdAsync(execution, activities);
        byId.CanRetry.Should().BeTrue();
        byId.VerificationOutcome.Should().Be("NeedsReview");

        var approve = new ApproveExecutionReviewCommandHandler(
            new SingleExecutionRepository(execution),
            new SeededActivityRepository(activities),
            new MockWorkspaceManager(),
            new MockFingerprintCalculator(),
            new MockActivityRecorder(),
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        var approveResult = await approve.HandleAsync(new ApproveExecutionReviewCommand(execution.Id, "fp"));
        approveResult.Status.Should().Be(ApproveExecutionReviewResultStatus.Conflict);
        approveResult.ErrorMessage.Should().Contain("verification outcome is 'NeedsReview'");
    }

    [Fact]
    public async Task RunningExecution_CannotRetry()
    {
        var execution = CreateReviewableExecution();
        execution.Status = TaskExecutionStatus.Running;
        var activities = CreateNeedsReviewBuildActivities(execution.Id);

        var byId = await GetExecutionByIdAsync(execution, activities, hasActive: true);
        byId.CanRetry.Should().BeFalse();

        var reviewResult = await CreateReviewHandler(execution, activities, allowNoChecks: true)
            .HandleAsync(new GetExecutionReviewQuery(execution.Id));
        reviewResult.Status.Should().Be(ExecutionReviewResultStatus.Conflict);
        reviewResult.ErrorMessage.Should().Contain("Running");
    }

    [Fact]
    public void Pipeline_CompletedNeedsReview_IsWarningNotHardFailure()
    {
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            Status = DevelopmentTaskStatus.Completed,
            Title = "Task"
        };
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending
        };
        var activities = CreateNeedsReviewBuildActivities(execution.Id);
        activities.Insert(0, Activity(execution.Id, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed));

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, CompletedAnalysis(task.Id), activities);
        stages[4].StageKey.Should().Be("build");
        stages[4].State.Should().Be(ExecutionStageStepState.NeedsReview);
        stages[4].State.Should().NotBe(ExecutionStageStepState.Failed);
    }

    [Fact]
    public void Pipeline_HardExecutionFailure_RemainsFailed()
    {
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            Status = DevelopmentTaskStatus.Failed,
            Title = "Task"
        };
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Failed
        };
        var activities = new List<ExecutionActivity>
        {
            Activity(execution.Id, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Failed, "Developer Agent failed.")
        };

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, CompletedAnalysis(task.Id), activities);
        stages[3].State.Should().Be(ExecutionStageStepState.Failed);
        stages[4].State.Should().NotBe(ExecutionStageStepState.NeedsReview);
    }

    [Fact]
    public async Task BuildRepositoryCheckCard_RemainsFailedWhenBuildFailed()
    {
        var execution = CreateReviewableExecution();
        var activities = CreateNeedsReviewBuildActivities(execution.Id);

        var review = await GetReviewAsync(execution, activities, allowNoChecks: true);
        review.Build.Status.Should().Be("Failed");
        review.VerificationOutcome.Should().Be("NeedsReview");

        var stages = ExecutionStageEvaluator.EvaluateStages(
            execution,
            execution.DevelopmentTask,
            CompletedAnalysis(execution.DevelopmentTaskId),
            activities);
        stages[4].State.Should().Be(ExecutionStageStepState.NeedsReview);
    }

    [Fact]
    public async Task ReviewAndMergeEligibility_UseTheSameSemantics()
    {
        var cases = new[]
        {
            CreateMergeReadyNoTestSuite(),
            CreateMergeReady(CreateVerifiedActivities),
            CreateMergeReady(CreateFailedTestActivities),
            CreateMergeReady(CreateNeedsReviewBuildActivities)
        };

        foreach (var (execution, activities) in cases)
        {
            var eligibility = ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks: true);
            var review = await GetReviewAsync(execution, activities, allowNoChecks: true);

            review.CanRequestMerge.Should().Be(eligibility.CanMerge);
            review.MergeBlockedReason.Should().Be(eligibility.BlockedReason);
        }
    }

    private async Task<ExecutionReviewDto> GetReviewAsync(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities,
        bool allowNoChecks)
    {
        var result = await CreateReviewHandler(execution, activities, allowNoChecks)
            .HandleAsync(new GetExecutionReviewQuery(execution.Id));
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review.Should().NotBeNull();
        return result.Review!;
    }

    private async Task<ExecutionDto> GetExecutionByIdAsync(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities,
        bool hasActive = false)
    {
        var handler = new GetExecutionByIdQueryHandler(
            new SingleExecutionRepository(execution, hasActive),
            new SeededActivityRepository(activities),
            new EmptyImpactAnalysisRepository(),
            Options.Create(new MergePolicyOptions()));

        var result = await handler.HandleAsync(new GetExecutionByIdQuery(execution.Id));
        result.Found.Should().BeTrue();
        result.Execution.Should().NotBeNull();
        return result.Execution!;
    }

    private GetExecutionReviewQueryHandler CreateReviewHandler(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities,
        bool allowNoChecks)
    {
        return new GetExecutionReviewQueryHandler(
            new SingleExecutionRepository(execution),
            new MockWorkspaceManager(),
            new MockDiffReader(),
            new MockFingerprintCalculator(),
            new SeededActivityRepository(activities),
            Options.Create(new MergePolicyOptions { AllowNoChecks = allowNoChecks }),
            NullLogger<GetExecutionReviewQueryHandler>.Instance);
    }

    private (TaskExecution Execution, List<ExecutionActivity> Activities) CreateMergeReadyNoTestSuite()
    {
        var execution = CreateCommittedMergeReadyExecution();
        return (execution, CreateNoTestSuiteActivities(execution.Id));
    }

    private (TaskExecution Execution, List<ExecutionActivity> Activities) CreateMergeReady(
        Func<Guid, List<ExecutionActivity>> activityFactory)
    {
        var execution = CreateCommittedMergeReadyExecution();
        return (execution, activityFactory(execution.Id));
    }

    private TaskExecution CreateReviewableExecution()
    {
        var workspace = CreateWorkspace();
        var task = CreateTask(workspace);
        return new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending,
            WorkspacePath = _workspaceDir,
            BranchName = "main",
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            CompletedAt = DateTime.UtcNow
        };
    }

    private TaskExecution CreateMergeReadyExecution()
    {
        var workspace = CreateWorkspace();
        var task = CreateTask(workspace);
        const string sha = "a451333161ec91ce5edffc1770ec7617818e2b9d";
        return new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            CommitStatus = ExecutionCommitStatus.Committed,
            CommitSha = sha,
            ApprovedChangeFingerprint = "fp",
            BaseCommitSha = sha,
            CommittedAt = DateTime.UtcNow.AddMinutes(-20),
            PushStatus = ExecutionPushStatus.Pushed,
            BranchName = "devpilot/task-1",
            RemoteBranchName = "devpilot/task-1",
            RemoteCommitSha = sha,
            PushedAt = DateTime.UtcNow.AddMinutes(-15),
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            PullRequestNumber = 42,
            PullRequestUrl = "https://github.com/enesscigdem/DevPilot/pull/42",
            PullRequestBaseBranch = "master",
            PullRequestRemoteState = ExecutionPullRequestRemoteState.Open,
            PullRequestIntegrityStatus = ExecutionPullRequestIntegrityStatus.Valid,
            CiStatus = ExecutionCiStatus.NoChecks,
            MergeStatus = ExecutionMergeStatus.None,
            WorkspacePath = _workspaceDir,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = DateTime.UtcNow.AddMinutes(-10)
        };
    }

    private TaskExecution CreateCommittedMergeReadyExecution()
    {
        var execution = CreateMergeReadyExecution();
        var filePath = Path.Combine(_workspaceDir, "src", "Calculator.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "public class Calculator {}");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Base commit");
        var baseSha = RunGitOutput(_workspaceDir, "rev-parse", "HEAD").Trim();

        File.WriteAllText(filePath, "public class Calculator { public int Add(int a, int b) => a + b; }");
        RunGit(_workspaceDir, "add", ".");
        var treeSha = RunGitOutput(_workspaceDir, "write-tree").Trim();
        var commitMsg = $"Execute task changes\n\nDevPilot-Execution: {execution.Id}\n";
        var commitSha = RunGitOutput(_workspaceDir, "commit-tree", treeSha, "-p", baseSha, "-m", commitMsg).Trim();
        RunGit(_workspaceDir, "update-ref", "refs/heads/main", commitSha);

        execution.WorkspacePath = _workspaceDir;
        execution.BranchName = "main";
        execution.RemoteBranchName = "main";
        execution.BaseCommitSha = baseSha;
        execution.CommitSha = commitSha;
        execution.RemoteCommitSha = commitSha;
        execution.ApprovedChangeFingerprint = "fp";
        return execution;
    }

    private static List<ExecutionActivity> CreateNoTestSuiteActivities(Guid executionId) =>
    [
        Activity(executionId, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed, "Developer Agent completed."),
        Activity(
            executionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Completed,
            "Repository checks passed.",
            "{\"RepositoryCheckId\":\"build\"}")
    ];

    private static List<ExecutionActivity> CreateVerifiedActivities(Guid executionId) =>
    [
        Activity(executionId, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed),
        Activity(executionId, ExecutionStage.Build, ExecutionActivityStatus.Completed, metadata: "{\"RepositoryCheckId\":\"build\"}"),
        Activity(executionId, ExecutionStage.Test, ExecutionActivityStatus.Completed, "All tests passed.", "{\"RepositoryCheckId\":\"test\"}")
    ];

    private static List<ExecutionActivity> CreateFailedTestActivities(Guid executionId) =>
    [
        Activity(executionId, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed),
        Activity(executionId, ExecutionStage.Build, ExecutionActivityStatus.Completed, metadata: "{\"RepositoryCheckId\":\"build\"}"),
        Activity(
            executionId,
            ExecutionStage.Test,
            ExecutionActivityStatus.Failed,
            "Tests failed",
            "{\"RepositoryCheckId\":\"test\",\"VerificationOutcome\":\"NeedsReview\"}")
    ];

    private static List<ExecutionActivity> CreateNeedsReviewBuildActivities(Guid executionId) =>
    [
        Activity(executionId, ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed),
        Activity(
            executionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Failed,
            "Build failed",
            "{\"RepositoryCheckId\":\"build\",\"VerificationOutcome\":\"NeedsReview\"}")
    ];

    private static ExecutionActivity Activity(
        Guid executionId,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message = "",
        string? metadata = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            Stage = stage,
            Status = status,
            Message = message,
            MetadataJson = metadata,
            CreatedAt = DateTime.UtcNow
        };

    private static RepositoryWorkspace CreateWorkspace() =>
        new()
        {
            Id = Guid.NewGuid(),
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "main",
            LocalPath = "/repo",
            Status = RepositoryWorkspaceStatus.Completed
        };

    private static DevelopmentTask CreateTask(RepositoryWorkspace workspace) =>
        new()
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            RepositoryWorkspace = workspace,
            Title = "Acceptance task",
            Status = DevelopmentTaskStatus.Completed,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static TaskImpactAnalysis CompletedAnalysis(Guid taskId) =>
        new()
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow
        };

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "Review Delivery Test");
        RunGit(path, "config", "user.email", "review-delivery@test.local");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        using var process = Process.Start(CreateGitStartInfo(workingDirectory, args))!;
        process.WaitForExit();
    }

    private static string RunGitOutput(string workingDirectory, params string[] args)
    {
        using var process = Process.Start(CreateGitStartInfo(workingDirectory, args))!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static ProcessStartInfo CreateGitStartInfo(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private sealed class SingleExecutionRepository : IExecutionRepository
    {
        private readonly TaskExecution _execution;
        private readonly bool _hasActive;

        public SingleExecutionRepository(TaskExecution execution, bool hasActive = false)
        {
            _execution = execution;
            _hasActive = hasActive;
        }

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<TaskExecution?>(id == _execution.Id ? _execution : null);

        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskExecution>>(new[] { _execution });

        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_hasActive);

        public Task<bool> HasFailedExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_execution.Status == TaskExecutionStatus.Failed);

        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClaimAsRunningAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RenewHeartbeatAsync(Guid executionId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CompleteWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> FailWithLeaseAsync(Guid executionId, Guid leaseToken, string errorMessage, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetModelAsync(Guid executionId, string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetReviewDecisionAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string? rejectionReason, CancellationToken cancellationToken = default)
        {
            if (_execution.Id != executionId || _execution.ReviewStatus != expectedStatus)
            {
                return Task.FromResult(false);
            }

            _execution.ReviewStatus = newStatus;
            _execution.ReviewDecidedAt = decidedAt;
            _execution.ReviewRejectionReason = rejectionReason;
            return Task.FromResult(true);
        }

        public Task<bool> TrySetReviewDecisionWithFingerprintAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string fingerprint, string? rejectionReason, CancellationToken cancellationToken = default)
        {
            if (_execution.Id != executionId || _execution.ReviewStatus != expectedStatus)
            {
                return Task.FromResult(false);
            }

            _execution.ReviewStatus = newStatus;
            _execution.ReviewDecidedAt = decidedAt;
            _execution.ApprovedChangeFingerprint = fingerprint;
            _execution.ReviewRejectionReason = rejectionReason;
            return Task.FromResult(true);
        }

        public Task<bool> TryClaimNewCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, string baseCommitSha, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStaleCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetCommitCompletedAsync(Guid executionId, Guid attemptId, string commitSha, DateTime committedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetCommitFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryClaimNewPushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStalePushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetPushCompletedAsync(Guid executionId, Guid attemptId, string remoteBranchName, string remoteCommitSha, DateTime pushedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPushFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryClaimNewPullRequestLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStalePullRequestLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetPullRequestOpenedAsync(Guid executionId, Guid attemptId, int pullRequestNumber, string pullRequestUrl, string baseBranch, DateTime createdAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPullRequestFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryClaimPullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStalePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ReleasePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime attemptAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ReplacePullRequestTrackingSnapshotAsync(Guid executionId, Guid attemptId, ExecutionPullRequestRemoteState remoteState, ExecutionPullRequestIntegrityStatus integrityStatus, DateTime? closedAt, DateTime? mergedAt, ExecutionCiStatus ciStatus, IReadOnlyList<ExecutionCiCheck> checks, DateTime syncedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryClaimMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan syncLeaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStaleMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan mergeLeaseTimeout, TimeSpan syncLeaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetExecutionMergedAsync(Guid executionId, Guid attemptId, string mergeCommitSha, DateTime mergedAt, string mergeMethod = "merge", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMergeFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RequestCancellationAsync(Guid executionId, string? reason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AcknowledgeCancellationWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCancellationRequestedAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleRunningExecutionsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class SeededActivityRepository : IExecutionActivityRepository
    {
        private readonly IReadOnlyList<ExecutionActivity> _activities;

        public SeededActivityRepository(IReadOnlyList<ExecutionActivity> activities)
        {
            _activities = activities;
        }

        public Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_activities);
    }

    private sealed class EmptyImpactAnalysisRepository : IImpactAnalysisRepository
    {
        public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TaskImpactAnalysis?>(null);

        public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> StartAnalysisAtomicAsync(TaskImpactAnalysis analysis, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasActiveAnalysisForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleAnalysesAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class MockWorkspaceManager : IExecutionWorkspaceManager
    {
        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionWorkspaceResult("/ws", "feature", true, null));

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceVerificationResult(true, true, true, true, null));
    }

    private sealed class MockDiffReader : IExecutionGitDiffReader
    {
        public Task<ExecutionGitDiffResult> ReadWorkspaceDiffAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionGitDiffResult(true, null, new[] { new ExecutionReviewFileDto("src/Calculator.cs", "Modified") }, "diff"));

        public Task<ExecutionGitDiffResult> ReadCommittedDiffAsync(string workspacePath, string baseCommitSha, string commitSha, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionGitDiffResult(true, null, new[] { new ExecutionReviewFileDto("src/Calculator.cs", "Modified") }, "diff"));
    }

    private sealed class MockFingerprintCalculator : IExecutionChangeFingerprintCalculator
    {
        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionFingerprintResult(true, "fp", "sha", false, 1));

        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(string workspacePath, string treeSha, string baseHeadSha, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionFingerprintResult(true, "fp", baseHeadSha, false, 1));
    }

    private sealed class MockActivityRecorder : IExecutionActivityRecorder
    {
        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
