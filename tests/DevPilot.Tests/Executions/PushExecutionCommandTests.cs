using DevPilot.Application.Executions.Commands.PushExecution;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class PushExecutionCommandTests : IDisposable
{
    private readonly StubExecutionRepository _repository = new();
    private readonly StubGitPushService _pushService = new();
    private readonly StubActivityRecorder _activityRecorder = new();
    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task PushExecution_CompletedApprovedCommitted_SucceedsAndPersistsPushedState()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed);
        var handler = new PushExecutionCommandHandler(_repository, _pushService, _activityRecorder, NullLogger<PushExecutionCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new PushExecutionCommand(execution.Id));

        // Assert
        result.Status.Should().Be(PushExecutionResultStatus.Success);
        result.Response.Should().NotBeNull();
        result.Response!.PushStatus.Should().Be("Pushed");
        result.Response.RemoteCommitSha.Should().Be(execution.CommitSha);

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PushStatus.Should().Be(ExecutionPushStatus.Pushed);

        _activityRecorder.RecordedActivities.Should().ContainSingle(a =>
            a.Stage == ExecutionStage.Push &&
            a.Status == ExecutionActivityStatus.Completed &&
            a.Message == "Push completed");
    }

    [Fact]
    public async Task PushExecution_PendingOrRejectedReview_ReturnsConflictAndDoesNotMarkFailed()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Pending, ExecutionCommitStatus.Committed);
        var handler = new PushExecutionCommandHandler(_repository, _pushService, _activityRecorder, NullLogger<PushExecutionCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new PushExecutionCommand(execution.Id));

        // Assert
        result.Status.Should().Be(PushExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("Pending");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PushStatus.Should().Be(ExecutionPushStatus.None);
    }

    [Fact]
    public async Task PushExecution_UncommittedExecution_ReturnsConflictAndDoesNotMarkFailed()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.None);
        var handler = new PushExecutionCommandHandler(_repository, _pushService, _activityRecorder, NullLogger<PushExecutionCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new PushExecutionCommand(execution.Id));

        // Assert
        result.Status.Should().Be(PushExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("not committed");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PushStatus.Should().Be(ExecutionPushStatus.None);
    }

    [Fact]
    public async Task PushExecution_ForbiddenMasterBranch_ReturnsConflict()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed);
        execution.BranchName = "master";
        _pushService.ConfiguredResult = new ExecutionPushResult(false, ErrorMessage: "Branch 'master' is forbidden or invalid for remote push.");

        var handler = new PushExecutionCommandHandler(_repository, _pushService, _activityRecorder, NullLogger<PushExecutionCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new PushExecutionCommand(execution.Id));

        // Assert
        result.Status.Should().Be(PushExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("forbidden");
    }

    [Fact]
    public async Task PushExecution_AlreadyPushed_ReturnsPersistedResultIdempotentlyWithoutDuplicateActivity()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed);
        execution.PushStatus = ExecutionPushStatus.Pushed;
        execution.RemoteBranchName = execution.BranchName;
        execution.RemoteCommitSha = execution.CommitSha;
        execution.PushedAt = DateTime.UtcNow.AddMinutes(-5);

        var handler = new PushExecutionCommandHandler(_repository, _pushService, _activityRecorder, NullLogger<PushExecutionCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new PushExecutionCommand(execution.Id));

        // Assert
        result.Status.Should().Be(PushExecutionResultStatus.Success);
        result.Response!.PushStatus.Should().Be("Pushed");

        // Verify no new activity was recorded
        _activityRecorder.RecordedActivities.Should().BeEmpty();
    }

    [Fact]
    public async Task PushExecution_FreshInProgressLease_ReturnsConflict()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed);
        execution.PushStatus = ExecutionPushStatus.InProgress;
        execution.PushClaimedAt = DateTime.UtcNow.AddSeconds(-30);

        var handler = new PushExecutionCommandHandler(_repository, _pushService, _activityRecorder, NullLogger<PushExecutionCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new PushExecutionCommand(execution.Id));

        // Assert
        result.Status.Should().Be(PushExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("in progress");
    }

    [Fact]
    public async Task PushExecution_StaleInProgressLease_ReclaimsLeaseAndPushes()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed);
        execution.PushStatus = ExecutionPushStatus.InProgress;
        execution.PushClaimedAt = DateTime.UtcNow.AddMinutes(-10);

        var handler = new PushExecutionCommandHandler(_repository, _pushService, _activityRecorder, NullLogger<PushExecutionCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new PushExecutionCommand(execution.Id));

        // Assert
        result.Status.Should().Be(PushExecutionResultStatus.Success);
        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PushStatus.Should().Be(ExecutionPushStatus.Pushed);
    }

    private TaskExecution SeedExecution(
        TaskExecutionStatus status,
        ExecutionReviewStatus reviewStatus,
        ExecutionCommitStatus commitStatus)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"devpilot_push_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            Title = "Implement push foundation",
            RepositoryWorkspace = new RepositoryWorkspace
            {
                Id = Guid.NewGuid(),
                Owner = "owner",
                Repository = "repo"
            }
        };

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = status,
            ReviewStatus = reviewStatus,
            CommitStatus = commitStatus,
            CommitSha = "a1b2c3d4e5f67890123456789012345678901234",
            CommittedAt = DateTime.UtcNow,
            WorkspacePath = tempDir,
            BranchName = "devpilot/exec-test-1234",
            PushStatus = ExecutionPushStatus.None
        };

        _repository.Executions[execution.Id] = execution;
        return execution;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    private sealed class StubExecutionRepository : IExecutionRepository
    {
        public Dictionary<Guid, TaskExecution> Executions { get; } = new();

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Executions.TryGetValue(id, out var exec);
            return Task.FromResult(exec);
        }

        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TaskExecution>>(Executions.Values.ToList());

        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetReviewDecisionAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TrySetReviewDecisionWithFingerprintAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string fingerprint, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryClaimNewCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, string baseCommitSha, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStaleCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetCommitCompletedAsync(Guid executionId, Guid attemptId, string commitSha, DateTime committedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetCommitFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> TryClaimNewPushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e) &&
                (e.PushStatus == ExecutionPushStatus.None || e.PushStatus == ExecutionPushStatus.Failed))
            {
                e.PushStatus = ExecutionPushStatus.InProgress;
                e.PushAttemptId = attemptId;
                e.PushClaimedAt = claimedAt;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> TryReclaimStalePushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e) && e.PushStatus == ExecutionPushStatus.InProgress)
            {
                e.PushAttemptId = attemptId;
                e.PushClaimedAt = claimedAt;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task SetPushCompletedAsync(Guid executionId, Guid attemptId, string remoteBranchName, string remoteCommitSha, DateTime pushedAt, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e))
            {
                e.PushStatus = ExecutionPushStatus.Pushed;
                e.RemoteBranchName = remoteBranchName;
                e.RemoteCommitSha = remoteCommitSha;
                e.PushedAt = pushedAt;
            }
            return Task.CompletedTask;
        }

        public Task SetPushFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e))
            {
                e.PushStatus = ExecutionPushStatus.Failed;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class StubGitPushService : IExecutionGitPushService
    {
        public ExecutionPushResult ConfiguredResult { get; set; } = new(true, false, "devpilot/exec-test-1234", "a1b2c3d4e5f67890123456789012345678901234", DateTime.UtcNow);

        public Task<ExecutionPushResult> PushExecutionBranchAsync(TaskExecution execution, Guid attemptId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ConfiguredResult);
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
}
