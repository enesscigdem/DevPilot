using System.Diagnostics;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Commands.ProcessExecution;
using DevPilot.Application.Executions.Commands.RunDeveloperAgent;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.DeveloperAgent;
using DevPilot.Infrastructure.Executions;
using DevPilot.Tests.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevPilot.Tests;

public class RunDeveloperAgentCommandHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly DeveloperAgent _developerAgent;
    private readonly GitExecutionWorkspaceManager _workspaceManager;
    private readonly FakeExecutionRepository _executionRepository;
    private readonly FakeImpactAnalysisRepository _analysisRepository;
    private readonly RunDeveloperAgentCommandHandler _handler;

    public RunDeveloperAgentCommandHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotRunAgentTests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/task-12345678-87654321";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        InitGitRepo(_originalRepoDir);

        File.WriteAllText(Path.Combine(_originalRepoDir, "App.cs"), "public class App {}");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");

        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _fakeAiProvider = new FakeAiProvider();
        var editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _developerAgent = new DeveloperAgent(
            _fakeAiProvider,
            editApplier,
            NullLogger<DeveloperAgent>.Instance);

        var cloneOptions = Options.Create(new Infrastructure.RepositoryClone.RepositoryCloneOptions
        {
            WorkspaceRoot = _tempDir
        });

        _workspaceManager = new GitExecutionWorkspaceManager(
            cloneOptions,
            NullLogger<GitExecutionWorkspaceManager>.Instance);

        _executionRepository = new FakeExecutionRepository();
        _analysisRepository = new FakeImpactAnalysisRepository();

        _handler = new RunDeveloperAgentCommandHandler(
            _executionRepository,
            _analysisRepository,
            _workspaceManager,
            _developerAgent,
            NullLogger<RunDeveloperAgentCommandHandler>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_originalRepoDir))
            {
                RunGit(_originalRepoDir, "worktree", "prune");
            }
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task HandleAsync_PreconditionMissingWorkspaceDetails_ReturnsConflict_AndDoesNotCallAi()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        _executionRepository.ExecutionToReturn = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = taskId,
            WorkspacePath = null,
            BranchName = null,
            Status = TaskExecutionStatus.Completed,
            DevelopmentTask = new DevelopmentTask { Id = taskId, Title = "Test Task" }
        };

        var result = await _handler.HandleAsync(new RunDeveloperAgentCommand(executionId));

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("persisted workspace path");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Pending)]
    [InlineData(TaskExecutionStatus.Running)]
    public async Task HandleAsync_ExecutionIsPendingOrRunning_ReturnsConflict_AndDoesNotCallAi(TaskExecutionStatus status)
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        _executionRepository.ExecutionToReturn = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = taskId,
            WorkspacePath = _worktreeDir,
            BranchName = _branchName,
            Status = status,
            DevelopmentTask = new DevelopmentTask { Id = taskId, Title = "Test Task" }
        };

        var result = await _handler.HandleAsync(new RunDeveloperAgentCommand(executionId));

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("owned by the automatic execution pipeline");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_PreconditionMissingCompletedImpactAnalysis_ReturnsConflict_AndDoesNotCallAi()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        _executionRepository.ExecutionToReturn = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = taskId,
            WorkspacePath = _worktreeDir,
            BranchName = _branchName,
            Status = TaskExecutionStatus.Completed,
            DevelopmentTask = new DevelopmentTask { Id = taskId, Title = "Test Task" }
        };

        _analysisRepository.AnalysisToReturn = null;

        var result = await _handler.HandleAsync(new RunDeveloperAgentCommand(executionId));

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("TaskImpactAnalysis");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_PreconditionWrongBranch_ReturnsConflict_AndDoesNotCallAi()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        _executionRepository.ExecutionToReturn = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = taskId,
            WorkspacePath = _worktreeDir,
            BranchName = "devpilot/different-branch",
            Status = TaskExecutionStatus.Completed,
            DevelopmentTask = new DevelopmentTask { Id = taskId, Title = "Test Task" }
        };

        _analysisRepository.AnalysisToReturn = new TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            Status = ImpactAnalysisStatus.Completed,
            Summary = "Test Summary"
        };

        var result = await _handler.HandleAsync(new RunDeveloperAgentCommand(executionId));

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("expected 'devpilot/different-branch'");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_PreconditionDirtyWorktree_ReturnsConflict_AndDoesNotCallAi()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        _executionRepository.ExecutionToReturn = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = taskId,
            WorkspacePath = _worktreeDir,
            BranchName = _branchName,
            Status = TaskExecutionStatus.Completed,
            DevelopmentTask = new DevelopmentTask { Id = taskId, Title = "Test Task" }
        };

        _analysisRepository.AnalysisToReturn = new TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            Status = ImpactAnalysisStatus.Completed,
            Summary = "Test Summary"
        };

        File.WriteAllText(Path.Combine(_worktreeDir, "Uncommitted.cs"), "public class Uncommitted {}");

        var result = await _handler.HandleAsync(new RunDeveloperAgentCommand(executionId));

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("uncommitted or untracked changes");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_AllPreconditionsMet_InvokesDeveloperAgent_AppliesEdits_UncommittedInWorktree()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        _executionRepository.ExecutionToReturn = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = taskId,
            WorkspacePath = _worktreeDir,
            BranchName = _branchName,
            Status = TaskExecutionStatus.Completed,
            DevelopmentTask = new DevelopmentTask
            {
                Id = taskId,
                Title = "Implement Feature X",
                Description = "Add method to App.cs",
                AcceptanceCriteria = "App class has Hello method"
            }
        };

        _analysisRepository.AnalysisToReturn = new TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            Status = ImpactAnalysisStatus.Completed,
            Summary = "Impacts App.cs",
            StructuredResult = new ImpactAnalysisResultData
            {
                Summary = "Impacts App.cs",
                ImpactedFiles = new List<ImpactedFile>
                {
                    new() { FilePath = "App.cs" }
                },
                ProposedPlan = new List<ProposedPlanStep>
                {
                    new() { Order = 1, Title = "Modify App", Description = "Add Hello method" }
                }
            }
        };

        _fakeAiProvider.ResponseToReturn = """
            {
              "files": [
                {
                  "filePath": "App.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public class App {}",
                      "replace": "public class App { public string Hello() => \"World\"; }"
                    }
                  ]
                }
              ]
            }
            """;

        var result = await _handler.HandleAsync(new RunDeveloperAgentCommand(executionId));

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1, "1 file edit call (manifest derived from approved impact analysis)");

        var fileContent = await File.ReadAllTextAsync(Path.Combine(_worktreeDir, "App.cs"));
        fileContent.Should().Be("public class App { public string Hello() => \"World\"; }");

        var status = RunGitWithOutput(_worktreeDir, "status", "--porcelain");
        status.Should().NotBeEmpty();
    }

    [Fact]
    public async Task NormalGitWorkspaceExecutionProcessor_PreparesWorkspaceAndPersistsDetails()
    {
        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var validationRunner = new FakeExecutionValidationRunner();

        _analysisRepository.AnalysisToReturn = new TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            Status = ImpactAnalysisStatus.Completed,
            Summary = "Summary",
            StructuredResult = new ImpactAnalysisResultData
            {
                Summary = "Summary",
                ImpactedFiles = new List<ImpactedFile>
                {
                    new() { FilePath = "App.cs" }
                }
            }
        };

        _fakeAiProvider.ResponseToReturn = """
            {
              "files": [
                {
                  "filePath": "App.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public class App {}",
                      "replace": "public class App { public string Hello() => \"World\"; }"
                    }
                  ]
                }
              ]
            }
            """;

        var processor = new GitWorkspaceExecutionProcessor(
            _workspaceManager,
            _executionRepository,
            _analysisRepository,
            _developerAgent,
            new TestRepositoryCheckRunnerAdapter(validationRunner),
            new NullActivityRecorder(),
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(
            ExecutionId: executionId,
            TaskId: taskId,
            TaskTitle: "Normal Execution Task",
            TaskDescription: "Description",
            AcceptanceCriteria: null,
            WorkspaceId: Guid.NewGuid(),
            WorkspaceLocalPath: _originalRepoDir,
            ImpactAnalysisSummary: "Summary");

        await processor.ProcessAsync(context);

        _executionRepository.UpdatedWorkspacePath.Should().NotBeNullOrWhiteSpace();
        _executionRepository.UpdatedBranchName.Should().NotBeNullOrWhiteSpace();
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test@example.com");
    }

    private static void RunGit(string workingDirectory, params string[] args)
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static string RunGitWithOutput(string workingDirectory, params string[] args)
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outStr = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return outStr;
    }
}

