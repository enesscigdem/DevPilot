using DevPilot.Application.Executions.Commands.StartExecution;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class StartExecutionCommandTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly RepositoryWorkspace _workspace;
    private readonly DevelopmentTask _task;
    private readonly TaskImpactAnalysis _completedAnalysis;

    private readonly FakeTaskRepository _taskRepository;
    private readonly FakeImpactAnalysisRepository _analysisRepository;
    private readonly FakeExecutionRepository _executionRepository;
    private readonly FakeExecutionDispatcher _dispatcher;
    private readonly StartExecutionCommandHandler _sut;

    public StartExecutionCommandTests()
    {
        _workspace = new RepositoryWorkspace
        {
            Id = _workspaceId,
            Owner = "testowner",
            Repository = "testrepo",
            LocalPath = "/test/repo",
            Branch = "main",
        };

        _task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = _workspaceId,
            RepositoryWorkspace = _workspace,
            Title = "Implement feature A",
            Description = "Task description",
            AcceptanceCriteria = "All criteria met",
            Priority = DevelopmentTaskPriority.High,
            Status = DevelopmentTaskStatus.Approved,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        };

        _completedAnalysis = new TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = _task.Id,
            Status = ImpactAnalysisStatus.Completed,
            Summary = "Plan approved summary: endpoints and handlers",
            Confidence = 95,
            StructuredResult = new ImpactAnalysisResultData
            {
                Summary = "Clean architecture endpoints",
            },
            CreatedAt = DateTime.UtcNow.AddMinutes(-25),
            CompletedAt = DateTime.UtcNow.AddMinutes(-24),
        };

        _taskRepository = new FakeTaskRepository();
        _taskRepository.Tasks[_task.Id] = _task;

        _analysisRepository = new FakeImpactAnalysisRepository();
        _analysisRepository.Analyses[_task.Id] = _completedAnalysis;

        _executionRepository = new FakeExecutionRepository();

        _dispatcher = new FakeExecutionDispatcher();

        _sut = new StartExecutionCommandHandler(
            _taskRepository,
            _analysisRepository,
            _executionRepository,
            _dispatcher,
            NullLogger<StartExecutionCommandHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_WhenTaskApprovedAndNoActiveExecution_CreatesPendingExecutionAndDispatches()
    {
        var command = new StartExecutionCommand(_task.Id);

        var result = await _sut.HandleAsync(command);

        result.Success.Should().BeTrue();
        result.Conflict.Should().BeFalse();
        result.NotFound.Should().BeFalse();
        result.Execution.Should().NotBeNull();
        result.Execution!.DevelopmentTaskId.Should().Be(_task.Id);
        result.Execution.Status.Should().Be(TaskExecutionStatus.Pending);

        _task.Status.Should().Be(DevelopmentTaskStatus.Executing);
        _dispatcher.DispatchedExecutionIds.Should().Contain(result.Execution.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenActivePendingExecutionExists_ReturnsConflict()
    {
        _executionRepository.ActiveExecutionExists = true;

        var command = new StartExecutionCommand(_task.Id);

        var result = await _sut.HandleAsync(command);

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("active execution");
        _dispatcher.DispatchedExecutionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenAtomicInsertFailsUniqueConstraint_ReturnsConflict()
    {
        _executionRepository.AtomicInsertSucceeds = false;

        var command = new StartExecutionCommand(_task.Id);

        var result = await _sut.HandleAsync(command);

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("active execution");
        _dispatcher.DispatchedExecutionIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(DevelopmentTaskStatus.Draft)]
    [InlineData(DevelopmentTaskStatus.ReadyForAnalysis)]
    [InlineData(DevelopmentTaskStatus.Analyzing)]
    [InlineData(DevelopmentTaskStatus.AwaitingApproval)]
    [InlineData(DevelopmentTaskStatus.Executing)]
    [InlineData(DevelopmentTaskStatus.Completed)]
    [InlineData(DevelopmentTaskStatus.Failed)]
    [InlineData(DevelopmentTaskStatus.Rejected)]
    public async Task HandleAsync_WhenTaskNotApproved_ReturnsConflict(DevelopmentTaskStatus nonApprovedStatus)
    {
        _task.Status = nonApprovedStatus;

        var command = new StartExecutionCommand(_task.Id);

        var result = await _sut.HandleAsync(command);

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("Approved");
        _dispatcher.DispatchedExecutionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenTaskNotFound_ReturnsNotFound()
    {
        var nonExistentTaskId = Guid.NewGuid();
        var command = new StartExecutionCommand(nonExistentTaskId);

        var result = await _sut.HandleAsync(command);

        result.Success.Should().BeFalse();
        result.NotFound.Should().BeTrue();
        _dispatcher.DispatchedExecutionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenAnalysisMissing_ReturnsConflict()
    {
        _analysisRepository.Analyses.Clear();

        var command = new StartExecutionCommand(_task.Id);

        var result = await _sut.HandleAsync(command);

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("analysis");
        _dispatcher.DispatchedExecutionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenAnalysisNotCompleted_ReturnsConflict()
    {
        _completedAnalysis.Status = ImpactAnalysisStatus.Failed;

        var command = new StartExecutionCommand(_task.Id);

        var result = await _sut.HandleAsync(command);

        result.Success.Should().BeFalse();
        result.Conflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("completed impact analysis");
        _dispatcher.DispatchedExecutionIds.Should().BeEmpty();
    }

    #region Test Fakes

    private sealed class FakeTaskRepository : ITaskRepository
    {
        public Dictionary<Guid, DevelopmentTask> Tasks { get; } = new();

        public Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Tasks.TryGetValue(id, out var task);
            return Task.FromResult(task);
        }

        public Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Tasks[task.Id] = task;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Tasks[task.Id] = task;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            Tasks.Remove(task.Id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(DevelopmentTaskQueryFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentTask>>(Tasks.Values.ToList());
    }

    private sealed class FakeImpactAnalysisRepository : IImpactAnalysisRepository
    {
        public Dictionary<Guid, TaskImpactAnalysis> Analyses { get; } = new();

        public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            Analyses.TryGetValue(taskId, out var analysis);
            return Task.FromResult(analysis);
        }

        public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default)
        {
            Analyses[analysis.DevelopmentTaskId] = analysis;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExecutionRepository : IExecutionRepository
    {
        public Dictionary<Guid, TaskExecution> Executions { get; } = new();
        public bool ActiveExecutionExists { get; set; }
        public bool AtomicInsertSucceeds { get; set; } = true;

        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            if (ActiveExecutionExists) return Task.FromResult(true);
            return Task.FromResult(Executions.Values.Any(e => e.DevelopmentTaskId == taskId &&
                (e.Status == TaskExecutionStatus.Pending || e.Status == TaskExecutionStatus.Running)));
        }

        public Task<bool> HasFailedExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Executions.Values.Any(e => e.DevelopmentTaskId == taskId && e.Status == TaskExecutionStatus.Failed));
        }

        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default)
        {
            if (!AtomicInsertSucceeds) return Task.FromResult(false);
            Executions[execution.Id] = execution;
            return Task.FromResult(true);
        }

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Executions.TryGetValue(id, out var exec);
            return Task.FromResult(exec);
        }

        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskExecution>>(Executions.Values.ToList());

        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetModelAsync(Guid executionId, string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
        public Task<bool> ClaimAsRunningAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RenewHeartbeatAsync(Guid executionId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CompleteWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> FailWithLeaseAsync(Guid executionId, Guid leaseToken, string errorMessage, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RequestCancellationAsync(Guid executionId, string? reason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AcknowledgeCancellationWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCancellationRequestedAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleRunningExecutionsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class FakeExecutionDispatcher : IExecutionDispatcher
    {
        public List<Guid> DispatchedExecutionIds { get; } = new();
        public bool ShouldThrow { get; set; }

        public void EnqueueProcessExecution(Guid executionId)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Failed to enqueue job in Hangfire.");
            }
            DispatchedExecutionIds.Add(executionId);
        }
    }

    #endregion
}
