using DevPilot.Domain.Entities;

namespace DevPilot.Application.Executions.Ports;

public interface IExecutionRepository
{
    /// <summary>
    /// Atomically persists the new <see cref="TaskExecution"/> record and updates the
    /// <see cref="DevelopmentTask"/> status in a single <c>SaveChangesAsync</c> call,
    /// which EF Core / PostgreSQL wraps in one implicit transaction.
    ///
    /// Returns <c>false</c> when the database unique partial index
    /// <c>IX_TaskExecutions_ActivePerTask</c> rejects the insert because a concurrent
    /// request already created an active execution for the same task (SqlState 23505).
    /// In that case the caller should treat the result as a conflict.
    /// </summary>
    Task<bool> StartExecutionAtomicAsync(
        TaskExecution execution,
        DevelopmentTask task,
        CancellationToken cancellationToken = default);

    Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Optimistic pre-check: returns <c>true</c> when a <c>Pending</c> or <c>Running</c>
    /// execution already exists for <paramref name="taskId"/>.
    /// The database unique partial index provides the authoritative concurrent guard.
    /// </summary>
    Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when at least one <c>Failed</c> execution exists for <paramref name="taskId"/>.
    /// </summary>
    Task<bool> HasFailedExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically transitions a <c>Pending</c> execution to <c>Running</c> and returns
    /// <c>true</c>. Sets <see cref="TaskExecution.StartedAt"/> to UTC now.
    /// </summary>
    Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically transitions a <c>Pending</c> execution to <c>Running</c> with the specified
    /// <paramref name="leaseToken"/> and lease expiration time.
    /// </summary>
    Task<bool> ClaimAsRunningAsync(
        Guid executionId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews the execution lease heartbeat if the execution is still Running and the lease token matches.
    /// </summary>
    Task<bool> RenewHeartbeatAsync(
        Guid executionId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions a <c>Running</c> execution to <c>Completed</c>.
    /// Sets <see cref="TaskExecution.CompletedAt"/> and advances the linked
    /// <see cref="DevelopmentTask"/> status to <c>Completed</c>.
    /// </summary>
    Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions a <c>Running</c> execution with matching <paramref name="leaseToken"/> to <c>Completed</c>.
    /// </summary>
    Task<bool> CompleteWithLeaseAsync(
        Guid executionId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions a <c>Running</c> execution to <c>Failed</c>.
    /// Sets <see cref="TaskExecution.CompletedAt"/>, persists the
    /// <paramref name="errorMessage"/>, and advances the linked
    /// <see cref="DevelopmentTask"/> status to <c>Failed</c>.
    /// </summary>
    Task FailAsync(
        Guid executionId,
        string errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions a <c>Running</c> execution with matching <paramref name="leaseToken"/> to <c>Failed</c>.
    /// </summary>
    Task<bool> FailWithLeaseAsync(
        Guid executionId,
        Guid leaseToken,
        string errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a cancellation request for a non-terminal execution.
    /// </summary>
    Task<bool> RequestCancellationAsync(
        Guid executionId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges cancellation by the owning worker with matching <paramref name="leaseToken"/>,
    /// transitioning execution to <c>Cancelled</c> and returning the task to <c>Approved</c>.
    /// </summary>
    Task<bool> AcknowledgeCancellationWithLeaseAsync(
        Guid executionId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether cancellation has been requested for the execution.
    /// </summary>
    Task<bool> IsCancellationRequestedAsync(
        Guid executionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles stale Running executions whose lease has expired or legacy Running executions before cutoff.
    /// </summary>
    Task<int> ReconcileStaleRunningExecutionsAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the isolated workspace local path and dedicated branch name for an execution.
    /// </summary>
    Task UpdateWorkspaceDetailsAsync(
        Guid executionId,
        string workspacePath,
        string branchName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the AI model name used for an execution.
    /// </summary>
    Task SetModelAsync(
        Guid executionId,
        string model,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically updates the review decision for a completed execution if the current review status matches expected.
    /// Returns true if exactly one row was updated, false otherwise.
    /// </summary>
    Task<bool> TrySetReviewDecisionAsync(
        Guid executionId,
        DevPilot.Domain.Enums.ExecutionReviewStatus expectedStatus,
        DevPilot.Domain.Enums.ExecutionReviewStatus newStatus,
        DateTime decidedAt,
        string? rejectionReason,
        CancellationToken cancellationToken = default);

    Task<bool> TrySetReviewDecisionWithFingerprintAsync(
        Guid executionId,
        DevPilot.Domain.Enums.ExecutionReviewStatus expectedStatus,
        DevPilot.Domain.Enums.ExecutionReviewStatus newStatus,
        DateTime decidedAt,
        string fingerprint,
        string? rejectionReason,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimNewCommitLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        string baseCommitSha,
        CancellationToken cancellationToken = default);

    Task<bool> TryReclaimStaleCommitLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken = default);

    Task SetCommitCompletedAsync(
        Guid executionId,
        Guid attemptId,
        string commitSha,
        DateTime committedAt,
        CancellationToken cancellationToken = default);

    Task SetCommitFailedAsync(
        Guid executionId,
        Guid attemptId,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimNewPushLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryReclaimStalePushLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken = default);

    Task SetPushCompletedAsync(
        Guid executionId,
        Guid attemptId,
        string remoteBranchName,
        string remoteCommitSha,
        DateTime pushedAt,
        CancellationToken cancellationToken = default);

    Task SetPushFailedAsync(
        Guid executionId,
        Guid attemptId,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimNewPullRequestLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryReclaimStalePullRequestLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken = default);

    Task SetPullRequestOpenedAsync(
        Guid executionId,
        Guid attemptId,
        int pullRequestNumber,
        string pullRequestUrl,
        string baseBranch,
        DateTime createdAt,
        CancellationToken cancellationToken = default);

    Task SetPullRequestFailedAsync(
        Guid executionId,
        Guid attemptId,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimPullRequestSyncLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryReclaimStalePullRequestSyncLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken = default);

    Task ReleasePullRequestSyncLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime attemptAt,
        CancellationToken cancellationToken = default);

    Task<bool> ReplacePullRequestTrackingSnapshotAsync(
        Guid executionId,
        Guid attemptId,
        DevPilot.Domain.Enums.ExecutionPullRequestRemoteState remoteState,
        DevPilot.Domain.Enums.ExecutionPullRequestIntegrityStatus integrityStatus,
        DateTime? closedAt,
        DateTime? mergedAt,
        DevPilot.Domain.Enums.ExecutionCiStatus ciStatus,
        IReadOnlyList<ExecutionCiCheck> checks,
        DateTime syncedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimMergeLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan syncLeaseTimeout,
        CancellationToken cancellationToken = default);

    Task<bool> TryReclaimStaleMergeLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan mergeLeaseTimeout,
        TimeSpan syncLeaseTimeout,
        CancellationToken cancellationToken = default);

    Task SetExecutionMergedAsync(
        Guid executionId,
        Guid attemptId,
        string mergeCommitSha,
        DateTime mergedAt,
        string mergeMethod = "merge",
        CancellationToken cancellationToken = default);

    Task SetMergeFailedAsync(
        Guid executionId,
        Guid attemptId,
        CancellationToken cancellationToken = default);
}
