using System.Collections.Concurrent;
using System.Diagnostics;
using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.RejectExecutionReview;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Queries.GetExecutionById;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class ExecutionReviewDecisionTests : IDisposable
{
    private readonly FakeExecutionRepository _executionRepository;
    private readonly StubWorkspaceManager _workspaceManager;
    private readonly StubActivityRecorder _activityRecorder;
    private readonly string _tempDir;
    private readonly string _workspaceDir;

    public ExecutionReviewDecisionTests()
    {
        _executionRepository = new FakeExecutionRepository();
        _workspaceManager = new StubWorkspaceManager();
        _activityRecorder = new StubActivityRecorder();

        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilot_ReviewDecision_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceDir = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceDir);
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
            // best effort cleanup
        }
    }

    private TaskExecution SeedCompletedExecution()
    {
        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = _workspaceDir,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            RepositoryWorkspace = workspace,
            Title = "Sample Task",
            Description = "Sample Task Description",
            Priority = DevelopmentTaskPriority.Medium,
            Status = DevelopmentTaskStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending,
            WorkspacePath = _workspaceDir,
            BranchName = "devpilot/execution-1",
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow,
        };

        _executionRepository.AddExecution(execution);
        return execution;
    }

    [Fact]
    public async Task ApproveExecutionReview_CompletedExecution_SetsApprovedAndRecordsReviewActivity()
    {
        // Arrange
        var execution = SeedCompletedExecution();
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new ApproveExecutionReviewCommandHandler(
            _executionRepository,
            _workspaceManager,
            fingerprintCalculator,
            _activityRecorder,
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new ApproveExecutionReviewCommand(execution.Id, fingerprintCalculator.SampleFingerprint));

        // Assert
        result.Status.Should().Be(ApproveExecutionReviewResultStatus.Success);
        result.Decision.Should().NotBeNull();
        result.Decision!.ReviewStatus.Should().Be("Approved");
        result.Decision.RejectionReason.Should().BeNull();

        var reloaded = await _executionRepository.GetByIdAsync(execution.Id);
        reloaded!.ReviewStatus.Should().Be(ExecutionReviewStatus.Approved);
        reloaded.ReviewDecidedAt.Should().NotBeNull();

        _activityRecorder.RecordedActivities.Should().ContainSingle(a =>
            a.ExecutionId == execution.Id &&
            a.Stage == ExecutionStage.Review &&
            a.Status == ExecutionActivityStatus.Completed &&
            a.Message == "Review approved");
    }

    [Fact]
    public async Task RejectExecutionReview_CompletedExecution_SetsRejectedAndRecordsReviewActivity()
    {
        // Arrange
        var execution = SeedCompletedExecution();
        var handler = new RejectExecutionReviewCommandHandler(
            _executionRepository,
            _workspaceManager,
            _activityRecorder,
            NullLogger<RejectExecutionReviewCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new RejectExecutionReviewCommand(execution.Id, "  Needs better tests  "));

        // Assert
        result.Status.Should().Be(RejectExecutionReviewResultStatus.Success);
        result.Decision.Should().NotBeNull();
        result.Decision!.ReviewStatus.Should().Be("Rejected");
        result.Decision.RejectionReason.Should().Be("Needs better tests");

        var reloaded = await _executionRepository.GetByIdAsync(execution.Id);
        reloaded!.ReviewStatus.Should().Be(ExecutionReviewStatus.Rejected);
        reloaded.ReviewRejectionReason.Should().Be("Needs better tests");

        _activityRecorder.RecordedActivities.Should().ContainSingle(a =>
            a.ExecutionId == execution.Id &&
            a.Stage == ExecutionStage.Review &&
            a.Status == ExecutionActivityStatus.Rejected &&
            a.Message == "Review rejected");
    }

    [Fact]
    public async Task RejectExecutionReview_ReasonExceeds1000Chars_ReturnsBadRequest()
    {
        // Arrange
        var execution = SeedCompletedExecution();
        var handler = new RejectExecutionReviewCommandHandler(
            _executionRepository,
            _workspaceManager,
            _activityRecorder,
            NullLogger<RejectExecutionReviewCommandHandler>.Instance);

        var longReason = new string('a', 1005);

        // Act
        var result = await handler.HandleAsync(new RejectExecutionReviewCommand(execution.Id, longReason));

        // Assert
        result.Status.Should().Be(RejectExecutionReviewResultStatus.BadRequest);
        result.ErrorMessage.Should().Contain("1000");

        var reloaded = await _executionRepository.GetByIdAsync(execution.Id);
        reloaded!.ReviewStatus.Should().Be(ExecutionReviewStatus.Pending);
    }

    [Fact]
    public async Task ConcurrentApproveAndReject_OnlyOneDecisionWins()
    {
        // Arrange
        var execution = SeedCompletedExecution();
        var fingerprintCalculator = new StubFingerprintCalculator();
        var approveHandler = new ApproveExecutionReviewCommandHandler(
            _executionRepository,
            _workspaceManager,
            fingerprintCalculator,
            _activityRecorder,
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        var rejectHandler = new RejectExecutionReviewCommandHandler(
            _executionRepository,
            _workspaceManager,
            _activityRecorder,
            NullLogger<RejectExecutionReviewCommandHandler>.Instance);

        // Act — trigger approve and reject concurrently
        var approveTask = Task.Run(() => approveHandler.HandleAsync(new ApproveExecutionReviewCommand(execution.Id, fingerprintCalculator.SampleFingerprint)));
        var rejectTask = Task.Run(() => rejectHandler.HandleAsync(new RejectExecutionReviewCommand(execution.Id, "Reject reason")));

        await Task.WhenAll(approveTask, rejectTask);

        var approveResult = await approveTask;
        var rejectResult = await rejectTask;

        // Assert — exactly one handler succeeded and the other failed with Conflict
        var successCount = (approveResult.Status == ApproveExecutionReviewResultStatus.Success ? 1 : 0) +
                           (rejectResult.Status == RejectExecutionReviewResultStatus.Success ? 1 : 0);

        successCount.Should().Be(1);

        if (approveResult.Status == ApproveExecutionReviewResultStatus.Success)
        {
            rejectResult.Status.Should().Be(RejectExecutionReviewResultStatus.Conflict);
        }
        else
        {
            approveResult.Status.Should().Be(ApproveExecutionReviewResultStatus.Conflict);
        }
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Pending)]
    [InlineData(TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    public async Task Decision_NonCompletedExecution_ReturnsConflict(TaskExecutionStatus status)
    {
        // Arrange
        var execution = SeedCompletedExecution();
        execution.Status = status;
        var fingerprintCalculator = new StubFingerprintCalculator();

        var approveHandler = new ApproveExecutionReviewCommandHandler(
            _executionRepository,
            _workspaceManager,
            fingerprintCalculator,
            _activityRecorder,
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        var rejectHandler = new RejectExecutionReviewCommandHandler(
            _executionRepository,
            _workspaceManager,
            _activityRecorder,
            NullLogger<RejectExecutionReviewCommandHandler>.Instance);

        // Act
        var approveResult = await approveHandler.HandleAsync(new ApproveExecutionReviewCommand(execution.Id, fingerprintCalculator.SampleFingerprint));
        var rejectResult = await rejectHandler.HandleAsync(new RejectExecutionReviewCommand(execution.Id, "reason"));

        // Assert
        approveResult.Status.Should().Be(ApproveExecutionReviewResultStatus.Conflict);
        rejectResult.Status.Should().Be(RejectExecutionReviewResultStatus.Conflict);
    }

    [Fact]
    public async Task Decision_ActivityRecordingFails_PersistedDecisionStillSucceeds()
    {
        // Arrange
        var execution = SeedCompletedExecution();
        var failingActivityRecorder = new FailingActivityRecorder();
        var fingerprintCalculator = new StubFingerprintCalculator();

        var handler = new ApproveExecutionReviewCommandHandler(
            _executionRepository,
            _workspaceManager,
            fingerprintCalculator,
            failingActivityRecorder,
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new ApproveExecutionReviewCommand(execution.Id, fingerprintCalculator.SampleFingerprint));

        // Assert — decision is successfully saved despite telemetry crash
        result.Status.Should().Be(ApproveExecutionReviewResultStatus.Success);
        var reloaded = await _executionRepository.GetByIdAsync(execution.Id);
        reloaded!.ReviewStatus.Should().Be(ExecutionReviewStatus.Approved);
    }

    [Fact]
    public async Task GetExecutionById_ExposesPersistedReviewStatus()
    {
        // Arrange
        var execution = SeedCompletedExecution();
        execution.ReviewStatus = ExecutionReviewStatus.Approved;

        var handler = new GetExecutionByIdQueryHandler(_executionRepository);

        // Act
        var result = await handler.HandleAsync(new GetExecutionByIdQuery(execution.Id));

        // Assert
        result.Found.Should().BeTrue();
        result.Execution.Should().NotBeNull();
        result.Execution!.ReviewStatus.Should().Be("Approved");
    }

    private sealed class FakeExecutionRepository : IExecutionRepository
    {
        private readonly ConcurrentDictionary<Guid, TaskExecution> _executions = new();

        public void AddExecution(TaskExecution execution)
        {
            _executions[execution.Id] = execution;
        }

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _executions.TryGetValue(id, out var execution);
            return Task.FromResult(execution);
        }

        public Task<bool> TrySetReviewDecisionAsync(
            Guid executionId,
            ExecutionReviewStatus expectedStatus,
            ExecutionReviewStatus newStatus,
            DateTime decidedAt,
            string? rejectionReason,
            CancellationToken cancellationToken = default)
        {
            if (!_executions.TryGetValue(executionId, out var execution))
            {
                return Task.FromResult(false);
            }

            lock (execution)
            {
                if (execution.Status == TaskExecutionStatus.Completed && execution.ReviewStatus == expectedStatus)
                {
                    execution.ReviewStatus = newStatus;
                    execution.ReviewDecidedAt = decidedAt;
                    execution.ReviewRejectionReason = rejectionReason;
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }
        }

        public Task<bool> TrySetReviewDecisionWithFingerprintAsync(
            Guid executionId,
            ExecutionReviewStatus expectedStatus,
            ExecutionReviewStatus newStatus,
            DateTime decidedAt,
            string fingerprint,
            string? rejectionReason,
            CancellationToken cancellationToken = default)
        {
            if (!_executions.TryGetValue(executionId, out var execution))
            {
                return Task.FromResult(false);
            }

            lock (execution)
            {
                if (execution.Status == TaskExecutionStatus.Completed && execution.ReviewStatus == expectedStatus)
                {
                    execution.ReviewStatus = newStatus;
                    execution.ReviewDecidedAt = decidedAt;
                    execution.ApprovedChangeFingerprint = fingerprint;
                    execution.ReviewRejectionReason = rejectionReason;
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }
        }

        public Task<bool> TryClaimNewCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, string baseCommitSha, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStaleCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetCommitCompletedAsync(Guid executionId, Guid attemptId, string commitSha, DateTime committedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetCommitFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskExecution>>(_executions.Values.ToList());
        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubWorkspaceManager : IExecutionWorkspaceManager
    {
        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(
            Guid executionId,
            Guid taskId,
            string sourceRepositoryLocalPath,
            string? sourceBranch = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(
            string workspacePath,
            string expectedBranchName,
            bool requireClean = true,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkspaceVerificationResult(
                IsValid: true,
                WorkspaceExists: true,
                BranchMatches: true,
                IsClean: true,
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

    private sealed class FailingActivityRecorder : IExecutionActivityRecorder
    {
        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated telemetry database exception");
        }
    }

    private sealed class StubFingerprintCalculator : IExecutionChangeFingerprintCalculator
    {
        public string SampleFingerprint { get; set; } = "sha256:1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(string workspacePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExecutionFingerprintResult(
                Success: true,
                Fingerprint: SampleFingerprint,
                BaseHeadSha: "base123",
                HasSensitiveFiles: false,
                ChangedFileCount: 1));
        }

        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(string workspacePath, string treeSha, string baseHeadSha, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExecutionFingerprintResult(
                Success: true,
                Fingerprint: SampleFingerprint,
                BaseHeadSha: baseHeadSha,
                HasSensitiveFiles: false,
                ChangedFileCount: 1));
        }
    }
}
