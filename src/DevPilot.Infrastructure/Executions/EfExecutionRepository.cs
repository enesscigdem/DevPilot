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
}