public class FakeExecutionRepository : IExecutionRepository
{
    public TaskExecution? ExecutionToReturn { get; set; }
    public string? UpdatedWorkspacePath { get; private set; }
    public string? UpdatedBranchName { get; private set; }

    public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ExecutionToReturn);
    }

    public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default)
    {
        UpdatedWorkspacePath = workspacePath;
        UpdatedBranchName = branchName;
        return Task.CompletedTask;
    }

    public Task SetModelAsync(Guid executionId, string model, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TaskExecution>>(Array.Empty<TaskExecution>());

    public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> HasFailedExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> TrySetReviewDecisionWithFingerprintAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string fingerprint, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(true);
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

    public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> TrySetReviewDecisionAsync(Guid executionId, DevPilot.Domain.Enums.ExecutionReviewStatus expectedStatus, DevPilot.Domain.Enums.ExecutionReviewStatus newStatus, DateTime decidedAt, string? rejectionReason, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

public class FakeImpactAnalysisRepository : IImpactAnalysisRepository
{
    public TaskImpactAnalysis? AnalysisToReturn { get; set; }

    public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(AnalysisToReturn);
    }

    public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> StartAnalysisAtomicAsync(TaskImpactAnalysis analysis, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> HasActiveAnalysisForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<int> ReconcileStaleAnalysesAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

public class FakeExecutionValidationRunner : IExecutionValidationRunner
{
    public Task<BuildValidationResult> ValidateBuildAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new BuildValidationResult { Success = true });

    public Task<TestValidationResult> ValidateTestAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new TestValidationResult { Success = true });
}

public class NullActivityRecorder : IExecutionActivityRecorder
{
    public Task RecordActivityAsync(
        Guid executionId,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        ExecutionActivityMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
