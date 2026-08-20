using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class CommitExecutionCommandTests : IDisposable
{
    private readonly StubExecutionRepository _repository = new();
    private readonly StubWorkspaceManager _workspaceManager = new();
    private readonly StubGitCommitService _commitService = new();
    private readonly StubActivityRecorder _activityRecorder = new();
    private readonly StubFingerprintCalculator _fingerprintCalculator = new();
    private readonly StubActivityRepository _activityRepository = new();

    [Fact]
    public async Task ApproveReview_WithStaleFingerprint_ReturnsConflict()
    {
        // Arrange
        var execution = SeedExecution(ExecutionReviewStatus.Pending);
        _fingerprintCalculator.CurrentFingerprint = "sha256:current_worktree_hash_1234567890abcdef";

        var handler = new ApproveExecutionReviewCommandHandler(
            _repository,
            _activityRepository,
            _workspaceManager,
            _fingerprintCalculator,
            _activityRecorder,
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        // Act — pass an old/stale fingerprint
        var command = new ApproveExecutionReviewCommand(execution.Id, "sha256:stale_old_hash_9876543210fedcba");
        var result = await handler.HandleAsync(command);

        // Assert
        result.Status.Should().Be(ApproveExecutionReviewResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("Review changes have changed");
    }

    [Fact]
    public async Task CommitExecution_ApprovedExecution_SucceedsAndPersistsCommitState()
    {
        // Arrange
        var execution = SeedExecution(ExecutionReviewStatus.Approved);
        execution.ApprovedChangeFingerprint = _fingerprintCalculator.CurrentFingerprint;

        var handler = new CommitExecutionCommandHandler(
            _repository,
            _workspaceManager,
            _commitService,
            _activityRecorder,
            NullLogger<CommitExecutionCommandHandler>.Instance);

        // Act
        var command = new CommitExecutionCommand(execution.Id);
        var result = await handler.HandleAsync(command);

        // Assert
        result.Status.Should().Be(CommitExecutionResultStatus.Success);
        result.Response.Should().NotBeNull();
        result.Response!.CommitStatus.Should().Be("Committed");
        result.Response.CommitSha.Should().Be("a1b2c3d4e5f67890123456789012345678901234");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.CommitStatus.Should().Be(ExecutionCommitStatus.Committed);
        reloaded.CommitSha.Should().Be("a1b2c3d4e5f67890123456789012345678901234");

        _activityRecorder.RecordedActivities.Should().ContainSingle(a =>
            a.Stage == ExecutionStage.Commit &&
            a.Status == ExecutionActivityStatus.Completed &&
            a.Message == "Commit completed");
    }

    [Fact]
    public async Task CommitExecution_PendingReviewExecution_ReturnsConflict()
    {
        // Arrange
        var execution = SeedExecution(ExecutionReviewStatus.Pending);

        var handler = new CommitExecutionCommandHandler(
            _repository,
            _workspaceManager,
            _commitService,
            _activityRecorder,
            NullLogger<CommitExecutionCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CommitExecutionCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CommitExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("Pending");
    }

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Best effort
            }
        }
    }

    private TaskExecution SeedExecution(ExecutionReviewStatus reviewStatus)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "devpilot_commit_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);

        InitGitRepo(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "README.md"), "# Test\n");
        RunGit(tempDir, "add .");
        RunGit(tempDir, "commit -m \"Initial commit\"");

        var taskId = Guid.NewGuid();

        var task = new DevelopmentTask
        {
            Id = taskId,
            Title = "Test Commit Task",
            RepositoryWorkspaceId = Guid.NewGuid(),
            Status = DevelopmentTaskStatus.Approved
        };

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = reviewStatus,
            WorkspacePath = tempDir,
            BranchName = "devpilot/exec-12345",
            CommitStatus = ExecutionCommitStatus.None
        };

        _repository.Add(execution);
        return execution;
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config user.name \"Test User\"");
        RunGit(path, "config user.email \"test@devpilot.local\"");
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }

    private sealed class StubExecutionRepository : InMemoryExecutionRepository
    {
        public void Add(TaskExecution execution) => Executions[execution.Id] = execution;
    }

    private sealed class StubWorkspaceManager : IExecutionWorkspaceManager
    {
        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceVerificationResult(true, true, true, true, null));
    }

    private sealed class StubGitCommitService : IExecutionGitCommitService
    {
        public Task<ExecutionCommitResult> CommitApprovedExecutionAsync(TaskExecution execution, string taskTitle, Guid attemptId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExecutionCommitResult(
                Success: true,
                IsAlreadyCommitted: false,
                CommitSha: "a1b2c3d4e5f67890123456789012345678901234",
                CommittedAt: DateTime.UtcNow,
                ErrorMessage: null));
        }
    }

    private sealed class StubActivityRecorder : IExecutionActivityRecorder
    {
        public List<(Guid ExecutionId, ExecutionStage Stage, ExecutionActivityStatus Status, string Message)> RecordedActivities { get; } = new();

        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default)
        {
            RecordedActivities.Add((executionId, stage, status, message));
            return Task.CompletedTask;
        }
    }

    private sealed class StubFingerprintCalculator : IExecutionChangeFingerprintCalculator
    {
        public string CurrentFingerprint { get; set; } = "sha256:default_fingerprint_123456";

        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(string workspacePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExecutionFingerprintResult(
                Success: true,
                Fingerprint: CurrentFingerprint,
                BaseHeadSha: "0000000000000000000000000000000000000000",
                HasSensitiveFiles: false,
                ChangedFileCount: 1));
        }

        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(string workspacePath, string treeSha, string baseHeadSha, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExecutionFingerprintResult(
                Success: true,
                Fingerprint: CurrentFingerprint,
                BaseHeadSha: baseHeadSha,
                HasSensitiveFiles: false,
                ChangedFileCount: 1));
        }
    }

    private sealed class StubActivityRepository : IExecutionActivityRepository
    {
        public Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ExecutionActivity>>(new List<ExecutionActivity>
            {
                new ExecutionActivity { ExecutionId = executionId, Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed, Message = "Build passed" },
                new ExecutionActivity { ExecutionId = executionId, Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed, Message = "Test passed" }
            });
        }
    }
}
