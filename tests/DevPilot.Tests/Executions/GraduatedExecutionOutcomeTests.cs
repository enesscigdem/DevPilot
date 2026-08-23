using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public void DetermineOutcome_DeveloperAgentFailedOnly_ReturnsFailed()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Failed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.DeveloperAgent,
                Status = ExecutionActivityStatus.Failed,
                Message = "Developer Agent failed: generation failed."
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.Failed);
    }

    [Fact]
    public void DetermineOutcome_DeveloperAgentFailedAttemptThenCompleted_SupersedesFailure()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            DeveloperAgentLifecycle(ExecutionActivityStatus.Started, "Developer Agent started."),
            DeveloperAgentLifecycle(ExecutionActivityStatus.Failed, "Developer Agent failed: transient generation error."),
            DeveloperAgentLifecycle(ExecutionActivityStatus.Completed, "Developer Agent completed.")
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().NotBe(ExecutionVerificationOutcome.Failed);
    }

    [Fact]
    public void DetermineOutcome_TokenLimitExceededThenCompactRetryAndCompleted_IsNotFailed()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            DeveloperAgentLifecycle(ExecutionActivityStatus.Started, "Developer Agent started."),
            ProviderCall(ExecutionActivityStatus.Failed, "Generation", "Provider call completed: Generation."),
            ProviderCall(ExecutionActivityStatus.Completed, "CompactGenerationRetry", "Provider call completed: CompactGenerationRetry."),
            DeveloperAgentLifecycle(ExecutionActivityStatus.Completed, "Developer Agent completed.")
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().NotBe(ExecutionVerificationOutcome.Failed);
    }

    [Fact]
    public void DetermineOutcome_MultipleIntermediateDeveloperAgentFailuresThenCompleted_IsNotFailed()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            DeveloperAgentLifecycle(ExecutionActivityStatus.Started, "Developer Agent started."),
            ProviderCall(ExecutionActivityStatus.Failed, "Generation", "Provider call completed: Generation."),
            ProviderCall(ExecutionActivityStatus.Failed, "Generation", "Provider call completed: Generation."),
            ProviderCall(ExecutionActivityStatus.Completed, "CompactGenerationRetry", "Provider call completed: CompactGenerationRetry."),
            new()
            {
                Stage = ExecutionStage.DeveloperAgent,
                Status = ExecutionActivityStatus.Failed,
                Message = "Generation technical summary.",
                MetadataJson = "{\"EventKind\":\"GenerationSummary\",\"LogicalProviderCallCount\":3}"
            },
            DeveloperAgentLifecycle(ExecutionActivityStatus.Completed, "Developer Agent completed.")
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().NotBe(ExecutionVerificationOutcome.Failed);
    }

    [Fact]
    public void DetermineOutcome_FinalDeveloperAgentFailureWithoutLaterCompletion_ReturnsFailed()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Failed };
        var activities = new List<ExecutionActivity>
        {
            DeveloperAgentLifecycle(ExecutionActivityStatus.Started, "Developer Agent started."),
            ProviderCall(ExecutionActivityStatus.Completed, "Generation", "Provider call completed: Generation."),
            DeveloperAgentLifecycle(ExecutionActivityStatus.Failed, "Developer Agent failed: apply failed.")
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.Failed);
    }

    [Fact]
    public void DetermineOutcome_DeveloperAgentCompleted_BuildPassed_NoTests_ReturnsPartiallyVerified()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            DeveloperAgentLifecycle(ExecutionActivityStatus.Completed, "Developer Agent completed."),
            BuildPassed()
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.PartiallyVerified);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeTrue();
    }

    [Fact]
    public void DetermineOutcome_RealAcceptanceShape_TransientDeveloperAgentFailureThenRepair_ReturnsPartiallyVerified()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = CreateRealAcceptanceActivities();

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.PartiallyVerified);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeTrue();
    }

    [Fact]
    public async Task ApproveCommand_RealAcceptancePartiallyVerified_AllowsApproval()
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
        foreach (var activity in CreateRealAcceptanceActivities())
        {
            activity.ExecutionId = executionId;
            activityRepo.Activities.Add(activity);
        }

        var handler = new ApproveExecutionReviewCommandHandler(
            repo,
            activityRepo,
            new MockWorkspaceManager(),
            new MockFingerprintCalculator(),
            new MockActivityRecorder(),
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ApproveExecutionReviewCommand(executionId, "fp"));
        result.Status.Should().Be(ApproveExecutionReviewResultStatus.Success);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ReviewAndApprove_RealAcceptanceShape_UseTheSamePartiallyVerifiedOutcome()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Task" },
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending,
            WorkspacePath = "/ws",
            BranchName = "feature"
        };
        repo.Executions[executionId] = execution;

        var activityRepo = new TestExecutionActivityRepository();
        foreach (var activity in CreateRealAcceptanceActivities())
        {
            activity.ExecutionId = executionId;
            activityRepo.Activities.Add(activity);
        }

        var reviewHandler = CreateReviewHandler(repo, activityRepo);
        var reviewResult = await reviewHandler.HandleAsync(new GetExecutionReviewQuery(executionId));
        reviewResult.Status.Should().Be(ExecutionReviewResultStatus.Success);
        reviewResult.Review.Should().NotBeNull();
        reviewResult.Review!.Build.Status.Should().Be("Passed");
        reviewResult.Review.Test.Status.Should().Be("Unknown");
        reviewResult.Review.VerificationOutcome.Should().Be("PartiallyVerified");

        var approveHandler = new ApproveExecutionReviewCommandHandler(
            repo,
            activityRepo,
            new MockWorkspaceManager(),
            new MockFingerprintCalculator(),
            new MockActivityRecorder(),
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);
        var approveResult = await approveHandler.HandleAsync(new ApproveExecutionReviewCommand(executionId, "fp"));
        approveResult.Status.Should().Be(ApproveExecutionReviewResultStatus.Success);
    }

    [Fact]
    public void DetermineOutcome_DeveloperAgentCompleted_UnresolvedBuildFailure_ReturnsNeedsReview()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var checkId = Guid.NewGuid();
        var activities = new List<ExecutionActivity>
        {
            DeveloperAgentLifecycle(ExecutionActivityStatus.Completed, "Developer Agent completed."),
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"StoppedWithEvidence\",\"ProgressResult\":\"SameFailure\",\"VerificationOutcome\":\"NeedsReview\"}}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.NeedsReview);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeFalse();
    }

    [Fact]
    public async Task ApproveCommand_UnresolvedBuildAfterDeveloperAgentCompleted_StillBlocks()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        repo.Executions[executionId] = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending,
            WorkspacePath = "/ws",
            BranchName = "feature"
        };

        var checkId = Guid.NewGuid();
        var activityRepo = new TestExecutionActivityRepository();
        activityRepo.Activities.Add(DeveloperAgentLifecycle(ExecutionActivityStatus.Completed, "Developer Agent completed.", executionId));
        activityRepo.Activities.Add(new ExecutionActivity
        {
            ExecutionId = executionId,
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Failed,
            MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"StoppedWithEvidence\",\"ProgressResult\":\"SameFailure\",\"VerificationOutcome\":\"NeedsReview\"}}"
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
    public void DetermineOutcome_WorkspaceFatalFailure_RemainsFailed()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Failed };
        var activities = new List<ExecutionActivity>
        {
            new() { Stage = ExecutionStage.Workspace, Status = ExecutionActivityStatus.Failed, Message = "Workspace prepare failed." },
            DeveloperAgentLifecycle(ExecutionActivityStatus.Completed, "Developer Agent completed.")
        };

        ExecutionVerificationEvaluator.DetermineOutcome(execution, activities)
            .Should().Be(ExecutionVerificationOutcome.Failed);
    }

    [Fact]
    public void DetermineOutcome_Cancelled_RemainsBlocked()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Cancelled };
        var activities = new List<ExecutionActivity>
        {
            DeveloperAgentLifecycle(ExecutionActivityStatus.Completed, "Developer Agent completed."),
            BuildPassed()
        };

        ExecutionVerificationEvaluator.DetermineOutcome(execution, activities)
            .Should().Be(ExecutionVerificationOutcome.Blocked);
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

    [Theory]
    [InlineData("C:/repo/src/TodoService.cs:42", "C:/repo/src/TodoService.cs", 42)]
    [InlineData(@"C:\repo\src\TodoService.cs:42", "C:/repo/src/TodoService.cs", 42)]
    [InlineData("/home/user/repo/src/TodoService.cs:42", "/home/user/repo/src/TodoService.cs", 42)]
    [InlineData("src/TodoService.cs:42", "src/TodoService.cs", 42)]
    [InlineData("C:/repo/src/TodoService.cs", "C:/repo/src/TodoService.cs", null)]
    public void ParseDiagnosticLocation_WindowsAndUnixPaths_PreservesFullPathAndLine(string input, string expectedPath, int? expectedLine)
    {
        var (path, line) = ExecutionDiagnosticEvidence.ParseDiagnosticLocation(input);
        path.Should().Be(expectedPath);
        line.Should().Be(expectedLine);
    }

    [Fact]
    public void WindowsActionableCompilerRepair_CorrelatesToTouchedFile()
    {
        var failure = new NormalizedFailureItem(
            FailureKey: "key1",
            TestName: null,
            ErrorSummary: "CS1002 ; expected",
            NormalizedDiagnostic: "c:/repo/src/todoservice.cs:42:5:CS1002:; expected",
            Location: "C:/repo/src/TodoService.cs:42");

        var actionable = ExecutionDiagnosticEvidence.CreateActionableCompilerEvidence(
            new[] { failure },
            null,
            null,
            "build failed");

        actionable.Locations.Should().ContainSingle();
        actionable.Locations[0].FilePath.Should().Be("C:/repo/src/TodoService.cs");
        actionable.Locations[0].Line.Should().Be(42);

        var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "C:/repo/src/TodoService.cs",
            "C:/repo/src/Other.cs"
        };

        var repairFiles = ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(actionable, modified).ToList();
        repairFiles.Should().Equal("C:/repo/src/TodoService.cs");
    }

    [Fact]
    public void PreExistingBuild_NoTests_ReturnsNoNewRegressions()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = "{\"VerificationOutcome\":\"NoNewRegressions\",\"BaselineClassification\":\"PreExisting\",\"PreExistingFailureCount\":1,\"NewRegressionCount\":0}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.NoNewRegressions);
    }

    [Fact]
    public void PreExistingTest_LaterPassingTest_ReturnsNoNewRegressions()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Completed,
                Message = "Check 1: No new regressions (1 pre-existing failure(s) remain).",
                MetadataJson = "{\"VerificationOutcome\":\"NoNewRegressions\",\"BaselineClassification\":\"PreExisting\",\"PreExistingFailureCount\":1,\"NewRegressionCount\":0}"
            },
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Completed,
                Message = "Check 2 passed.",
                MetadataJson = "{\"VerificationOutcome\":\"Verified\"}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.NoNewRegressions);
    }

    [Fact]
    public void InfrastructureErrorEvidence_WithPassingCheck_DoesNotReturnVerified()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = "{\"VerificationOutcome\":\"Verified\"}"
            },
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = "{\"VerificationOutcome\":\"VerificationInfrastructureError\"}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.VerificationInfrastructureError);
    }

    [Fact]
    public void CleanBuildOnlyRepository_ReturnsPartiallyVerified()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = "{\"VerificationOutcome\":\"Verified\"}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.PartiallyVerified);
    }

    [Fact]
    public void CleanBuildAndTestsRepository_ReturnsVerified()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = "{\"VerificationOutcome\":\"Verified\"}"
            },
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = "{\"VerificationOutcome\":\"Verified\"}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.Verified);
    }

    [Fact]
    public void NeedsReview_CannotBeOverwrittenByLaterStatusMetadata()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = "{\"VerificationOutcome\":\"NeedsReview\",\"NewRegressionCount\":1}"
            },
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = "{\"VerificationOutcome\":\"Verified\"}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.NeedsReview);
    }

    [Fact]
    public void BuildFail_RepairRetryFail_LaterBuildPass_ReturnsVerified()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var checkId = Guid.NewGuid();
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"VerifyingRepository\"}}"
            },
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"FixingBuildIssue\",\"RepairRound\":1}}"
            },
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"CheckPassed\"}}"
            },
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = "{\"RepositoryCheckId\":\"tests\",\"EventKind\":\"CheckPassed\"}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.Verified);
    }

    [Fact]
    public void TestFail_RetryFail_LaterTestPass_ReturnsVerified()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var checkId = Guid.NewGuid();
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"VerifyingRepository\"}}"
            },
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"FixingTestIssue\",\"RepairRound\":1}}"
            },
            new()
            {
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Completed,
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"CheckPassed\"}}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.Verified);
    }

    [Fact]
    public void UnresolvedFinalFailure_ReturnsNeedsReview()
    {
        var execution = new TaskExecution { Status = TaskExecutionStatus.Completed };
        var checkId = Guid.NewGuid();
        var activities = new List<ExecutionActivity>
        {
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Failed,
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"StoppedWithEvidence\",\"ProgressResult\":\"SameFailure\",\"VerificationOutcome\":\"NeedsReview\"}}"
            }
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        outcome.Should().Be(ExecutionVerificationOutcome.NeedsReview);
    }

    [Fact]
    public void WorktreeIndependent_CompilerBaselineComparison_ClassifiedPreExisting()
    {
        var taskOutput = "C:/tmp/executions/abc/src/TodoService.cs(42,5): error CS1002: ; expected";
        var baselineOutput = "C:/tmp/baselines/xyz/src/TodoService.cs(42,5): error CS1002: ; expected";

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(
            taskOutput, null, null, workspaceRoot: "C:/tmp/executions/abc");
        var baselineFailures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(
            baselineOutput, null, null, workspaceRoot: "C:/tmp/baselines/xyz");

        taskFailures.Should().ContainSingle();
        baselineFailures.Should().ContainSingle();
        taskFailures[0].Location.Should().Be("src/TodoService.cs:42");
        baselineFailures[0].Location.Should().Be("src/TodoService.cs:42");

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures, baselineFailures, baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.PreExisting);
        comparison.PreExistingCount.Should().Be(1);
        comparison.NewRegressionCount.Should().Be(0);
    }

    [Fact]
    public void SameRelativeFile_ChangedCompilerError_ClassifiedNewRegression()
    {
        var taskOutput = "C:/tmp/executions/abc/src/TodoService.cs(42,5): error CS1002: ; expected";
        var baselineOutput = "C:/tmp/baselines/xyz/src/TodoService.cs(42,5): error CS0103: The name 'foo' does not exist in the current context";

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(
            taskOutput, null, null, workspaceRoot: "C:/tmp/executions/abc");
        var baselineFailures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(
            baselineOutput, null, null, workspaceRoot: "C:/tmp/baselines/xyz");

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures, baselineFailures, baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.NewRegression);
        comparison.NewRegressionCount.Should().Be(1);
    }

    [Fact]
    public void DifferentRepositoryFiles_SameFilename_NotConflated()
    {
        var taskOutput = "C:/tmp/executions/abc/src/a/TodoService.cs(42,5): error CS1002: ; expected";
        var baselineOutput = "C:/tmp/baselines/xyz/src/b/TodoService.cs(42,5): error CS1002: ; expected";

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(
            taskOutput, null, null, workspaceRoot: "C:/tmp/executions/abc");
        var baselineFailures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(
            baselineOutput, null, null, workspaceRoot: "C:/tmp/baselines/xyz");

        taskFailures[0].Location.Should().Be("src/a/TodoService.cs:42");
        baselineFailures[0].Location.Should().Be("src/b/TodoService.cs:42");

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures, baselineFailures, baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.NewRegression);
        comparison.NewRegressionCount.Should().Be(1);
        comparison.PreExistingCount.Should().Be(0);
    }

    [Fact]
    public void UnixAbsoluteTaskAndBaselineWorktreeRoots_MatchesCorrectly()
    {
        var taskOutput = "/tmp/executions/abc/src/TodoService.cs:42:5: error CS1002: ; expected";
        var baselineOutput = "/tmp/baselines/xyz/src/TodoService.cs:42:5: error CS1002: ; expected";

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(
            taskOutput, null, null, workspaceRoot: "/tmp/executions/abc");
        var baselineFailures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(
            baselineOutput, null, null, workspaceRoot: "/tmp/baselines/xyz");

        taskFailures[0].Location.Should().Be("src/TodoService.cs:42");
        baselineFailures[0].Location.Should().Be("src/TodoService.cs:42");

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures, baselineFailures, baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.PreExisting);
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

    [Fact]
    public async Task ReviewStageStatus_BuildFailed_TestsNeverStarted_ReturnsBuildFailedAndTestsUnknown()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Task" },
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
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Failed,
            MetadataJson = "{\"RepositoryCheckId\":\"build\",\"VerificationOutcome\":\"NeedsReview\"}"
        });

        var handler = CreateReviewHandler(repo, activityRepo);
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review.Should().NotBeNull();
        result.Review!.Build.Status.Should().Be("Failed");
        result.Review.Test.Status.Should().Be("Unknown");
        result.Review.VerificationOutcome.Should().Be("NeedsReview");
    }

    [Fact]
    public async Task ReviewStageStatus_VerificationUnavailable_NoChecksExecuted_ReturnsBothUnknown()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Task" },
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
            Stage = ExecutionStage.Execution,
            Status = ExecutionActivityStatus.Completed,
            MetadataJson = "{\"VerificationOutcome\":\"VerificationUnavailable\"}"
        });

        var handler = CreateReviewHandler(repo, activityRepo);
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review.Should().NotBeNull();
        result.Review!.Build.Status.Should().Be("Unknown");
        result.Review.Test.Status.Should().Be("Unknown");
        result.Review.VerificationOutcome.Should().Be("VerificationUnavailable");
    }

    [Fact]
    public async Task ReviewStageStatus_BuildOnlyRepository_BuildPasses_ReturnsBuildPassedAndTestsUnknown()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Task" },
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
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Completed,
            MetadataJson = "{\"RepositoryCheckId\":\"build\",\"VerificationOutcome\":\"Verified\"}"
        });

        var handler = CreateReviewHandler(repo, activityRepo);
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review.Should().NotBeNull();
        result.Review!.Build.Status.Should().Be("Passed");
        result.Review.Test.Status.Should().Be("Unknown");
        result.Review.VerificationOutcome.Should().Be("PartiallyVerified");
    }

    [Fact]
    public async Task ReviewStageStatus_BuildAndTestsPass_ReturnsBothPassed()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Task" },
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
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Completed,
            MetadataJson = "{\"RepositoryCheckId\":\"build\",\"VerificationOutcome\":\"Verified\"}"
        });
        activityRepo.Activities.Add(new ExecutionActivity
        {
            ExecutionId = executionId,
            Stage = ExecutionStage.Test,
            Status = ExecutionActivityStatus.Completed,
            MetadataJson = "{\"RepositoryCheckId\":\"tests\",\"VerificationOutcome\":\"Verified\"}"
        });

        var handler = CreateReviewHandler(repo, activityRepo);
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review.Should().NotBeNull();
        result.Review!.Build.Status.Should().Be("Passed");
        result.Review.Test.Status.Should().Be("Passed");
        result.Review.VerificationOutcome.Should().Be("Verified");
    }

    [Fact]
    public async Task ReviewStageStatus_TestFail_Repair_TestPasses_ReturnsTestsPassed()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Task" },
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
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Completed,
            MetadataJson = "{\"RepositoryCheckId\":\"build\"}"
        });
        activityRepo.Activities.Add(new ExecutionActivity
        {
            ExecutionId = executionId,
            Stage = ExecutionStage.Test,
            Status = ExecutionActivityStatus.Failed,
            MetadataJson = "{\"RepositoryCheckId\":\"tests\",\"EventKind\":\"VerifyingRepository\"}"
        });
        activityRepo.Activities.Add(new ExecutionActivity
        {
            ExecutionId = executionId,
            Stage = ExecutionStage.Test,
            Status = ExecutionActivityStatus.Completed,
            MetadataJson = "{\"RepositoryCheckId\":\"tests\",\"EventKind\":\"CheckPassed\"}"
        });

        var handler = CreateReviewHandler(repo, activityRepo);
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review.Should().NotBeNull();
        result.Review!.Build.Status.Should().Be("Passed");
        result.Review.Test.Status.Should().Be("Passed");
        result.Review.VerificationOutcome.Should().Be("Verified");
    }

    [Fact]
    public async Task ReviewStageStatus_PreExistingTestFailures_ReturnsTestsNoNewRegressions()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Task" },
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
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Completed,
            MetadataJson = "{\"RepositoryCheckId\":\"build\"}"
        });
        activityRepo.Activities.Add(new ExecutionActivity
        {
            ExecutionId = executionId,
            Stage = ExecutionStage.Test,
            Status = ExecutionActivityStatus.Completed,
            Message = "No new regressions (2 pre-existing repository failure(s) remain).",
            MetadataJson = "{\"RepositoryCheckId\":\"tests\",\"VerificationOutcome\":\"NoNewRegressions\",\"BaselineClassification\":\"PreExisting\",\"PreExistingFailureCount\":2,\"NewRegressionCount\":0}"
        });

        var handler = CreateReviewHandler(repo, activityRepo);
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review.Should().NotBeNull();
        result.Review!.Build.Status.Should().Be("Passed");
        result.Review.Test.Status.Should().Be("NoNewRegressions");
        result.Review.Test.PreExistingFailureCount.Should().Be(2);
        result.Review.VerificationOutcome.Should().Be("NoNewRegressions");
    }

    private static List<ExecutionActivity> CreateRealAcceptanceActivities()
    {
        var checkId = "npm-build";
        return new List<ExecutionActivity>
        {
            DeveloperAgentLifecycle(ExecutionActivityStatus.Started, "Developer Agent started."),
            ProviderCall(ExecutionActivityStatus.Failed, "Generation", "Provider call completed: Generation."),
            ProviderCall(ExecutionActivityStatus.Completed, "CompactGenerationRetry", "Provider call completed: CompactGenerationRetry."),
            DeveloperAgentLifecycle(ExecutionActivityStatus.Completed, "Developer Agent completed."),
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Failed,
                Message = "npm build failed with 1 compiler error.",
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"VerifyingRepository\"}}"
            },
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Started,
                Message = "Compile repair started.",
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"FixingBuildIssue\",\"RepairKind\":\"Compile\",\"RepairRound\":1}}"
            },
            new()
            {
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Completed,
                Message = "npm build passed.",
                MetadataJson = $"{{\"RepositoryCheckId\":\"{checkId}\",\"EventKind\":\"CheckPassed\",\"VerificationOutcome\":\"Verified\"}}"
            }
        };
    }

    private static ExecutionActivity DeveloperAgentLifecycle(
        ExecutionActivityStatus status,
        string message,
        Guid? executionId = null) =>
        new()
        {
            ExecutionId = executionId ?? Guid.Empty,
            Stage = ExecutionStage.DeveloperAgent,
            Status = status,
            Message = message,
            MetadataJson = "{\"EventKind\":\"GeneratingChange\"}"
        };

    private static ExecutionActivity ProviderCall(
        ExecutionActivityStatus status,
        string callKind,
        string message) =>
        new()
        {
            Stage = ExecutionStage.DeveloperAgent,
            Status = status,
            Message = message,
            MetadataJson = $"{{\"EventKind\":\"ProviderCall\",\"ProviderCallKind\":\"{callKind}\",\"LogicalProviderCallCount\":1}}"
        };

    private static ExecutionActivity BuildPassed() =>
        new()
        {
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Completed,
            MetadataJson = "{\"RepositoryCheckId\":\"build\",\"VerificationOutcome\":\"Verified\"}"
        };

    private static GetExecutionReviewQueryHandler CreateReviewHandler(
        IExecutionRepository repo,
        IExecutionActivityRepository activityRepo)
    {
        return new GetExecutionReviewQueryHandler(
            repo,
            new MockWorkspaceManager(),
            new MockDiffReader(),
            new MockFingerprintCalculator(),
            activityRepo,
            Options.Create(new MergePolicyOptions()),
            NullLogger<GetExecutionReviewQueryHandler>.Instance);
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

    private sealed class MockDiffReader : IExecutionGitDiffReader
    {
        public Task<ExecutionGitDiffResult> ReadWorkspaceDiffAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionGitDiffResult(true, null, new[] { new ExecutionReviewFileDto("src/TodoService.cs", "Modified") }, "diff"));

        public Task<ExecutionGitDiffResult> ReadCommittedDiffAsync(string workspacePath, string baseCommitSha, string commitSha, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionGitDiffResult(true, null, new[] { new ExecutionReviewFileDto("src/TodoService.cs", "Modified") }, "diff"));
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
