using DevPilot.Application.Executions.Commands.CreatePullRequest;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.GitProviders;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class CreatePullRequestCommandTests
{
    private readonly StubExecutionRepository _repository = new();
    private readonly FakeGitHubPullRequestService _prService = new();
    private readonly StubActivityRecorder _activityRecorder = new();

    [Fact]
    public async Task CreatePullRequest_CompletedApprovedCommittedPushed_SucceedsAndPersistsOpenedState()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Created);
        result.Response.Should().NotBeNull();
        result.Response!.PullRequestStatus.Should().Be("Open");
        result.Response.PullRequestNumber.Should().Be(12);
        result.Response.PullRequestUrl.Should().Be("https://github.com/enesscigdem/DevPilot/pull/12");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.Open);
        reloaded.PullRequestNumber.Should().Be(12);

        _activityRecorder.RecordedActivities.Should().ContainSingle(a =>
            a.Stage == ExecutionStage.PullRequest &&
            a.Status == ExecutionActivityStatus.Completed &&
            a.Message == "Pull request opened");
    }

    [Fact]
    public async Task CreatePullRequest_PendingOrRunningExecution_ReturnsConflictAndDoesNotMarkFailed()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Running, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("cannot request pull request");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.None);
    }

    [Fact]
    public async Task CreatePullRequest_PendingOrRejectedReview_ReturnsConflictAndDoesNotMarkFailed()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Pending, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("review status");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.None);
    }

    [Fact]
    public async Task CreatePullRequest_UncommittedExecution_ReturnsConflictAndDoesNotMarkFailed()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.None, ExecutionPushStatus.Pushed);
        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("not committed");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.None);
    }

    [Fact]
    public async Task CreatePullRequest_UnpushedExecution_ReturnsConflictAndDoesNotMarkFailed()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.None);
        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("not pushed");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.None);
    }

    [Fact]
    public async Task CreatePullRequest_LocalCommitShaDiffersFromRemoteSha_ReturnsConflict()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        execution.RemoteCommitSha = "different_remote_sha_1234567890";
        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("Local commit SHA does not match remote commit SHA");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.None);
    }

    [Fact]
    public async Task CreatePullRequest_HeadEqualsBase_ReturnsConflict()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        execution.BranchName = "master";
        execution.RemoteBranchName = "master";
        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("invalid or matches base branch");

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.None);
    }

    [Fact]
    public async Task CreatePullRequest_Definitive401AuthRejection_MarksStatusFailedAndAllowsImmediateRetry()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        _prService.ConfiguredResult = new ExecutionPullRequestServiceResult(
            Success: false,
            IsConfigurationError: true,
            IsConflict: false,
            PullRequestNumber: null,
            PullRequestUrl: null,
            BaseBranch: null,
            CreatedAt: null,
            ErrorMessage: "GitHub API authentication or permission failed (HTTP 401). Check configured token.",
            WasPostSent: true,
            IsDefinitiveNoMutationFailure: true);

        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.ExternalFailure);

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.Failed);
        CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(reloaded).Should().BeTrue("User should be allowed to retry immediately after fixing token");
    }

    [Fact]
    public async Task CreatePullRequest_Definitive403PermissionRejection_MarksStatusFailedAndAllowsImmediateRetry()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        _prService.ConfiguredResult = new ExecutionPullRequestServiceResult(
            Success: false,
            IsConfigurationError: true,
            IsConflict: false,
            PullRequestNumber: null,
            PullRequestUrl: null,
            BaseBranch: null,
            CreatedAt: null,
            ErrorMessage: "GitHub API authentication or permission failed (HTTP 403). Check configured token.",
            WasPostSent: true,
            IsDefinitiveNoMutationFailure: true);

        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.ExternalFailure);

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.Failed);
        CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(reloaded).Should().BeTrue("User should be allowed to retry immediately after fixing permissions");
    }

    [Fact]
    public async Task CreatePullRequest_PostTimeoutOrTransportUncertainty_RetainsStatusInProgress()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        _prService.ConfiguredResult = new ExecutionPullRequestServiceResult(
            Success: false,
            IsConfigurationError: false,
            IsConflict: false,
            PullRequestNumber: null,
            PullRequestUrl: null,
            BaseBranch: null,
            CreatedAt: null,
            ErrorMessage: "HTTP request timed out after POST sent.",
            WasPostSent: true);

        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.ExternalFailure);

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.InProgress, "Uncertain outcome must remain InProgress for stale lease recovery");
        CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(reloaded).Should().BeFalse("Uncertain status should not allow immediate double POST while lease is fresh");
    }

    [Fact]
    public async Task CreatePullRequest_AlreadyOpened_ReturnsPersistedResultIdempotentlyWithoutDuplicateActivity()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        execution.PullRequestStatus = ExecutionPullRequestStatus.Open;
        execution.PullRequestNumber = 42;
        execution.PullRequestUrl = "https://github.com/enesscigdem/DevPilot/pull/42";
        execution.PullRequestBaseBranch = "master";
        execution.PullRequestCreatedAt = DateTime.UtcNow.AddHours(-1);

        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Success);
        result.Response!.PullRequestNumber.Should().Be(42);

        _activityRecorder.RecordedActivities.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatePullRequest_FreshInProgressLease_ReturnsConflict()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        execution.PullRequestStatus = ExecutionPullRequestStatus.InProgress;
        execution.PullRequestClaimedAt = DateTime.UtcNow.AddSeconds(-30);

        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("in progress");
    }

    [Fact]
    public async Task CreatePullRequest_StaleInProgressLease_ReclaimsLeaseAndCreatesPR()
    {
        // Arrange
        var execution = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        execution.PullRequestStatus = ExecutionPullRequestStatus.InProgress;
        execution.PullRequestClaimedAt = DateTime.UtcNow.AddMinutes(-10);

        var handler = new CreatePullRequestCommandHandler(_repository, _prService, _activityRecorder, NullLogger<CreatePullRequestCommandHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new CreatePullRequestCommand(execution.Id));

        // Assert
        result.Status.Should().Be(CreatePullRequestResultStatus.Created);
        result.Response!.PullRequestNumber.Should().Be(12);

        var reloaded = await _repository.GetByIdAsync(execution.Id);
        reloaded!.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.Open);
    }

    [Fact]
    public void CalculateCanRequestPullRequest_ReturnsTrueOnlyForApprovedCommittedPushedAndNoneOrFailed()
    {
        var exec = SeedExecution(TaskExecutionStatus.Completed, ExecutionReviewStatus.Approved, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed);
        exec.PullRequestStatus = ExecutionPullRequestStatus.None;
        CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(exec).Should().BeTrue();

        exec.PullRequestStatus = ExecutionPullRequestStatus.Failed;
        CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(exec).Should().BeTrue();

        exec.PullRequestStatus = ExecutionPullRequestStatus.InProgress;
        CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(exec).Should().BeFalse();

        exec.PullRequestStatus = ExecutionPullRequestStatus.Open;
        CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(exec).Should().BeFalse();

        exec.ReviewStatus = ExecutionReviewStatus.Pending;
        exec.PullRequestStatus = ExecutionPullRequestStatus.None;
        CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(exec).Should().BeFalse();
    }

    [Fact]
    public void SanitizeDescription_StripsFakeDevPilotMarkers()
    {
        var rawDesc = "Task summary\n<!-- devpilot-execution:fake-id-1234 -->\nAdditional details";
        var sanitized = GitHubExecutionPullRequestService.SanitizeDescription(rawDesc);

        sanitized.Should().NotContain("<!-- devpilot-execution:fake-id-1234 -->");
        sanitized.Should().Contain("Task summary");
        sanitized.Should().Contain("Additional details");
    }

    [Fact]
    public void ValidatePullRequestInfo_RejectsMismatchedRepoOrMarker()
    {
        var validDto = new GitHubPullRequestDto(
            Number: 15,
            HtmlUrl: "https://github.com/enesscigdem/DevPilot/pull/15",
            State: "open",
            Merged: false,
            ClosedAt: null,
            MergedAt: null,
            HeadRef: "devpilot/task-123",
            HeadSha: "sha123",
            HeadRepoOwner: "enesscigdem",
            HeadRepoName: "DevPilot",
            BaseRef: "master",
            BaseRepoOwner: "enesscigdem",
            BaseRepoName: "DevPilot",
            Body: "<!-- devpilot-execution:a1b2c3d4-e5f6-7890-1234-567890123456 -->"
        );

        var expectedMarker = "<!-- devpilot-execution:a1b2c3d4-e5f6-7890-1234-567890123456 -->";

        GitHubExecutionPullRequestService.ValidatePullRequestInfo(validDto, "enesscigdem", "DevPilot", "devpilot/task-123", "sha123", "master", expectedMarker).Should().BeTrue();

        // Mismatched head ref
        GitHubExecutionPullRequestService.ValidatePullRequestInfo(validDto, "enesscigdem", "DevPilot", "wrong-branch", "sha123", "master", expectedMarker).Should().BeFalse();

        // Mismatched marker
        GitHubExecutionPullRequestService.ValidatePullRequestInfo(validDto, "enesscigdem", "DevPilot", "devpilot/task-123", "sha123", "master", "<!-- devpilot-execution:other-id -->").Should().BeFalse();
    }

    private TaskExecution SeedExecution(
        TaskExecutionStatus status,
        ExecutionReviewStatus reviewStatus,
        ExecutionCommitStatus commitStatus,
        ExecutionPushStatus pushStatus)
    {
        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            LocalPath = "/tmp/fake-workspace",
            Status = RepositoryWorkspaceStatus.Completed
        };

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            RepositoryWorkspace = workspace,
            Title = "Implement Order Filter",
            Description = "Add status filter to order list query.",
            Status = DevelopmentTaskStatus.Completed
        };

        var commitSha = "a1b2c3d4e5f67890123456789012345678901234";

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = status,
            ReviewStatus = reviewStatus,
            CommitStatus = commitStatus,
            CommitSha = commitSha,
            CommittedAt = DateTime.UtcNow.AddMinutes(-30),
            PushStatus = pushStatus,
            RemoteBranchName = "devpilot/task-b4198e4f-a555da2e",
            RemoteCommitSha = commitSha,
            PushedAt = DateTime.UtcNow.AddMinutes(-20),
            WorkspacePath = "/tmp/fake-workspace",
            BranchName = "devpilot/task-b4198e4f-a555da2e"
        };

        _repository.Executions[execution.Id] = execution;
        return execution;
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
        public Task<bool> TryClaimNewPushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStalePushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetPushCompletedAsync(Guid executionId, Guid attemptId, string remoteBranchName, string remoteCommitSha, DateTime pushedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPushFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> TryClaimNewPullRequestLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e) &&
                (e.PullRequestStatus == ExecutionPullRequestStatus.None || e.PullRequestStatus == ExecutionPullRequestStatus.Failed))
            {
                e.PullRequestStatus = ExecutionPullRequestStatus.InProgress;
                e.PullRequestAttemptId = attemptId;
                e.PullRequestClaimedAt = claimedAt;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> TryReclaimStalePullRequestLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e) && e.PullRequestStatus == ExecutionPullRequestStatus.InProgress)
            {
                e.PullRequestAttemptId = attemptId;
                e.PullRequestClaimedAt = claimedAt;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task SetPullRequestOpenedAsync(Guid executionId, Guid attemptId, int pullRequestNumber, string pullRequestUrl, string baseBranch, DateTime createdAt, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e))
            {
                e.PullRequestStatus = ExecutionPullRequestStatus.Open;
                e.PullRequestNumber = pullRequestNumber;
                e.PullRequestUrl = pullRequestUrl;
                e.PullRequestBaseBranch = baseBranch;
                e.PullRequestCreatedAt = createdAt;
            }
            return Task.CompletedTask;
        }

        public Task SetPullRequestFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e))
            {
                e.PullRequestStatus = ExecutionPullRequestStatus.Failed;
            }
            return Task.CompletedTask;
        }

        public Task<bool> TryClaimPullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStalePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ReleasePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime releasedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ReplacePullRequestTrackingSnapshotAsync(Guid executionId, Guid attemptId, ExecutionPullRequestRemoteState remoteState, ExecutionPullRequestIntegrityStatus integrityStatus, DateTime? closedAt, DateTime? mergedAt, ExecutionCiStatus ciStatus, IReadOnlyList<ExecutionCiCheck> checks, DateTime syncedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryClaimMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan syncTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStaleMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan mergeLeaseTimeout, TimeSpan syncTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetExecutionMergedAsync(Guid executionId, Guid attemptId, string mergeCommitSha, DateTime mergedAt, string mergeMethod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMergeFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeGitHubPullRequestService : IExecutionGitHubPullRequestService
    {
        public ExecutionPullRequestServiceResult ConfiguredResult { get; set; } = new(
            Success: true,
            IsConfigurationError: false,
            IsConflict: false,
            PullRequestNumber: 12,
            PullRequestUrl: "https://github.com/enesscigdem/DevPilot/pull/12",
            BaseBranch: "master",
            CreatedAt: DateTime.UtcNow,
            ErrorMessage: null,
            WasPostSent: true);

        public Task<ExecutionPullRequestServiceResult> CreateOrAdoptPullRequestAsync(TaskExecution execution, Guid attemptId, CancellationToken cancellationToken = default)
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
