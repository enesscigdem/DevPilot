using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Tests.Executions;

internal class InMemoryExecutionRepository : IExecutionRepository
{
    private readonly object _syncLock = new();

    public Dictionary<Guid, TaskExecution> Executions { get; } = new();
    public Dictionary<Guid, DevelopmentTask> Tasks { get; } = new();

    public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_syncLock)
        {
            Executions.TryGetValue(id, out var exec);
            return Task.FromResult(exec);
        }
    }

    public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncLock)
        {
            return Task.FromResult<IReadOnlyList<TaskExecution>>(Executions.Values.ToList());
        }
    }

    public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default)
    {
        lock (_syncLock)
        {
            var hasActive = Executions.Values.Any(e =>
                e.DevelopmentTaskId == execution.DevelopmentTaskId &&
                (e.Status == TaskExecutionStatus.Pending || e.Status == TaskExecutionStatus.Running));

            if (hasActive)
            {
                return Task.FromResult(false);
            }

            Executions[execution.Id] = execution;
            Tasks[task.Id] = task;
            if (execution.DevelopmentTask == null) execution.DevelopmentTask = task;
            return Task.FromResult(true);
        }
    }
    public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Executions.Values.Any(e => e.DevelopmentTaskId == taskId && (e.Status == TaskExecutionStatus.Pending || e.Status == TaskExecutionStatus.Running)));
    public Task<bool> HasFailedExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Executions.Values.Any(e => e.DevelopmentTaskId == taskId && e.Status == TaskExecutionStatus.Failed));
    public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => ClaimAsRunningAsync(executionId, Guid.NewGuid(), cancellationToken);
    public Task<bool> ClaimAsRunningAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            if (e.Status != TaskExecutionStatus.Pending) return Task.FromResult(false);
            e.Status = TaskExecutionStatus.Running;
            e.StartedAt = DateTime.UtcNow;
            e.LeaseToken = leaseToken;
            e.HeartbeatAt = DateTime.UtcNow;
            e.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(45);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
    public Task<bool> RenewHeartbeatAsync(Guid executionId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e) && e.LeaseToken == leaseToken && e.Status == TaskExecutionStatus.Running)
        {
            e.HeartbeatAt = DateTime.UtcNow;
            e.LeaseExpiresAt = DateTime.UtcNow.Add(leaseDuration);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
    public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.Status = TaskExecutionStatus.Completed;
            e.CompletedAt = DateTime.UtcNow;
            e.LeaseExpiresAt = null;
        }
        return Task.CompletedTask;
    }
    public Task<bool> CompleteWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e) && e.LeaseToken == leaseToken && e.Status == TaskExecutionStatus.Running)
        {
            e.Status = TaskExecutionStatus.Completed;
            e.CompletedAt = DateTime.UtcNow;
            e.LeaseExpiresAt = null;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
    public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.Status = TaskExecutionStatus.Failed;
            e.CompletedAt = DateTime.UtcNow;
            e.ErrorMessage = errorMessage;
            e.LeaseExpiresAt = null;
        }
        return Task.CompletedTask;
    }
    public Task<bool> FailWithLeaseAsync(Guid executionId, Guid leaseToken, string errorMessage, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e) && e.LeaseToken == leaseToken && e.Status == TaskExecutionStatus.Running)
        {
            e.Status = TaskExecutionStatus.Failed;
            e.CompletedAt = DateTime.UtcNow;
            e.ErrorMessage = errorMessage;
            e.LeaseExpiresAt = null;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
    public Task<bool> RequestCancellationAsync(Guid executionId, string? reason, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e) && (e.Status == TaskExecutionStatus.Pending || e.Status == TaskExecutionStatus.Running) && e.CancellationRequestedAt == null)
        {
            e.CancellationRequestedAt = DateTime.UtcNow;
            e.CancellationReason = reason;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
    public Task<bool> AcknowledgeCancellationWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e) && e.LeaseToken == leaseToken && e.Status == TaskExecutionStatus.Running)
        {
            e.Status = TaskExecutionStatus.Cancelled;
            e.CancelledAt = DateTime.UtcNow;
            e.CompletedAt = DateTime.UtcNow;
            e.LeaseExpiresAt = null;
            if (e.DevelopmentTask != null)
            {
                e.DevelopmentTask.Status = DevelopmentTaskStatus.Approved;
            }
            if (Tasks.TryGetValue(e.DevelopmentTaskId, out var t))
            {
                t.Status = DevelopmentTaskStatus.Approved;
            }
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
    public Task<bool> IsCancellationRequestedAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            return Task.FromResult(e.CancellationRequestedAt != null);
        }
        return Task.FromResult(false);
    }
    public Task<int> ReconcileStaleRunningExecutionsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        int count = 0;
        var now = DateTime.UtcNow;
        foreach (var e in Executions.Values.Where(x => x.Status == TaskExecutionStatus.Running && ((x.LeaseExpiresAt != null && x.LeaseExpiresAt < now) || (x.LeaseExpiresAt == null && x.CreatedAt < cutoffUtc))))
        {
            e.Status = TaskExecutionStatus.Failed;
            e.CompletedAt = now;
            e.ErrorMessage = "Execution interrupted because the worker stopped unexpectedly.";
            e.LeaseExpiresAt = null;
            count++;
        }
        return Task.FromResult(count);
    }
    public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetModelAsync(Guid executionId, string model, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.Model = model;
        }
        return Task.CompletedTask;
    }
    public Task<bool> TrySetReviewDecisionAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string? rejectionReason, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.ReviewStatus = newStatus;
            e.ReviewDecidedAt = decidedAt;
            e.ReviewRejectionReason = rejectionReason;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> TrySetReviewDecisionWithFingerprintAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string fingerprint, string? rejectionReason, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.ReviewStatus = newStatus;
            e.ReviewDecidedAt = decidedAt;
            e.ApprovedChangeFingerprint = fingerprint;
            e.ReviewRejectionReason = rejectionReason;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> TryClaimNewCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, string baseCommitSha, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.CommitStatus = ExecutionCommitStatus.InProgress;
            e.CommitAttemptId = attemptId;
            e.CommitClaimedAt = claimedAt;
            e.BaseCommitSha = baseCommitSha;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> TryReclaimStaleCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.CommitStatus = ExecutionCommitStatus.InProgress;
            e.CommitAttemptId = attemptId;
            e.CommitClaimedAt = claimedAt;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task SetCommitCompletedAsync(Guid executionId, Guid attemptId, string commitSha, DateTime committedAt, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.CommitStatus = ExecutionCommitStatus.Committed;
            e.CommitSha = commitSha;
            e.CommittedAt = committedAt;
        }
        return Task.CompletedTask;
    }

    public Task SetCommitFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.CommitStatus = ExecutionCommitStatus.Failed;
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryClaimNewPushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
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
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.PushStatus = ExecutionPushStatus.InProgress;
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

    public Task<bool> TryClaimNewPullRequestLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
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
        if (Executions.TryGetValue(executionId, out var e))
        {
            e.PullRequestStatus = ExecutionPullRequestStatus.InProgress;
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

    public Task<bool> TryClaimPullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            if (e.PullRequestSyncAttemptId != null && e.PullRequestSyncClaimedAt != null)
                return Task.FromResult(false);

            e.PullRequestSyncAttemptId = attemptId;
            e.PullRequestSyncClaimedAt = claimedAt;
            e.PullRequestLastSyncAttemptAt = claimedAt;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> TryReclaimStalePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            if (e.PullRequestSyncClaimedAt != null && (claimedAt - e.PullRequestSyncClaimedAt.Value) < leaseTimeout)
            {
                return Task.FromResult(false);
            }

            e.PullRequestSyncAttemptId = attemptId;
            e.PullRequestSyncClaimedAt = claimedAt;
            e.PullRequestLastSyncAttemptAt = claimedAt;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task ReleasePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime releasedAt, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e) && e.PullRequestSyncAttemptId == attemptId)
        {
            e.PullRequestSyncAttemptId = null;
            e.PullRequestSyncClaimedAt = null;
        }
        return Task.CompletedTask;
    }

    public Task<bool> ReplacePullRequestTrackingSnapshotAsync(
        Guid executionId,
        Guid attemptId,
        ExecutionPullRequestRemoteState remoteState,
        ExecutionPullRequestIntegrityStatus integrityStatus,
        DateTime? closedAt,
        DateTime? mergedAt,
        ExecutionCiStatus ciStatus,
        IReadOnlyList<ExecutionCiCheck> checks,
        DateTime syncedAt,
        CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e) && e.PullRequestSyncAttemptId == attemptId)
        {
            e.PullRequestRemoteState = remoteState;
            e.PullRequestIntegrityStatus = integrityStatus;
            e.PullRequestClosedAt = closedAt;
            e.PullRequestMergedAt = mergedAt;
            e.CiStatus = ciStatus;
            e.CiChecks = checks.ToList();
            e.PullRequestLastSyncedAt = syncedAt;
            e.PullRequestSyncAttemptId = null;
            e.PullRequestSyncClaimedAt = null;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> TryClaimMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan syncTimeout, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            if (e.MergeStatus == ExecutionMergeStatus.Merged || (e.MergeStatus == ExecutionMergeStatus.InProgress && e.MergeAttemptId != attemptId))
                return Task.FromResult(false);

            e.MergeStatus = ExecutionMergeStatus.InProgress;
            e.MergeAttemptId = attemptId;
            e.MergeClaimedAt = claimedAt;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> TryReclaimStaleMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan mergeLeaseTimeout, TimeSpan syncTimeout, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e))
        {
            if (e.MergeStatus == ExecutionMergeStatus.Merged)
                return Task.FromResult(false);

            if (e.MergeClaimedAt != null && (claimedAt - e.MergeClaimedAt.Value) < mergeLeaseTimeout)
                return Task.FromResult(false);

            e.MergeStatus = ExecutionMergeStatus.InProgress;
            e.MergeAttemptId = attemptId;
            e.MergeClaimedAt = claimedAt;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task SetExecutionMergedAsync(Guid executionId, Guid attemptId, string mergeCommitSha, DateTime mergedAt, string mergeMethod, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e) && e.MergeAttemptId == attemptId)
        {
            e.MergeStatus = ExecutionMergeStatus.Merged;
            e.MergeCommitSha = mergeCommitSha;
            e.MergedAt = mergedAt;
            e.MergeMethod = mergeMethod;
            e.PullRequestRemoteState = ExecutionPullRequestRemoteState.Merged;
            e.PullRequestMergedAt = mergedAt;
            e.MergeAttemptId = null;
            e.MergeClaimedAt = null;
            e.PullRequestSyncAttemptId = null;
            e.PullRequestSyncClaimedAt = null;
        }
        return Task.CompletedTask;
    }

    public Task SetMergeFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default)
    {
        if (Executions.TryGetValue(executionId, out var e) && e.MergeAttemptId == attemptId)
        {
            e.MergeStatus = ExecutionMergeStatus.Failed;
            e.MergeAttemptId = null;
            e.MergeClaimedAt = null;
        }
        return Task.CompletedTask;
    }

    public void Seed(TaskExecution execution)
    {
        Executions[execution.Id] = execution;
    }
}
