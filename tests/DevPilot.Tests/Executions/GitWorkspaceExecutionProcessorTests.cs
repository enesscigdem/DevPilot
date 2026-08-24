using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class GitWorkspaceExecutionProcessorTests
{
    [Fact]
    public async Task ProcessAsync_FullSuccess_ExecutesStagesInOrder_BuildAndTestReceiveNullTargetPath_AndRecordsActivities()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager();
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis
            {
                Id = Guid.NewGuid(),
                DevelopmentTaskId = taskId,
                Status = ImpactAnalysisStatus.Completed,
                Summary = "Impact summary"
            }
        };
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "App.cs" })
        };
        var validationRunner = new TestExecutionValidationRunner();
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            new TestRepositoryCheckRunnerAdapter(validationRunner),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(
            ExecutionId: executionId,
            TaskId: taskId,
            TaskTitle: "Feature X",
            TaskDescription: "Add feature X",
            AcceptanceCriteria: "Feature X works",
            WorkspaceId: workspaceId,
            WorkspaceLocalPath: "/path/to/source",
            ImpactAnalysisSummary: "Impact summary");

        await processor.ProcessAsync(context);

        workspaceManager.PrepareCalled.Should().BeTrue();
        executionRepo.UpdatedWorkspacePath.Should().NotBeNullOrWhiteSpace();
        executionRepo.UpdatedBranchName.Should().NotBeNullOrWhiteSpace();
        workspaceManager.VerifyRequireCleanCalls.Should().Contain(true);
        agent.CallCount.Should().Be(1);
        validationRunner.BuildCallCount.Should().Be(1);
        validationRunner.TestCallCount.Should().Be(1);

        // Verify chronological stage events recorded, including deterministic repository preflight.
        recorder.RecordedActivities.Should().HaveCount(9);
        recorder.RecordedActivities[0].stage.Should().Be(ExecutionStage.Workspace);
        recorder.RecordedActivities[0].status.Should().Be(ExecutionActivityStatus.Started);
        recorder.RecordedActivities[1].stage.Should().Be(ExecutionStage.Workspace);
        recorder.RecordedActivities[1].status.Should().Be(ExecutionActivityStatus.Completed);
        recorder.RecordedActivities[1].metadata?.BranchName.Should().Be("devpilot/branch");

        recorder.RecordedActivities[2].stage.Should().Be(ExecutionStage.Workspace);
        recorder.RecordedActivities[2].metadata?.EventKind.Should().Be("RepositoryPreflight");
        recorder.RecordedActivities[2].metadata?.DiscoveredCheckCount.Should().Be(2);

        recorder.RecordedActivities[3].stage.Should().Be(ExecutionStage.DeveloperAgent);
        recorder.RecordedActivities[3].status.Should().Be(ExecutionActivityStatus.Started);
        recorder.RecordedActivities[4].stage.Should().Be(ExecutionStage.DeveloperAgent);
        recorder.RecordedActivities[4].status.Should().Be(ExecutionActivityStatus.Completed);
        recorder.RecordedActivities[4].metadata?.ModifiedFileCount.Should().Be(1);

        recorder.RecordedActivities[5].stage.Should().Be(ExecutionStage.Build);
        recorder.RecordedActivities[5].status.Should().Be(ExecutionActivityStatus.Started);
        recorder.RecordedActivities[6].stage.Should().Be(ExecutionStage.Build);
        recorder.RecordedActivities[6].status.Should().Be(ExecutionActivityStatus.Completed);
        recorder.RecordedActivities[6].metadata?.BuildPassed.Should().BeTrue();
        recorder.RecordedActivities[6].metadata?.RepositoryCheckId.Should().Be("dotnet:build:test");

        recorder.RecordedActivities[7].stage.Should().Be(ExecutionStage.Test);
        recorder.RecordedActivities[7].status.Should().Be(ExecutionActivityStatus.Started);
        recorder.RecordedActivities[8].stage.Should().Be(ExecutionStage.Test);
        recorder.RecordedActivities[8].status.Should().Be(ExecutionActivityStatus.Completed);
        recorder.RecordedActivities[8].metadata?.TestPassed.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessAsync_UnconfiguredRepositoryVerification_StopsBeforeProviderGeneration()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent();
        var recorder = new TestActivityRecorder();
        var processor = new GitWorkspaceExecutionProcessor(
            new TestWorkspaceManager(),
            new TestExecutionRepository(),
            new TestImpactAnalysisRepository
            {
                AnalysisToReturn = new TaskImpactAnalysis
                {
                    Id = Guid.NewGuid(),
                    DevelopmentTaskId = taskId,
                    Status = ImpactAnalysisStatus.Completed
                }
            },
            agent,
            new UnconfiguredRepositoryCheckRunner(),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var act = () => processor.ProcessAsync(CreateContext(taskId));

        await act.Should().NotThrowAsync();
        agent.CallCount.Should().Be(1);
        recorder.RecordedActivities.Should().Contain(activity =>
            activity.metadata != null &&
            activity.metadata.VerificationOutcome == "VerificationUnavailable" &&
            activity.metadata.EventKind == "ReadyForReview");
    }

    [Fact]
    public async Task ProcessAsync_DeveloperAgentFails_RecordsDevAgentFailed_DoesNotRecordBuildOrTest()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager();
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed }
        };
        var agent = new TestDeveloperAgent { ResultToReturn = DeveloperAgentResult.Fail("AI schema syntax error") };
        var validationRunner = new TestExecutionValidationRunner();
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            new TestRepositoryCheckRunnerAdapter(validationRunner),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(
            ExecutionId: executionId,
            TaskId: taskId,
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            WorkspaceId: Guid.NewGuid(),
            WorkspaceLocalPath: "/source",
            ImpactAnalysisSummary: "Summary");

        var act = async () => await processor.ProcessAsync(context);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.WithMessage("Developer Agent failed: AI schema syntax error");

        agent.CallCount.Should().Be(1);
        validationRunner.BuildCallCount.Should().Be(0);

        recorder.RecordedActivities.Should().Contain(a => a.stage == ExecutionStage.DeveloperAgent && a.status == ExecutionActivityStatus.Failed);
        recorder.RecordedActivities.Should().NotContain(a => a.stage == ExecutionStage.Build);
        recorder.RecordedActivities.Should().NotContain(a => a.stage == ExecutionStage.Test);
    }

    [Fact]
    public async Task ProcessAsync_DeveloperAgentFailsWithKimiClassification_PersistsClassifiedDiagnosticAndModelTelemetry()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager();
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed, Model = "kimi-k2.7-code" }
        };
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Fail("Kimi HTTP 503 after 4 attempts while generating 'WorkspaceTaskActivityItemDto.cs'.")
        };
        var validationRunner = new TestExecutionValidationRunner();
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            new TestRepositoryCheckRunnerAdapter(validationRunner),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(
            ExecutionId: executionId,
            TaskId: taskId,
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            WorkspaceId: Guid.NewGuid(),
            WorkspaceLocalPath: "/source",
            ImpactAnalysisSummary: "Summary");

        var act = async () => await processor.ProcessAsync(context);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.WithMessage("Developer Agent failed: Kimi HTTP 503 after 4 attempts while generating 'WorkspaceTaskActivityItemDto.cs'.");

        executionRepo.SetModel.Should().Be("kimi-k2.7-code");

        var failedActivity = recorder.RecordedActivities.Should()
            .ContainSingle(a => a.stage == ExecutionStage.DeveloperAgent && a.status == ExecutionActivityStatus.Failed)
            .Subject;

        failedActivity.message.Should().Be("Developer Agent failed: Kimi HTTP 503 after 4 attempts while generating 'WorkspaceTaskActivityItemDto.cs'.");
        failedActivity.metadata.Should().NotBeNull();
        failedActivity.metadata!.Model.Should().Be("kimi-k2.7-code");
    }

    [Fact]
    public async Task ProcessAsync_BuildFails_RecordsBuildFailed_DoesNotRecordTestStage()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager();
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed }
        };
        var agent = new TestDeveloperAgent { ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "App.cs" }) };
        var validationRunner = new TestExecutionValidationRunner
        {
            BuildResultToReturn = BuildValidationResult.FailResult("dotnet build failed with exit code 1.")
        };
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            new TestRepositoryCheckRunnerAdapter(validationRunner),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(
            ExecutionId: executionId,
            TaskId: taskId,
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            WorkspaceId: Guid.NewGuid(),
            WorkspaceLocalPath: "/source",
            ImpactAnalysisSummary: "Summary");

        var act = async () => await processor.ProcessAsync(context);

        await act.Should().NotThrowAsync();

        validationRunner.BuildCallCount.Should().Be(1);
        validationRunner.TestCallCount.Should().Be(0);

        recorder.RecordedActivities.Should().Contain(a => a.stage == ExecutionStage.Build && a.status == ExecutionActivityStatus.Failed && a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
        recorder.RecordedActivities.Should().NotContain(a => a.stage == ExecutionStage.Test);
    }

    [Fact]
    public async Task ProcessAsync_TestFailsWithoutCorrelatableEvidence_StopsWithoutRepair()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager();
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed }
        };
        var agent = new TestDeveloperAgent { ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "App.cs" }) };
        var validationRunner = new TestExecutionValidationRunner
        {
            TestResultToReturn = TestValidationResult.FailResult("dotnet test failed with exit code 1.")
        };
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            new TestRepositoryCheckRunnerAdapter(validationRunner),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(
            ExecutionId: executionId,
            TaskId: taskId,
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            WorkspaceId: Guid.NewGuid(),
            WorkspaceLocalPath: "/source",
            ImpactAnalysisSummary: "Summary");

        var act = async () => await processor.ProcessAsync(context);

        await act.Should().NotThrowAsync();

        validationRunner.BuildCallCount.Should().Be(1);
        validationRunner.TestCallCount.Should().Be(1);
        agent.CallCount.Should().Be(1, "uncorrelated test output must not trigger broad repair");

        recorder.RecordedActivities.Should().Contain(a => a.stage == ExecutionStage.Test && a.status == ExecutionActivityStatus.Failed && a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
    }

    [Fact]
    public async Task ProcessAsync_TestFails_TestRepairSucceeds_CompletesExecutionSuccessfully()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager();
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed }
        };
        var agent = new TestDeveloperAgent { ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/TodoService.cs" }) };

        var testResults = new Queue<TestValidationResult>(new[]
        {
            FailedTodoTest(),
            new TestValidationResult { Success = true, ExitCode = 0 },
            new TestValidationResult { Success = true, ExitCode = 0 }
        });

        var validationRunner = new QueuedTestValidationRunner(testResults);
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            new TestRepositoryCheckRunnerAdapter(validationRunner),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(
            ExecutionId: executionId,
            TaskId: taskId,
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            WorkspaceId: Guid.NewGuid(),
            WorkspaceLocalPath: "/source",
            ImpactAnalysisSummary: "Summary");

        await processor.ProcessAsync(context);

        // Initial build (1) + 1 test repair build (1) = 2
        validationRunner.BuildCallCount.Should().Be(2);
        // Initial full test + targeted retry + final full suite
        validationRunner.TestCallCount.Should().Be(3);
        // Initial agent (1) + 1 test repair (1) = 2
        agent.CallCount.Should().Be(2);

        var messages = recorder.RecordedActivities.Select(a => a.message).ToList();
        messages.Should().Contain("Test repair started (round 1/2).");
        messages.Should().Contain("Test repair completed (round 1).");
        messages.Should().Contain("Test retry passed.");
        messages.Should().Contain("Tests passed.");
    }

    // ── Helper Test Fakes ──────────────────────────────────────────────────────────
    private class TestWorkspaceManager : IExecutionWorkspaceManager
    {
        public bool PrepareSuccess { get; set; } = true;
        public string PrepareErrorMessage { get; set; } = "";
        public string PreparedWorkspacePath { get; set; } = "/workspace/path";
        public bool PrepareCalled { get; private set; }
        public bool VerifyCleanSuccess { get; set; } = true;
        public string VerifyErrorMessage { get; set; } = "";
        public List<bool> VerifyRequireCleanCalls { get; } = new();

        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(
            Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default)
        {
            PrepareCalled = true;
            if (!PrepareSuccess) return Task.FromResult(new ExecutionWorkspaceResult("", "", Success: false, ErrorMessage: PrepareErrorMessage));
            return Task.FromResult(new ExecutionWorkspaceResult(PreparedWorkspacePath, "devpilot/branch", Success: true));
        }

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(
            string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default)
        {
            VerifyRequireCleanCalls.Add(requireClean);
            if (requireClean && !VerifyCleanSuccess)
            {
                return Task.FromResult(new WorkspaceVerificationResult(IsValid: false, WorkspaceExists: true, BranchMatches: true, IsClean: false, ErrorMessage: VerifyErrorMessage));
            }
            return Task.FromResult(new WorkspaceVerificationResult(IsValid: true, WorkspaceExists: true, BranchMatches: true, IsClean: true));
        }
    }

    private class TestExecutionRepository : IExecutionRepository
    {
        public string? UpdatedWorkspacePath { get; private set; }
        public string? UpdatedBranchName { get; private set; }
        public string? SetModel { get; private set; }

        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default)
        {
            UpdatedWorkspacePath = workspacePath;
            UpdatedBranchName = branchName;
            return Task.CompletedTask;
        }

        public Task SetModelAsync(Guid executionId, string model, CancellationToken cancellationToken = default)
        {
            SetModel = model;
            return Task.CompletedTask;
        }

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskExecution?>(null);
        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskExecution>>(Array.Empty<TaskExecution>());
        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasFailedExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetReviewDecisionAsync(Guid executionId, DevPilot.Domain.Enums.ExecutionReviewStatus expectedStatus, DevPilot.Domain.Enums.ExecutionReviewStatus newStatus, DateTime decidedAt, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> TrySetReviewDecisionWithFingerprintAsync(Guid executionId, DevPilot.Domain.Enums.ExecutionReviewStatus expectedStatus, DevPilot.Domain.Enums.ExecutionReviewStatus newStatus, DateTime decidedAt, string fingerprint, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(true);
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
        public Task ReleasePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime releasedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ReplacePullRequestTrackingSnapshotAsync(Guid executionId, Guid attemptId, DevPilot.Domain.Enums.ExecutionPullRequestRemoteState remoteState, DevPilot.Domain.Enums.ExecutionPullRequestIntegrityStatus integrityStatus, DateTime? closedAt, DateTime? mergedAt, DevPilot.Domain.Enums.ExecutionCiStatus ciStatus, IReadOnlyList<DevPilot.Domain.Entities.ExecutionCiCheck> checks, DateTime syncedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryClaimMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan syncTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStaleMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan mergeLeaseTimeout, TimeSpan syncTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetExecutionMergedAsync(Guid executionId, Guid attemptId, string mergeCommitSha, DateTime mergedAt, string mergeMethod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMergeFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ClaimAsRunningAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RenewHeartbeatAsync(Guid executionId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CompleteWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> FailWithLeaseAsync(Guid executionId, Guid leaseToken, string errorMessage, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RequestCancellationAsync(Guid executionId, string? reason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AcknowledgeCancellationWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCancellationRequestedAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleRunningExecutionsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    [Fact]
    public async Task ProcessAsync_BuildFailsWithCompilerErrorsInStdOut_InvokesCompileRepairAndSucceedsOnRebuild()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager();
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis
            {
                Id = Guid.NewGuid(),
                DevelopmentTaskId = taskId,
                Status = ImpactAnalysisStatus.Completed,
                Summary = "Impact summary"
            }
        };
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs" })
        };

        var initialBuildFailed = new BuildValidationResult
        {
            Success = false,
            ExitCode = 1,
            ErrorMessage = "dotnet build failed with exit code 1.",
            StdOut = "src/App.cs(12,15): error CS0246: The type or namespace name 'IMediator' could not be found\n"
        };
        var rebuildSucceeded = new BuildValidationResult { Success = true };

        var buildResults = new Queue<BuildValidationResult>(new[] { initialBuildFailed, rebuildSucceeded });
        var validationRunner = new QueuedValidationRunner(buildResults);
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            new TestRepositoryCheckRunnerAdapter(validationRunner),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(
            ExecutionId: executionId,
            TaskId: taskId,
            TaskTitle: "Feature with build error",
            TaskDescription: "Add feature",
            AcceptanceCriteria: "Must work",
            WorkspaceId: workspaceId,
            WorkspaceLocalPath: "/path/to/source",
            ImpactAnalysisSummary: "Impact summary");

        await processor.ProcessAsync(context);

        // Developer Agent called twice: initial generation + compile repair
        agent.CallCount.Should().Be(2);
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/App.cs");
        validationRunner.BuildCallCount.Should().Be(2);
        validationRunner.TestCallCount.Should().Be(1);

        // Verify explicit compile repair activities recorded
        var messages = recorder.RecordedActivities.Select(a => a.message).ToList();
        messages.Should().Contain("Compile repair started.");
        messages.Should().Contain("Compile repair completed.");
        messages.Should().Contain("Build retry started.");
        messages.Should().Contain("Build retry passed.");
    }

    [Fact]
    public async Task CompileRepair_WhenSameDiagnosticRepeats_StopsAfterFirstFocusedRepair()
    {
        var workspaceId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis
            {
                Id = Guid.NewGuid(),
                DevelopmentTaskId = taskId,
                Status = ImpactAnalysisStatus.Completed,
                Summary = "Impact summary"
            }
        };
        var workspaceManager = new TestWorkspaceManager();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs" })
        };

        // First build fails with compiler error, subsequent repair rebuilds also fail
        var buildFailed = new BuildValidationResult
        {
            Success = false,
            ExitCode = 1,
            ErrorMessage = "dotnet build failed with exit code 1.",
            StdOut = "src/App.cs(12,15): error CS0246: The type or namespace name 'IMediator' could not be found\n"
        };

        var buildResults = new Queue<BuildValidationResult>(new[] { buildFailed, buildFailed, buildFailed, buildFailed });
        var validationRunner = new QueuedValidationRunner(buildResults);
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            new TestRepositoryCheckRunnerAdapter(validationRunner),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(
            ExecutionId: executionId,
            TaskId: taskId,
            TaskTitle: "Feature with unresolvable build error",
            TaskDescription: "Add feature",
            AcceptanceCriteria: "Must work",
            WorkspaceId: workspaceId,
            WorkspaceLocalPath: "/path/to/source",
            ImpactAnalysisSummary: "Impact summary");

        var act = () => processor.ProcessAsync(context);
        await act.Should().NotThrowAsync();

        agent.CallCount.Should().Be(3, "generation + changed repair + one final diagnostic-directed attempt");
        validationRunner.BuildCallCount.Should().Be(3);
        agent.FocusedRepairRequests.Should().HaveCount(2);
        agent.FocusedRepairRequests[1].IsFinalDiagnosticAttempt.Should().BeTrue();

        // Verify explicit compile repair activities recorded
        var messages = recorder.RecordedActivities.Select(a => a.message).ToList();
        messages.Should().Contain("Compile repair started.");
        messages.Should().Contain("Compile repair completed.");
        messages.Should().Contain("Build retry started.");
        messages.Should().Contain("Build retry failed.");
        messages.Should().Contain(m => m.Contains("Build validation failed:"));
        recorder.RecordedActivities.Should().Contain(a => a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
    }

    [Fact]
    public async Task CompileRepair_UncorrelatedDiagnostic_DoesNotFallbackToAllModifiedFiles()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs", "src/Valid.cs" })
        };
        var runner = new ScriptedValidationRunner(
            new[]
            {
                new BuildValidationResult
                {
                    Success = false,
                    ErrorMessage = "dotnet build failed.",
                    StdOut = "src/Unrelated.cs(8,3): error CS1002: ; expected"
                }
            });

        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, recorder: recorder);
        var act = () => processor.ProcessAsync(CreateContext(taskId));

        await act.Should().NotThrowAsync();
        agent.CallCount.Should().Be(1, "uncorrelated diagnostics must not regenerate all touched files");
        runner.BuildRequests.Should().HaveCount(1);
        recorder.RecordedActivities.Should().Contain(a => a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
    }

    [Fact]
    public async Task CompileRepair_SameFingerprintAfterRepair_StopsWithoutAnotherBuildOrRepair()
    {
        var taskId = Guid.NewGuid();
        var failure = new BuildValidationResult
        {
            Success = false,
            ErrorMessage = "dotnet build failed.",
            StdOut = "src/App.cs(8,3): error CS1002: ; expected"
        };
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs", "src/Valid.cs" })
        };
        var runner = new ScriptedValidationRunner(new[] { failure });
        var fingerprint = new TestFingerprintCalculator("same", "same");
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        var act = () => processor.ProcessAsync(CreateContext(taskId));

        await act.Should().NotThrowAsync();
        agent.CallCount.Should().Be(2);
        runner.BuildRequests.Should().HaveCount(1, "a no-diff repair should stop before another build");
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/App.cs");
        recorder.RecordedActivities.Should().Contain(a => a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
    }

    [Fact]
    public async Task CompileRepair_SameDiagnosticAfterChangedRepair_AllowsOneFinalDiagnosticAttemptThenStops()
    {
        var taskId = Guid.NewGuid();
        var failure = new BuildValidationResult
        {
            Success = false,
            ErrorMessage = "dotnet build failed.",
            StdOut = "src/App.cs(8,3): error CS1002: ; expected"
        };
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs", "src/Valid.cs" })
        };
        var runner = new ScriptedValidationRunner(new[] { failure, failure, failure });
        var fingerprint = new TestFingerprintCalculator("before", "after", "after-before", "after-after");
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        var act = () => processor.ProcessAsync(CreateContext(taskId));

        await act.Should().NotThrowAsync();
        agent.CallCount.Should().Be(3, "one changed repair plus one final diagnostic-directed attempt");
        runner.BuildRequests.Should().HaveCount(3);
        agent.FocusedRepairRequests.Should().HaveCount(2);
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/App.cs");
        agent.FocusedRepairRequests[0].IsFinalDiagnosticAttempt.Should().BeFalse();
        agent.FocusedRepairRequests[1].RepairFiles.Should().Equal("src/App.cs");
        agent.FocusedRepairRequests[1].IsFinalDiagnosticAttempt.Should().BeTrue();
        agent.FocusedRepairRequests[1].AcceptanceCriteria.Should().Contain("exact current compiler diagnostic");
        recorder.RecordedActivities.Should().Contain(a => a.metadata != null && a.metadata.ProgressResult == "SameFailure");
        recorder.RecordedActivities.Should().Contain(a => a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
    }

    [Fact]
    public async Task CompileRepair_ChangedDiagnostic_AllowsOneFurtherFocusedRepair()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs", "src/Other.cs", "src/Valid.cs" })
        };
        var runner = new ScriptedValidationRunner(new[]
        {
            new BuildValidationResult { Success = false, ErrorMessage = "build failed", StdOut = "src/App.cs(8,3): error CS1002: ; expected" },
            new BuildValidationResult { Success = false, ErrorMessage = "build failed", StdOut = "src/Other.cs(9,4): error CS0103: Name is not defined" },
            new BuildValidationResult { Success = true }
        });
        var fingerprint = new TestFingerprintCalculator("a", "b", "c", "d");
        var processor = CreateProcessor(taskId, agent, runner, fingerprint);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.CallCount.Should().Be(3);
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/App.cs");
        agent.FocusedRepairRequests[1].RepairFiles.Should().Equal("src/Other.cs");
        agent.FocusedRepairRequests[0].RepairFiles.Should().NotContain("src/Valid.cs");
        agent.FocusedRepairRequests[1].RepairFiles.Should().NotContain("src/Valid.cs");
    }

    [Fact]
    public async Task CompileRepair_TwoFileDiagnostics_RepairsHighestConfidenceFileThenRebuildsBeforeNextFile()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs", "src/Other.cs" })
        };
        var runner = new ScriptedValidationRunner(new[]
        {
            new BuildValidationResult
            {
                Success = false,
                ErrorMessage = "build failed",
                StdOut = "src/App.cs(8,3): error CS1002: ; expected\nsrc/Other.cs(9,4): error CS0103: Name is not defined"
            },
            new BuildValidationResult
            {
                Success = false,
                ErrorMessage = "build failed",
                StdOut = "src/Other.cs(9,4): error CS0103: Name is not defined"
            },
            new BuildValidationResult { Success = true }
        });
        var fingerprint = new TestFingerprintCalculator("a", "b", "c", "d");
        var processor = CreateProcessor(taskId, agent, runner, fingerprint);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.FocusedRepairRequests.Should().HaveCount(2);
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/App.cs");
        agent.FocusedRepairRequests[1].RepairFiles.Should().Equal("src/Other.cs");
        runner.BuildRequests.Should().HaveCount(3, "build reruns after file A before file B is repaired");
    }

    [Fact]
    public async Task CompileRepair_SecondFileProviderFailure_PreservesFirstAppliedRepair()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs", "src/Other.cs" })
        };
        agent.FocusedRepairResults.Enqueue(DeveloperAgentResult.Ok(new List<string> { "src/App.cs" }));
        agent.FocusedRepairResults.Enqueue(DeveloperAgentResult.Fail("TokenLimitExceeded"));
        var runner = new ScriptedValidationRunner(new[]
        {
            new BuildValidationResult
            {
                Success = false,
                ErrorMessage = "build failed",
                StdOut = "src/App.cs(8,3): error CS1002: ; expected\nsrc/Other.cs(9,4): error CS0103: Name is not defined"
            },
            new BuildValidationResult
            {
                Success = false,
                ErrorMessage = "build failed",
                StdOut = "src/Other.cs(9,4): error CS0103: Name is not defined"
            }
        });
        var fingerprint = new TestFingerprintCalculator("a", "b");
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.FocusedRepairRequests.Should().HaveCount(2);
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/App.cs");
        agent.FocusedRepairRequests[1].RepairFiles.Should().Equal("src/Other.cs");
        recorder.RecordedActivities.Should().Contain(a =>
            a.message.Contains("Compile repair completed") &&
            a.metadata != null &&
            a.metadata.RepairFiles != null &&
            a.metadata.RepairFiles.Contains("src/App.cs"));
        recorder.RecordedActivities.Should().Contain(a =>
            a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
    }

    [Fact]
    public async Task CompileRepair_SeventeenDiagnostics_RepairsFirstImplicatedFileOnly()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs", "src/Other.cs", "src/Valid.cs" })
        };
        var runner = new ScriptedValidationRunner(new[]
        {
            new BuildValidationResult { Success = false, ErrorMessage = "build failed", StdOut = SeventeenCompilerDiagnostics() },
            new BuildValidationResult { Success = true }
        });
        var fingerprint = new TestFingerprintCalculator("before", "after");
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.FocusedRepairRequests.Should().ContainSingle();
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/App.cs");
        agent.FocusedRepairRequests[0].IsFinalDiagnosticAttempt.Should().BeFalse();
        recorder.RecordedActivities.Should().Contain(a =>
            a.metadata != null &&
            a.metadata.DiagnosticErrorCount == 17 &&
            a.metadata.DistinctDiagnosticFileCount == 2 &&
            a.metadata.SelectedRepairTarget == "src/App.cs");
    }

    [Fact]
    public async Task CompileRepair_UnchangedDiagnosticsAfterFileA_SelectsExactFileBNotFinalA()
    {
        var taskId = Guid.NewGuid();
        var failure = new BuildValidationResult
        {
            Success = false,
            ErrorMessage = "build failed",
            StdOut = SeventeenCompilerDiagnostics()
        };
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/App.cs", "src/Other.cs" })
        };
        var runner = new ScriptedValidationRunner(new[] { failure, failure, new BuildValidationResult { Success = true } });
        var fingerprint = new TestFingerprintCalculator("a", "b", "c", "d");
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.FocusedRepairRequests.Should().HaveCount(2);
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/App.cs");
        agent.FocusedRepairRequests[0].IsFinalDiagnosticAttempt.Should().BeFalse();
        agent.FocusedRepairRequests[1].RepairFiles.Should().Equal("src/Other.cs");
        agent.FocusedRepairRequests[1].IsFinalDiagnosticAttempt.Should().BeFalse();
        recorder.RecordedActivities.Should().Contain(a =>
            a.metadata != null &&
            a.metadata.SelectedRepairTarget == "src/Other.cs" &&
            a.metadata.AttemptedRepairTargets != null &&
            a.metadata.AttemptedRepairTargets.Contains("src/App.cs"));
    }

    [Fact]
    public async Task CompileRepair_MoreThanFiveDistinctExactFiles_ContinuesUntilEvidenceIsExhausted()
    {
        var taskId = Guid.NewGuid();
        var files = Enumerable.Range(1, 6).Select(i => $"src/File{i}.cs").ToArray();
        var stdout = string.Join('\n', files.Select((file, index) => $"{file}({index + 1},1): error CS1002: ; expected"));
        var failure = new BuildValidationResult { Success = false, ErrorMessage = "build failed", StdOut = stdout };
        var agent = new TestDeveloperAgent { ResultToReturn = DeveloperAgentResult.Ok(files) };
        var runner = new ScriptedValidationRunner(Enumerable.Repeat(failure, 8));
        var fingerprint = new TestFingerprintCalculator(Enumerable.Range(0, 20).Select(i => $"fp-{i}").ToArray());
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.FocusedRepairRequests.Should().HaveCount(6);
        agent.FocusedRepairRequests.Should().OnlyContain(request => !request.IsFinalDiagnosticAttempt);
        agent.FocusedRepairRequests.Select(request => request.RepairFiles.Single()).Should().Equal(files);
        agent.FocusedRepairRequests.Select(request => request.RepairFiles.Single()).Distinct().Should().HaveCount(6);
        recorder.RecordedActivities.Should().Contain(a => a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
    }

    [Fact]
    public async Task CompileRepair_SixthExactFile_CanStillApplyAndRerunBuild()
    {
        var taskId = Guid.NewGuid();
        var files = Enumerable.Range(1, 6).Select(i => $"src/File{i}.cs").ToArray();
        var stdout = string.Join('\n', files.Select((file, index) => $"{file}({index + 1},1): error CS1002: ; expected"));
        var failure = new BuildValidationResult { Success = false, ErrorMessage = "build failed", StdOut = stdout };
        var agent = new TestDeveloperAgent { ResultToReturn = DeveloperAgentResult.Ok(files) };
        var runner = new ScriptedValidationRunner(new[]
        {
            failure, failure, failure, failure, failure, failure,
            new BuildValidationResult { Success = true }
        });
        var fingerprint = new TestFingerprintCalculator(Enumerable.Range(0, 20).Select(i => $"fp-{i}").ToArray());
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.FocusedRepairRequests.Should().HaveCount(6);
        agent.FocusedRepairRequests[^1].RepairFiles.Should().Equal("src/File6.cs");
        runner.BuildRequests.Should().HaveCount(7);
        recorder.RecordedActivities.Should().Contain(a => a.message == "Build retry passed.");
    }

    [Fact]
    public async Task CompileRepair_FreshImprovedDiagnostics_RecomputeEligibleTargets()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/File1.cs", "src/File2.cs", "src/File3.cs" })
        };
        var runner = new ScriptedValidationRunner(new[]
        {
            new BuildValidationResult
            {
                Success = false,
                ErrorMessage = "build failed",
                StdOut = "src/File1.cs(1,1): error CS1002: ; expected\nsrc/File2.cs(2,1): error CS1002: ; expected"
            },
            new BuildValidationResult
            {
                Success = false,
                ErrorMessage = "build failed",
                StdOut = "src/File3.cs(3,1): error CS0103: Name is not defined"
            },
            new BuildValidationResult { Success = true }
        });
        var fingerprint = new TestFingerprintCalculator("a", "b", "c", "d");
        var processor = CreateProcessor(taskId, agent, runner, fingerprint);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.FocusedRepairRequests.Should().HaveCount(2);
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/File1.cs");
        agent.FocusedRepairRequests[1].RepairFiles.Should().Equal("src/File3.cs");
        agent.FocusedRepairRequests[1].IsFinalDiagnosticAttempt.Should().BeFalse();
    }

    [Fact]
    public async Task CompileRepair_GlobalSafetyCap_PreventsInfiniteConvergence()
    {
        var taskId = Guid.NewGuid();
        var files = Enumerable.Range(1, 13).Select(i => $"src/File{i}.cs").ToArray();
        var stdout = string.Join('\n', files.Select((file, index) => $"{file}({index + 1},1): error CS1002: ; expected"));
        var failure = new BuildValidationResult { Success = false, ErrorMessage = "build failed", StdOut = stdout };
        var agent = new TestDeveloperAgent { ResultToReturn = DeveloperAgentResult.Ok(files) };
        var runner = new ScriptedValidationRunner(Enumerable.Repeat(failure, 16));
        var fingerprint = new TestFingerprintCalculator(Enumerable.Range(0, 40).Select(i => $"fp-{i}").ToArray());
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.FocusedRepairRequests.Should().HaveCount(12);
        agent.FocusedRepairRequests.Select(request => request.RepairFiles.Single()).Should().Equal(files.Take(12));
        recorder.RecordedActivities.Should().Contain(a => a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
    }

    [Fact]
    public async Task CompileRepair_UniqueCompilerPathOutsideModifiedFiles_ExpandsScopeThenRebuilds()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "DevPilotScope_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        File.WriteAllText(Path.Combine(workspace, "src", "app.ts"), "export const app = 1;");
        File.WriteAllText(Path.Combine(workspace, "src", "config.ts"), "export const config = 1;");
        try
        {
            var taskId = Guid.NewGuid();
            var agent = new TestDeveloperAgent
            {
                ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/app.ts" })
            };
            agent.FocusedRepairResults.Enqueue(DeveloperAgentResult.Ok(new List<string> { "src/config.ts" }));
            var runner = new ScriptedValidationRunner(new[]
            {
                new BuildValidationResult
                {
                    Success = false,
                    ErrorMessage = "build failed",
                    StdOut = "src/config.ts(1,14): error TS2322: Type 'number' is not assignable to type 'string'."
                },
                new BuildValidationResult { Success = true }
            });
            var fingerprint = new TestFingerprintCalculator("before", "after");
            var recorder = new TestActivityRecorder();
            var processor = CreateProcessor(
                taskId,
                agent,
                runner,
                fingerprint,
                recorder,
                new TestWorkspaceManager { PreparedWorkspacePath = workspace });

            await processor.ProcessAsync(CreateContext(taskId));

            agent.FocusedRepairRequests.Should().ContainSingle();
            agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/config.ts");
            agent.FocusedRepairRequests[0].IsFinalDiagnosticAttempt.Should().BeFalse();
            runner.BuildRequests.Should().HaveCount(2);
            recorder.RecordedActivities.Should().Contain(a =>
                a.metadata != null &&
                a.metadata.ScopeExpandedByCompilerEvidence == true &&
                a.metadata.SelectedRepairTarget == "src/config.ts");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CompileRepair_WebhookStyleOneFileRepair_StillConverges()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/app.ts", "src/webhookController.ts" })
        };
        var runner = new ScriptedValidationRunner(new[]
        {
            new BuildValidationResult
            {
                Success = false,
                ErrorMessage = "build failed",
                StdOut = "src/app.ts(12,4): error TS2322: Type 'string' is not assignable to type 'number'."
            },
            new BuildValidationResult { Success = true }
        });
        var fingerprint = new TestFingerprintCalculator("before", "after");
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.FocusedRepairRequests.Should().ContainSingle();
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/app.ts");
        agent.FocusedRepairRequests[0].IsFinalDiagnosticAttempt.Should().BeFalse();
        runner.BuildRequests.Should().HaveCount(2);
        recorder.RecordedActivities.Should().Contain(a => a.message == "Build retry passed.");
    }

    private static string SeventeenCompilerDiagnostics()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 9; i++)
        {
            lines.Add($"src/App.cs({i},1): error CS1001: diagnostic {i}");
        }

        for (var i = 1; i <= 8; i++)
        {
            lines.Add($"src/Other.cs({i},1): error CS2001: diagnostic {i}");
        }

        return string.Join('\n', lines);
    }

    [Fact]
    public async Task TestRepair_RerunsTargetedTestBeforeRequiredFullSuite_WithoutBuild()
    {
        var taskId = Guid.NewGuid();
        var failedTest = FailedTodoTest();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/TodoService.cs", "src/Valid.cs" })
        };
        var runner = new ScriptedValidationRunner(
            new[] { new BuildValidationResult { Success = true }, new BuildValidationResult { Success = true } },
            new[] { failedTest, new TestValidationResult { Success = true }, new TestValidationResult { Success = true } });
        var fingerprint = new TestFingerprintCalculator("before", "after");
        var processor = CreateProcessor(taskId, agent, runner, fingerprint);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.CallCount.Should().Be(2);
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/TodoService.cs");
        runner.TestRequests.Should().HaveCount(3);
        runner.TestRequests[0].SkipBuild.Should().BeTrue();
        runner.TestRequests[0].TestFilter.Should().BeNull();
        runner.TestRequests[1].SkipBuild.Should().BeTrue();
        runner.TestRequests[1].TestFilter.Should().Be("DevPilot.Tests.TodoServiceTests.Filters_completed_todos");
        runner.TestRequests[2].SkipBuild.Should().BeTrue();
        runner.TestRequests[2].TestFilter.Should().BeNull();
    }

    [Fact]
    public async Task TestRepair_WhenRepairBreaksBuild_StopsWithCompilerEvidenceInsteadOfStaleTestRound()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/TodoService.cs", "src/Valid.cs" })
        };
        var runner = new ScriptedValidationRunner(
            new[]
            {
                new BuildValidationResult { Success = true },
                new BuildValidationResult
                {
                    Success = false,
                    ErrorMessage = "dotnet build failed after test repair",
                    StdOut = "src/TodoService.cs(51,7): error CS1002: ; expected"
                }
            },
            new[] { FailedTodoTest() });
        var fingerprint = new TestFingerprintCalculator("before", "after");
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        var act = () => processor.ProcessAsync(CreateContext(taskId));

        await act.Should().NotThrowAsync();
        agent.CallCount.Should().Be(2, "stale test evidence must not start a second test repair");
        runner.TestRequests.Should().HaveCount(1);
        recorder.RecordedActivities.Should().Contain(activity =>
            activity.stage == ExecutionStage.Build &&
            activity.metadata != null &&
            activity.metadata.ProgressResult == "NewBuildFailure" &&
            activity.metadata.FailureFingerprint != null);
    }

    [Fact]
    public async Task TestRepair_SameAuthoritativeFailure_StopsWithoutBroadSecondRepair()
    {
        var taskId = Guid.NewGuid();
        var failedTest = FailedTodoTest();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/TodoService.cs", "src/Valid.cs" })
        };
        var runner = new ScriptedValidationRunner(
            new[] { new BuildValidationResult { Success = true }, new BuildValidationResult { Success = true } },
            new[] { failedTest, failedTest, failedTest });
        var fingerprint = new TestFingerprintCalculator("before", "after", "final-before", "final-after");
        var recorder = new TestActivityRecorder();
        var processor = CreateProcessor(taskId, agent, runner, fingerprint, recorder);

        var act = () => processor.ProcessAsync(CreateContext(taskId));

        await act.Should().NotThrowAsync();
        agent.CallCount.Should().Be(3, "one changed test repair plus one final diagnostic-directed attempt");
        agent.FocusedRepairRequests.Should().HaveCount(2);
        agent.FocusedRepairRequests[0].RepairFiles.Should().Equal("src/TodoService.cs");
        agent.FocusedRepairRequests[1].IsFinalDiagnosticAttempt.Should().BeTrue();
        runner.TestRequests.Should().HaveCount(3, "same failure allows one final attempt then stops");
        recorder.RecordedActivities.Should().Contain(a => a.metadata != null && a.metadata.VerificationOutcome == "NeedsReview");
    }

    [Fact]
    public async Task NpmCiInfrastructureFailure_DoesNotTriggerFocusedRepair()
    {
        var taskId = Guid.NewGuid();
        var agent = new TestDeveloperAgent
        {
            ResultToReturn = DeveloperAgentResult.Ok(new List<string> { "src/models/issue.ts" })
        };
        var recorder = new TestActivityRecorder();
        var processor = new GitWorkspaceExecutionProcessor(
            new TestWorkspaceManager(),
            new TestExecutionRepository(),
            new TestImpactAnalysisRepository
            {
                AnalysisToReturn = new TaskImpactAnalysis
                {
                    Id = Guid.NewGuid(),
                    DevelopmentTaskId = taskId,
                    Status = ImpactAnalysisStatus.Completed
                }
            },
            agent,
            new NodePrerequisiteInfrastructureCheckRunner(),
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        await processor.ProcessAsync(CreateContext(taskId));

        agent.CallCount.Should().Be(1, "only initial generation should run");
        agent.FocusedRepairRequests.Should().BeEmpty();
        recorder.RecordedActivities.Should().Contain(activity =>
            activity.status == ExecutionActivityStatus.Failed &&
            activity.message.Contains("infrastructure failure", StringComparison.OrdinalIgnoreCase) &&
            activity.metadata != null &&
            activity.metadata.VerificationOutcome == "VerificationInfrastructureError");
    }

    private static GitWorkspaceExecutionProcessor CreateProcessor(
        Guid taskId,
        TestDeveloperAgent agent,
        IExecutionValidationRunner runner,
        IExecutionChangeFingerprintCalculator? fingerprint = null,
        TestActivityRecorder? recorder = null,
        IExecutionWorkspaceManager? workspaceManager = null,
        IConfiguration? configuration = null)
    {
        return new GitWorkspaceExecutionProcessor(
            workspaceManager ?? new TestWorkspaceManager(),
            new TestExecutionRepository(),
            new TestImpactAnalysisRepository
            {
                AnalysisToReturn = new TaskImpactAnalysis
                {
                    Id = Guid.NewGuid(),
                    DevelopmentTaskId = taskId,
                    Status = ImpactAnalysisStatus.Completed
                }
            },
            agent,
            new TestRepositoryCheckRunnerAdapter(runner),
            recorder ?? new TestActivityRecorder(),
            NullLogger<GitWorkspaceExecutionProcessor>.Instance,
            configuration,
            changeFingerprintCalculator: fingerprint);
    }

    private static ExecutionProcessingContext CreateContext(Guid taskId) => new(
        Guid.NewGuid(),
        taskId,
        "Focused repair",
        "Repair only the implicated behavior",
        null,
        Guid.NewGuid(),
        "/source",
        "Summary");

    private static TestValidationResult FailedTodoTest() => new()
    {
        Success = false,
        ExitCode = 1,
        ErrorMessage = "dotnet test failed.",
        StdOut = """
            Failed DevPilot.Tests.TodoServiceTests.Filters_completed_todos [10 ms]
              Error Message:
               Expected one completed todo, but found two.
              Stack Trace:
                 at DevPilot.Todos.TodoService.Filter(Boolean completed) in /workspace/path/src/TodoService.cs:line 41
            Failed! - Failed: 1, Passed: 10, Skipped: 0, Total: 11
            """
    };

    private sealed class ScriptedValidationRunner : IExecutionValidationRunner
    {
        private readonly Queue<BuildValidationResult> _buildResults;
        private readonly Queue<TestValidationResult> _testResults;

        public ScriptedValidationRunner(
            IEnumerable<BuildValidationResult> buildResults,
            IEnumerable<TestValidationResult>? testResults = null)
        {
            _buildResults = new Queue<BuildValidationResult>(buildResults);
            _testResults = new Queue<TestValidationResult>(testResults ?? new[] { new TestValidationResult { Success = true } });
        }

        public List<ExecutionValidationRequest> BuildRequests { get; } = new();
        public List<ExecutionValidationRequest> TestRequests { get; } = new();

        public Task<BuildValidationResult> ValidateBuildAsync(
            ExecutionValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            BuildRequests.Add(request);
            return Task.FromResult(_buildResults.Count > 0
                ? _buildResults.Dequeue()
                : new BuildValidationResult { Success = true });
        }

        public Task<TestValidationResult> ValidateTestAsync(
            ExecutionValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            TestRequests.Add(request);
            return Task.FromResult(_testResults.Count > 0
                ? _testResults.Dequeue()
                : new TestValidationResult { Success = true });
        }
    }

    private sealed class TestFingerprintCalculator : IExecutionChangeFingerprintCalculator
    {
        private readonly Queue<string> _fingerprints;

        public TestFingerprintCalculator(params string[] fingerprints)
        {
            _fingerprints = new Queue<string>(fingerprints);
        }

        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(
            string workspacePath,
            CancellationToken cancellationToken = default)
        {
            var fingerprint = _fingerprints.Count > 0 ? _fingerprints.Dequeue() : "fallback";
            return Task.FromResult(new ExecutionFingerprintResult(true, Fingerprint: fingerprint));
        }

        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(
            string workspacePath,
            string treeSha,
            string baseHeadSha,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionFingerprintResult(true, Fingerprint: treeSha, BaseHeadSha: baseHeadSha));
    }

    private sealed class NodePrerequisiteInfrastructureCheckRunner : IRepositoryCheckRunner
    {
        private static readonly RepositoryCheck BuildCheck = new(
            "node:package.json:build",
            "npm build",
            RepositoryCheckKind.Build,
            "node",
            "npm",
            new[] { "run", "build" },
            ".",
            true,
            TimeSpan.FromMinutes(5),
            RepositoryCheckSource.PackageJsonScript,
            "package.json",
            Order: 110);

        public Task<RepositoryProfile> DiscoverAsync(
            RepositoryPreflightRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryProfile(
                RepositoryVerificationState.Configured,
                new[] { "node" },
                new[] { BuildCheck }));

        public Task<RepositoryCheckResult> ExecuteAsync(
            RepositoryCheckExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryCheckResult
            {
                CheckId = request.Check.Id,
                CheckDisplayName = request.Check.DisplayName,
                CheckKind = request.Check.Kind,
                FailureCategory = RepositoryCheckFailureCategory.InfrastructureFailure,
                Success = false,
                ExitCode = 1,
                ErrorMessage = "Node dependency preparation failed (npm ci): exit 1. This is a repository prerequisite infrastructure failure."
            });
    }

    private sealed class UnconfiguredRepositoryCheckRunner : IRepositoryCheckRunner
    {
        public Task<RepositoryProfile> DiscoverAsync(
            RepositoryPreflightRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryProfile(
                RepositoryVerificationState.Unconfigured,
                Array.Empty<string>(),
                Array.Empty<RepositoryCheck>(),
                "No supported manifest was found."));

        public Task<RepositoryCheckResult> ExecuteAsync(
            RepositoryCheckExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No check should execute for an unconfigured profile.");
    }

    private class QueuedValidationRunner : IExecutionValidationRunner
    {
        private readonly Queue<BuildValidationResult> _buildResults;
        public int BuildCallCount { get; private set; }
        public int TestCallCount { get; private set; }

        public QueuedValidationRunner(Queue<BuildValidationResult> buildResults)
        {
            _buildResults = buildResults;
        }

        public Task<BuildValidationResult> ValidateBuildAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            BuildCallCount++;
            return Task.FromResult(_buildResults.Count > 0 ? _buildResults.Dequeue() : new BuildValidationResult { Success = false, ErrorMessage = "dotnet build failed." });
        }

        public Task<TestValidationResult> ValidateTestAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            return Task.FromResult(new TestValidationResult { Success = true });
        }
    }

    private class QueuedTestValidationRunner : IExecutionValidationRunner
    {
        private readonly Queue<TestValidationResult> _testResults;
        public int BuildCallCount { get; private set; }
        public int TestCallCount { get; private set; }

        public QueuedTestValidationRunner(Queue<TestValidationResult> testResults)
        {
            _testResults = testResults;
        }

        public Task<BuildValidationResult> ValidateBuildAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            BuildCallCount++;
            return Task.FromResult(new BuildValidationResult { Success = true });
        }

        public Task<TestValidationResult> ValidateTestAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            return Task.FromResult(_testResults.Count > 0 ? _testResults.Dequeue() : new TestValidationResult { Success = true });
        }
    }

    private class TestImpactAnalysisRepository : IImpactAnalysisRepository
    {
        public TaskImpactAnalysis? AnalysisToReturn { get; set; }

        public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(AnalysisToReturn);

        public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> StartAnalysisAtomicAsync(TaskImpactAnalysis analysis, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasActiveAnalysisForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleAnalysesAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private class TestDeveloperAgent : IDeveloperAgent
    {
        public DeveloperAgentResult ResultToReturn { get; set; } = DeveloperAgentResult.Ok(new List<string> { "Modified.cs" });
        public Queue<DeveloperAgentResult> FocusedRepairResults { get; } = new();
        public int CallCount { get; private set; }
        public List<DeveloperAgentRequest> Requests { get; } = new();
        public List<FocusedRepairRequest> FocusedRepairRequests { get; } = new();

        public Task<DeveloperAgentResult> GenerateAndApplyEditsAsync(DeveloperAgentRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            return Task.FromResult(ResultToReturn);
        }

        public Task<DeveloperAgentResult> ExecuteFocusedRepairAsync(FocusedRepairRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            FocusedRepairRequests.Add(request);
            var result = FocusedRepairResults.Count > 0 ? FocusedRepairResults.Dequeue() : ResultToReturn;
            return Task.FromResult(result);
        }
    }

    private class TestExecutionValidationRunner : IExecutionValidationRunner
    {
        public BuildValidationResult BuildResultToReturn { get; set; } = new BuildValidationResult { Success = true };
        public TestValidationResult TestResultToReturn { get; set; } = new TestValidationResult { Success = true };
        public int BuildCallCount { get; private set; }
        public int TestCallCount { get; private set; }
        public ExecutionValidationRequest? LastBuildRequest { get; private set; }
        public ExecutionValidationRequest? LastTestRequest { get; private set; }

        public Task<BuildValidationResult> ValidateBuildAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            BuildCallCount++;
            LastBuildRequest = request;
            return Task.FromResult(BuildResultToReturn);
        }

        public Task<TestValidationResult> ValidateTestAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            LastTestRequest = request;
            return Task.FromResult(TestResultToReturn);
        }
    }

    private class TestActivityRecorder : IExecutionActivityRecorder
    {
        public List<(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata)> RecordedActivities { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            RecordedActivities.Add((executionId, stage, status, message, metadata));
            return Task.CompletedTask;
        }
    }
}
