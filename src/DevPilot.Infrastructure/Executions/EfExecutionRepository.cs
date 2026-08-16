using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DevPilot.Infrastructure.Executions;

public sealed class EfExecutionRepository : IExecutionRepository
{
    private readonly DevPilotDbContext _dbContext;

    public EfExecutionRepository(DevPilotDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Atomicity is achieved by staging both the <see cref="TaskExecution"/> insert and the
    /// <see cref="DevelopmentTask"/> update in the same <see cref="DbContext"/> change-tracker
    /// and flushing them with a single <c>SaveChangesAsync</c> call, which EF Core sends to
    /// PostgreSQL as a single implicit transaction.
    ///
    /// The unique partial index <c>IX_TaskExecutions_ActivePerTask</c>
    /// (<c>ON TaskExecutions (DevelopmentTaskId) WHERE "Status" IN ('Pending', 'Running')</c>)
    /// provides the authoritative, race-condition-safe guard against duplicate active executions.
    /// A <see cref="PostgresException"/> with <c>SqlState 23505</c> (unique_violation) is caught
    /// here and translated to a <c>false</c> return value so the application layer stays free of
    /// infrastructure concerns.
    /// </remarks>
    public async Task<bool> StartExecutionAtomicAsync(
        TaskExecution execution,
        DevelopmentTask task,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Stage both writes in the shared, scoped DbContext.
            // The task entity is already tracked (loaded via ITaskRepository.GetByIdAsync
            // on the same DbContext instance), so its mutations are detected automatically.
            // Calling Update() explicitly keeps behaviour correct even if tracking state
            // differs in future refactors.
            _dbContext.TaskExecutions.Add(execution);
            _dbContext.DevelopmentTasks.Update(task);

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            // Unique partial index violation: a concurrent request already created an
            // active execution for this task. Clear the change-tracker so the caller
            // can inspect or retry cleanly.
            _dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<TaskExecution?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TaskExecutions
            .Include(e => e.DevelopmentTask)
                .ThenInclude(t => t.RepositoryWorkspace)
            .Include(e => e.CiChecks)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskExecution>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TaskExecutions
            .Include(e => e.DevelopmentTask)
                .ThenInclude(t => t.RepositoryWorkspace)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> HasActiveExecutionForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TaskExecutions
            .AsNoTracking()
            .AnyAsync(
                e => e.DevelopmentTaskId == taskId &&
                     (e.Status == TaskExecutionStatus.Pending || e.Status == TaskExecutionStatus.Running),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses a targeted <c>ExecuteUpdateAsync</c> (single UPDATE statement with a WHERE clause)
    /// so that only a <c>Pending</c> row is mutated.  If the execution has already been
    /// claimed (Running, Completed, Failed, or simply not found), zero rows are affected and
    /// <c>false</c> is returned — providing a safe idempotency guard for re-delivered
    /// Hangfire jobs.
    /// </remarks>
    public async Task<bool> ClaimAsRunningAsync(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId && e.Status == TaskExecutionStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.Status, TaskExecutionStatus.Running)
                    .SetProperty(e => e.StartedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Load the execution so we know the linked task ID.
        var execution = await _dbContext.TaskExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
            return;

        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.Status, TaskExecutionStatus.Completed)
                    .SetProperty(e => e.CompletedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        await _dbContext.DevelopmentTasks
            .Where(t => t.Id == execution.DevelopmentTaskId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Status, DevelopmentTaskStatus.Completed)
                    .SetProperty(t => t.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FailAsync(
        Guid executionId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var truncated = errorMessage.Length > 4000
            ? errorMessage[..4000]
            : errorMessage;

        var execution = await _dbContext.TaskExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
            return;

        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.Status, TaskExecutionStatus.Failed)
                    .SetProperty(e => e.CompletedAt, now)
                    .SetProperty(e => e.ErrorMessage, truncated),
                cancellationToken)
            .ConfigureAwait(false);

        await _dbContext.DevelopmentTasks
            .Where(t => t.Id == execution.DevelopmentTaskId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Status, DevelopmentTaskStatus.Failed)
                    .SetProperty(t => t.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateWorkspaceDetailsAsync(
        Guid executionId,
        string workspacePath,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.WorkspacePath, workspacePath)
                    .SetProperty(e => e.BranchName, branchName),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TrySetReviewDecisionAsync(
        Guid executionId,
        ExecutionReviewStatus expectedStatus,
        ExecutionReviewStatus newStatus,
        DateTime decidedAt,
        string? rejectionReason,
        CancellationToken cancellationToken = default)
    {
        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.Status == TaskExecutionStatus.Completed &&
                        e.ReviewStatus == expectedStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.ReviewStatus, newStatus)
                    .SetProperty(e => e.ReviewDecidedAt, decidedAt)
                    .SetProperty(e => e.ReviewRejectionReason, rejectionReason),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TrySetReviewDecisionWithFingerprintAsync(
        Guid executionId,
        ExecutionReviewStatus expectedStatus,
        ExecutionReviewStatus newStatus,
        DateTime decidedAt,
        string fingerprint,
        string? rejectionReason,
        CancellationToken cancellationToken = default)
    {
        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.Status == TaskExecutionStatus.Completed &&
                        e.ReviewStatus == expectedStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.ReviewStatus, newStatus)
                    .SetProperty(e => e.ReviewDecidedAt, decidedAt)
                    .SetProperty(e => e.ApprovedChangeFingerprint, fingerprint)
                    .SetProperty(e => e.ReviewRejectionReason, rejectionReason),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimNewCommitLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        string baseCommitSha,
        CancellationToken cancellationToken = default)
    {
        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.Status == TaskExecutionStatus.Completed &&
                        e.ReviewStatus == ExecutionReviewStatus.Approved &&
                        (e.CommitStatus == ExecutionCommitStatus.None || e.CommitStatus == ExecutionCommitStatus.Failed))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.CommitStatus, ExecutionCommitStatus.InProgress)
                    .SetProperty(e => e.CommitAttemptId, attemptId)
                    .SetProperty(e => e.CommitClaimedAt, claimedAt)
                    .SetProperty(e => e.BaseCommitSha, baseCommitSha),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryReclaimStaleCommitLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken = default)
    {
        var threshold = claimedAt - leaseTimeout;

        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.Status == TaskExecutionStatus.Completed &&
                        e.ReviewStatus == ExecutionReviewStatus.Approved &&
                        e.CommitStatus == ExecutionCommitStatus.InProgress &&
                        (e.CommitClaimedAt == null || e.CommitClaimedAt < threshold))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.CommitAttemptId, attemptId)
                    .SetProperty(e => e.CommitClaimedAt, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task SetCommitCompletedAsync(
        Guid executionId,
        Guid attemptId,
        string commitSha,
        DateTime committedAt,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        (e.CommitAttemptId == attemptId || e.CommitStatus == ExecutionCommitStatus.InProgress))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.CommitStatus, ExecutionCommitStatus.Committed)
                    .SetProperty(e => e.CommitSha, commitSha)
                    .SetProperty(e => e.CommittedAt, committedAt),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetCommitFailedAsync(
        Guid executionId,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId && e.CommitAttemptId == attemptId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.CommitStatus, ExecutionCommitStatus.Failed),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimNewPushLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        CancellationToken cancellationToken = default)
    {
        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.Status == TaskExecutionStatus.Completed &&
                        e.ReviewStatus == ExecutionReviewStatus.Approved &&
                        e.CommitStatus == ExecutionCommitStatus.Committed &&
                        (e.PushStatus == ExecutionPushStatus.None || e.PushStatus == ExecutionPushStatus.Failed))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PushStatus, ExecutionPushStatus.InProgress)
                    .SetProperty(e => e.PushAttemptId, attemptId)
                    .SetProperty(e => e.PushClaimedAt, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryReclaimStalePushLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken = default)
    {
        var threshold = claimedAt - leaseTimeout;

        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.Status == TaskExecutionStatus.Completed &&
                        e.ReviewStatus == ExecutionReviewStatus.Approved &&
                        e.CommitStatus == ExecutionCommitStatus.Committed &&
                        e.PushStatus == ExecutionPushStatus.InProgress &&
                        (e.PushClaimedAt == null || e.PushClaimedAt < threshold))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PushAttemptId, attemptId)
                    .SetProperty(e => e.PushClaimedAt, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task SetPushCompletedAsync(
        Guid executionId,
        Guid attemptId,
        string remoteBranchName,
        string remoteCommitSha,
        DateTime pushedAt,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        (e.PushAttemptId == attemptId || e.PushStatus == ExecutionPushStatus.InProgress))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PushStatus, ExecutionPushStatus.Pushed)
                    .SetProperty(e => e.RemoteBranchName, remoteBranchName)
                    .SetProperty(e => e.RemoteCommitSha, remoteCommitSha)
                    .SetProperty(e => e.PushedAt, pushedAt),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetPushFailedAsync(
        Guid executionId,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId && e.PushAttemptId == attemptId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PushStatus, ExecutionPushStatus.Failed),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimNewPullRequestLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        CancellationToken cancellationToken = default)
    {
        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.Status == TaskExecutionStatus.Completed &&
                        e.ReviewStatus == ExecutionReviewStatus.Approved &&
                        e.CommitStatus == ExecutionCommitStatus.Committed &&
                        e.PushStatus == ExecutionPushStatus.Pushed &&
                        (e.PullRequestStatus == ExecutionPullRequestStatus.None || e.PullRequestStatus == ExecutionPullRequestStatus.Failed))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PullRequestStatus, ExecutionPullRequestStatus.InProgress)
                    .SetProperty(e => e.PullRequestAttemptId, attemptId)
                    .SetProperty(e => e.PullRequestClaimedAt, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryReclaimStalePullRequestLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken = default)
    {
        var threshold = claimedAt - leaseTimeout;

        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.Status == TaskExecutionStatus.Completed &&
                        e.ReviewStatus == ExecutionReviewStatus.Approved &&
                        e.CommitStatus == ExecutionCommitStatus.Committed &&
                        e.PushStatus == ExecutionPushStatus.Pushed &&
                        e.PullRequestStatus == ExecutionPullRequestStatus.InProgress &&
                        (e.PullRequestClaimedAt == null || e.PullRequestClaimedAt < threshold))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PullRequestAttemptId, attemptId)
                    .SetProperty(e => e.PullRequestClaimedAt, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task SetPullRequestOpenedAsync(
        Guid executionId,
        Guid attemptId,
        int pullRequestNumber,
        string pullRequestUrl,
        string baseBranch,
        DateTime createdAt,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        (e.PullRequestAttemptId == attemptId || e.PullRequestStatus == ExecutionPullRequestStatus.InProgress))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PullRequestStatus, ExecutionPullRequestStatus.Open)
                    .SetProperty(e => e.PullRequestNumber, pullRequestNumber)
                    .SetProperty(e => e.PullRequestUrl, pullRequestUrl)
                    .SetProperty(e => e.PullRequestBaseBranch, baseBranch)
                    .SetProperty(e => e.PullRequestCreatedAt, createdAt),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetPullRequestFailedAsync(
        Guid executionId,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId && e.PullRequestAttemptId == attemptId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PullRequestStatus, ExecutionPullRequestStatus.Failed),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimPullRequestSyncLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        CancellationToken cancellationToken = default)
    {
        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.PullRequestStatus == ExecutionPullRequestStatus.Open &&
                        e.PullRequestSyncAttemptId == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PullRequestSyncAttemptId, attemptId)
                    .SetProperty(e => e.PullRequestSyncClaimedAt, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected > 0)
        {
            var tracked = _dbContext.ChangeTracker.Entries<TaskExecution>()
                .FirstOrDefault(e => e.Entity.Id == executionId);
            if (tracked != null)
            {
                tracked.Entity.PullRequestSyncAttemptId = attemptId;
                tracked.Entity.PullRequestSyncClaimedAt = claimedAt;
            }
        }

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryReclaimStalePullRequestSyncLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken = default)
    {
        var threshold = claimedAt - leaseTimeout;

        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.PullRequestStatus == ExecutionPullRequestStatus.Open &&
                        e.PullRequestSyncAttemptId != null &&
                        (e.PullRequestSyncClaimedAt == null || e.PullRequestSyncClaimedAt < threshold))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PullRequestSyncAttemptId, attemptId)
                    .SetProperty(e => e.PullRequestSyncClaimedAt, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected > 0)
        {
            var tracked = _dbContext.ChangeTracker.Entries<TaskExecution>()
                .FirstOrDefault(e => e.Entity.Id == executionId);
            if (tracked != null)
            {
                tracked.Entity.PullRequestSyncAttemptId = attemptId;
                tracked.Entity.PullRequestSyncClaimedAt = claimedAt;
            }
        }

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task ReleasePullRequestSyncLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime attemptAt,
        CancellationToken cancellationToken = default)
    {
        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId && e.PullRequestSyncAttemptId == attemptId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.PullRequestLastSyncAttemptAt, attemptAt)
                    .SetProperty(e => e.PullRequestSyncAttemptId, (Guid?)null)
                    .SetProperty(e => e.PullRequestSyncClaimedAt, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected > 0)
        {
            var tracked = _dbContext.ChangeTracker.Entries<TaskExecution>()
                .FirstOrDefault(e => e.Entity.Id == executionId);
            if (tracked != null)
            {
                tracked.Entity.PullRequestLastSyncAttemptAt = attemptAt;
                tracked.Entity.PullRequestSyncAttemptId = null;
                tracked.Entity.PullRequestSyncClaimedAt = null;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReplacePullRequestTrackingSnapshotAsync(
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
        var tracked = _dbContext.ChangeTracker.Entries<TaskExecution>()
            .FirstOrDefault(e => e.Entity.Id == executionId);

        TaskExecution? execution;
        if (tracked != null)
        {
            await tracked.ReloadAsync(cancellationToken).ConfigureAwait(false);
            execution = tracked.Entity;
            await _dbContext.Entry(execution)
                .Collection(e => e.CiChecks)
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            execution = await _dbContext.TaskExecutions
                .Include(e => e.CiChecks)
                .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (execution is null || execution.PullRequestSyncAttemptId != attemptId)
        {
            return false;
        }

        // Guard against stale sync overwriting a confirmed Merged state (Constraint #1)
        if ((execution.PullRequestRemoteState == ExecutionPullRequestRemoteState.Merged || execution.MergeStatus == ExecutionMergeStatus.Merged) &&
            remoteState != ExecutionPullRequestRemoteState.Merged)
        {
            execution.PullRequestLastSyncAttemptAt = syncedAt;
            execution.PullRequestSyncAttemptId = null;
            execution.PullRequestSyncClaimedAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        execution.PullRequestRemoteState = remoteState;
        execution.PullRequestIntegrityStatus = integrityStatus;
        execution.PullRequestClosedAt = closedAt;
        execution.PullRequestMergedAt = mergedAt;
        execution.PullRequestLastSyncedAt = syncedAt;
        execution.PullRequestLastSyncAttemptAt = syncedAt;
        execution.CiStatus = ciStatus;
        execution.CiLastSyncedAt = syncedAt;
        execution.PullRequestSyncAttemptId = null;
        execution.PullRequestSyncClaimedAt = null;

        _dbContext.ExecutionCiChecks.RemoveRange(execution.CiChecks);

        foreach (var c in checks)
        {
            c.Id = Guid.NewGuid();
            c.TaskExecutionId = executionId;
            c.CreatedAt = syncedAt;
            _dbContext.ExecutionCiChecks.Add(c);
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimMergeLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan syncLeaseTimeout,
        CancellationToken cancellationToken = default)
    {
        var syncThreshold = claimedAt - syncLeaseTimeout;

        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.Status == TaskExecutionStatus.Completed &&
                        e.ReviewStatus == ExecutionReviewStatus.Approved &&
                        e.CommitStatus == ExecutionCommitStatus.Committed &&
                        e.PushStatus == ExecutionPushStatus.Pushed &&
                        e.PullRequestStatus == ExecutionPullRequestStatus.Open &&
                        (e.MergeStatus == ExecutionMergeStatus.None || e.MergeStatus == ExecutionMergeStatus.Failed) &&
                        (e.PullRequestSyncAttemptId == null || e.PullRequestSyncClaimedAt == null || e.PullRequestSyncClaimedAt < syncThreshold))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.MergeStatus, ExecutionMergeStatus.InProgress)
                    .SetProperty(e => e.MergeAttemptId, attemptId)
                    .SetProperty(e => e.MergeClaimedAt, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected > 0)
        {
            var tracked = _dbContext.ChangeTracker.Entries<TaskExecution>()
                .FirstOrDefault(e => e.Entity.Id == executionId);
            if (tracked != null)
            {
                tracked.Entity.MergeStatus = ExecutionMergeStatus.InProgress;
                tracked.Entity.MergeAttemptId = attemptId;
                tracked.Entity.MergeClaimedAt = claimedAt;
            }
        }

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryReclaimStaleMergeLeaseAsync(
        Guid executionId,
        Guid attemptId,
        DateTime claimedAt,
        TimeSpan mergeLeaseTimeout,
        TimeSpan syncLeaseTimeout,
        CancellationToken cancellationToken = default)
    {
        var mergeThreshold = claimedAt - mergeLeaseTimeout;
        var syncThreshold = claimedAt - syncLeaseTimeout;

        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.MergeStatus == ExecutionMergeStatus.InProgress &&
                        (e.MergeClaimedAt == null || e.MergeClaimedAt < mergeThreshold) &&
                        (e.PullRequestSyncAttemptId == null || e.PullRequestSyncClaimedAt == null || e.PullRequestSyncClaimedAt < syncThreshold))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.MergeAttemptId, attemptId)
                    .SetProperty(e => e.MergeClaimedAt, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected > 0)
        {
            var tracked = _dbContext.ChangeTracker.Entries<TaskExecution>()
                .FirstOrDefault(e => e.Entity.Id == executionId);
            if (tracked != null)
            {
                tracked.Entity.MergeAttemptId = attemptId;
                tracked.Entity.MergeClaimedAt = claimedAt;
            }
        }

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task SetExecutionMergedAsync(
        Guid executionId,
        Guid attemptId,
        string mergeCommitSha,
        DateTime mergedAt,
        string mergeMethod = "merge",
        CancellationToken cancellationToken = default)
    {
        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.MergeAttemptId == attemptId &&
                        e.MergeStatus == ExecutionMergeStatus.InProgress)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.MergeStatus, ExecutionMergeStatus.Merged)
                    .SetProperty(e => e.MergeCommitSha, mergeCommitSha)
                    .SetProperty(e => e.MergedAt, mergedAt)
                    .SetProperty(e => e.MergeMethod, mergeMethod)
                    .SetProperty(e => e.PullRequestRemoteState, ExecutionPullRequestRemoteState.Merged)
                    .SetProperty(e => e.PullRequestMergedAt, mergedAt)
                    .SetProperty(e => e.MergeAttemptId, (Guid?)null)
                    .SetProperty(e => e.MergeClaimedAt, (DateTime?)null)
                    .SetProperty(e => e.PullRequestSyncAttemptId, (Guid?)null)
                    .SetProperty(e => e.PullRequestSyncClaimedAt, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected > 0)
        {
            var tracked = _dbContext.ChangeTracker.Entries<TaskExecution>()
                .FirstOrDefault(e => e.Entity.Id == executionId);
            if (tracked != null)
            {
                tracked.Entity.MergeStatus = ExecutionMergeStatus.Merged;
                tracked.Entity.MergeCommitSha = mergeCommitSha;
                tracked.Entity.MergedAt = mergedAt;
                tracked.Entity.MergeMethod = mergeMethod;
                tracked.Entity.PullRequestRemoteState = ExecutionPullRequestRemoteState.Merged;
                tracked.Entity.PullRequestMergedAt = mergedAt;
                tracked.Entity.MergeAttemptId = null;
                tracked.Entity.MergeClaimedAt = null;
                tracked.Entity.PullRequestSyncAttemptId = null;
                tracked.Entity.PullRequestSyncClaimedAt = null;
            }
        }
    }

    /// <inheritdoc />
    public async Task SetMergeFailedAsync(
        Guid executionId,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        var affected = await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId &&
                        e.MergeAttemptId == attemptId &&
                        e.MergeStatus == ExecutionMergeStatus.InProgress)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.MergeStatus, ExecutionMergeStatus.Failed)
                    .SetProperty(e => e.MergeAttemptId, (Guid?)null)
                    .SetProperty(e => e.MergeClaimedAt, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected > 0)
        {
            var tracked = _dbContext.ChangeTracker.Entries<TaskExecution>()
                .FirstOrDefault(e => e.Entity.Id == executionId);
            if (tracked != null)
            {
                tracked.Entity.MergeStatus = ExecutionMergeStatus.Failed;
                tracked.Entity.MergeAttemptId = null;
                tracked.Entity.MergeClaimedAt = null;
            }
        }
    }
}
