using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Tests.Executions;

internal sealed class InMemoryExecutionRepository : IExecutionRepository
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
    public Task<bool> TryClaimNewPullRequestLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> TryReclaimStalePullRequestLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task SetPullRequestOpenedAsync(Guid executionId, Guid attemptId, int pullRequestNumber, string pullRequestUrl, string baseBranch, DateTime createdAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetPullRequestFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
