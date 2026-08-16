using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class GitWorkspaceExecutionProcessorTests
{
    [Fact]
    public async Task ProcessAsync_FullSuccess_ExecutesStagesInOrder_BuildAndTestReceiveNullTargetPath()
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

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            validationRunner,
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
        validationRunner.LastBuildRequest?.TargetPath.Should().BeNull();
        validationRunner.LastTestRequest?.TargetPath.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_WorkspacePreparationFails_ThrowsException_AndDoesNotCallAgentOrValidation()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager { PrepareSuccess = false, PrepareErrorMessage = "Git error" };
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository();
        var agent = new TestDeveloperAgent();
        var validationRunner = new TestExecutionValidationRunner();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            validationRunner,
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
        ex.WithMessage("*Execution workspace preparation failed: Git error*");

        agent.CallCount.Should().Be(0);
        validationRunner.BuildCallCount.Should().Be(0);
        validationRunner.TestCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_PreAiVerificationFails_ThrowsException_AndDoesNotCallAgentOrValidation()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager { VerifyCleanSuccess = false, VerifyErrorMessage = "Dirty worktree pre-AI" };
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository();
        var agent = new TestDeveloperAgent();
        var validationRunner = new TestExecutionValidationRunner();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            validationRunner,
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
        ex.WithMessage("*Developer Agent failed: Execution workspace verification failed prior to AI invocation*");

        agent.CallCount.Should().Be(0);
        validationRunner.BuildCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_DeveloperAgentFails_ThrowsStageError_DoesNotCallValidation()
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

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            validationRunner,
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
    }

    [Fact]
    public async Task ProcessAsync_DeveloperAgentProducesZeroModifiedFiles_ThrowsStageError_DoesNotCallValidation()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var workspaceManager = new TestWorkspaceManager();
        var executionRepo = new TestExecutionRepository();
        var impactRepo = new TestImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed }
        };
        var agent = new TestDeveloperAgent { ResultToReturn = DeveloperAgentResult.Ok(new List<string>()) };
        var validationRunner = new TestExecutionValidationRunner();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            validationRunner,
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
        ex.WithMessage("Developer Agent failed: Developer Agent returned success but produced zero modified files.");

        agent.CallCount.Should().Be(1);
        validationRunner.BuildCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_BuildFails_ThrowsBuildError_DoesNotInvokeTestValidation()
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

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            validationRunner,
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
        ex.WithMessage("Build validation failed: dotnet build failed with exit code 1.");

        validationRunner.BuildCallCount.Should().Be(1);
        validationRunner.TestCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_TestFails_ThrowsTestError()
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

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            agent,
            validationRunner,
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
        ex.WithMessage("Test validation failed: dotnet test failed with exit code 1.");

        validationRunner.BuildCallCount.Should().Be(1);
        validationRunner.TestCallCount.Should().Be(1);
    }

    // ── Helper Test Fakes ──────────────────────────────────────────────────────────
    private class TestWorkspaceManager : IExecutionWorkspaceManager
    {
        public bool PrepareSuccess { get; set; } = true;
        public string PrepareErrorMessage { get; set; } = "";
        public bool PrepareCalled { get; private set; }
        public bool VerifyCleanSuccess { get; set; } = true;
        public string VerifyErrorMessage { get; set; } = "";
        public List<bool> VerifyRequireCleanCalls { get; } = new();

        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(
            Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default)
        {
            PrepareCalled = true;
            if (!PrepareSuccess) return Task.FromResult(new ExecutionWorkspaceResult("", "", Success: false, ErrorMessage: PrepareErrorMessage));
            return Task.FromResult(new ExecutionWorkspaceResult("/workspace/path", "devpilot/branch", Success: true));
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

        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default)
        {
            UpdatedWorkspacePath = workspacePath;
            UpdatedBranchName = branchName;
            return Task.CompletedTask;
        }

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskExecution?>(null);
        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskExecution>>(Array.Empty<TaskExecution>());
        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class TestImpactAnalysisRepository : IImpactAnalysisRepository
    {
        public TaskImpactAnalysis? AnalysisToReturn { get; set; }

        public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(AnalysisToReturn);

        public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class TestDeveloperAgent : IDeveloperAgent
    {
        public DeveloperAgentResult ResultToReturn { get; set; } = DeveloperAgentResult.Ok(new List<string> { "Modified.cs" });
        public int CallCount { get; private set; }

        public Task<DeveloperAgentResult> GenerateAndApplyEditsAsync(DeveloperAgentRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ResultToReturn);
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
}
