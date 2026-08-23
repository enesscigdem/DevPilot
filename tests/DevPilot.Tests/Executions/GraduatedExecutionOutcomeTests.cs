using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class GraduatedExecutionOutcomeTests
{
    [Fact]
    public void DetermineOutcome_AllPassed_ReturnsVerified()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new() { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed },
            new() { Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.Verified);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeTrue();
    }

    [Fact]
    public void DetermineOutcome_BuildOnlyPassed_ReturnsPartiallyVerified()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new() { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.PartiallyVerified);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeTrue();
    }

    [Fact]
    public void DetermineOutcome_PreExistingFailures_ReturnsNoNewRegressions()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = "{\"VerificationOutcome\":\"NoNewRegressions\",\"PreExistingFailureCount\":2,\"NewRegressionCount\":0}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.NoNewRegressions);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeTrue();
    }

    [Fact]
    public void DetermineOutcome_NeedsReviewActivity_ReturnsNeedsReview()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = "{\"VerificationOutcome\":\"NeedsReview\",\"NewRegressionCount\":1}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.NeedsReview);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeFalse();
    }

    [Fact]
    public void DetermineOutcome_VerificationUnavailable_ReturnsVerificationUnavailable()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Execution,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = "{\"VerificationOutcome\":\"VerificationUnavailable\",\"EventKind\":\"ReadyForReview\"}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.VerificationUnavailable);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeTrue();
    }

    [Fact]
    public void DetermineOutcome_InfrastructureError_ReturnsVerificationInfrastructureError()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = "{\"VerificationOutcome\":\"VerificationInfrastructureError\"}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.VerificationInfrastructureError);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeTrue();
    }

    [Fact]
    public void DetermineOutcome_ExecutionFailed_ReturnsFailed()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Failed };
        var activities = new List<ExecutionActivity>();

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.Failed);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeFalse();
    }

    [Fact]
    public void BaselineComparison_Inconclusive_ProducesUnknownWithZeroNewRegressions()
    {
        var taskFailures = new List<NormalizedFailureItem>
        {
            new("F1", "Test1", "Failure", "Details")
        };

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures,
            Array.Empty<NormalizedFailureItem>(),
            baselineCheckSucceeded: false,
            baselineIsInconclusive: true);

        comparison.Classification.Should().Be(BaselineFailureClassification.Unknown);
        comparison.NewRegressionCount.Should().Be(0);
        comparison.NewRegressions.Should().BeEmpty();
    }

    [Fact]
    public void ActionableEvidence_FiltersToOnlyNewAndChangedRegressions()
    {
        var newRegression = new NormalizedFailureItem("N1", "NewFailingTest", "Err2", "New regression");

        var actionableEvidence = ExecutionDiagnosticEvidence.CreateActionableTestEvidence(
            new[] { newRegression },
            "stdout containing PreExistingTest and NewFailingTest",
            "stderr",
            "Test run failed");

        actionableEvidence.TestName.Should().Be("NewFailingTest");
        actionableEvidence.RelevantLines.Should().Contain("Err2");
    }

    [Fact]
    public async Task ApproveCommand_NeedsReviewOutcome_BlocksApproval()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending,
            WorkspacePath = "/ws",
            BranchName = "feature"
        };
        repo.Executions[executionId] = execution;

        var activityRepo = new TestExecutionActivityRepository();
        activityRepo.Activities.Add(new ExecutionActivity
        {
            ExecutionId = executionId,
            Stage = ExecutionStage.Test,
            Status = ExecutionActivityStatus.Failed,
            MetadataJson = "{\"VerificationOutcome\":\"NeedsReview\"}"
        });

        var handler = new ApproveExecutionReviewCommandHandler(
            repo,
            activityRepo,
            new MockWorkspaceManager(),
            new MockFingerprintCalculator(),
            new MockActivityRecorder(),
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ApproveExecutionReviewCommand(executionId, "fp"));
        result.Status.Should().Be(ApproveExecutionReviewResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("verification outcome is 'NeedsReview'");
    }

    [Fact]
    public async Task CommitCommand_NeedsReviewOutcome_BlocksCommit()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            ApprovedChangeFingerprint = "fp",
            WorkspacePath = "/ws",
            BranchName = "feature"
        };
        repo.Executions[executionId] = execution;

        var activityRepo = new TestExecutionActivityRepository();
        activityRepo.Activities.Add(new ExecutionActivity
        {
            ExecutionId = executionId,
            Stage = ExecutionStage.Test,
            Status = ExecutionActivityStatus.Failed,
            MetadataJson = "{\"VerificationOutcome\":\"NeedsReview\"}"
        });

        var handler = new CommitExecutionCommandHandler(
            repo,
            new MockWorkspaceManager(),
            new MockGitCommitService(),
            new MockActivityRecorder(),
            NullLogger<CommitExecutionCommandHandler>.Instance,
            activityRepository: activityRepo);

        var result = await handler.HandleAsync(new CommitExecutionCommand(executionId));
        result.Status.Should().Be(CommitExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("verification outcome is 'NeedsReview'");
    }

    private sealed class TestExecutionActivityRepository : IExecutionActivityRepository
    {
        public List<ExecutionActivity> Activities { get; } = new();

        public Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ExecutionActivity>>(Activities.Where(a => a.ExecutionId == executionId).ToList());
        }
    }

    private sealed class MockWorkspaceManager : IExecutionWorkspaceManager
    {
        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionWorkspaceResult("/ws", "feature", true, null));

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceVerificationResult(true, true, true, true, null));
    }

    private sealed class MockFingerprintCalculator : IExecutionChangeFingerprintCalculator
    {
        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(string workspacePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionFingerprintResult(true, "fp", "sha", false, 1));

        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(string workspacePath, string treeSha, string baseHeadSha, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionFingerprintResult(true, "fp", baseHeadSha, false, 1));
    }

    private sealed class MockActivityRecorder : IExecutionActivityRecorder
    {
        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class MockGitCommitService : IExecutionGitCommitService
    {
        public Task<ExecutionCommitResult> CommitApprovedExecutionAsync(TaskExecution execution, string taskTitle, Guid attemptId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionCommitResult(Success: true, IsAlreadyCommitted: false, CommitSha: "sha"));
    }
}
