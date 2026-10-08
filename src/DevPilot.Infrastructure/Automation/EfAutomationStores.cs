using DevPilot.Application.Automation;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.Automation;

public sealed class EfAutomationPolicyStore : IAutomationPolicyStore
{
    private readonly DevPilotDbContext _db;

    public EfAutomationPolicyStore(DevPilotDbContext db)
    {
        _db = db;
    }

    public Task<AutomationPolicy?> GetAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
        _db.AutomationPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.RepositoryWorkspaceId == repositoryWorkspaceId, cancellationToken);

    public async Task<AutomationPolicy?> SaveAsync(AutomationPolicy policy, CancellationToken cancellationToken = default)
    {
        if (!await _db.RepositoryWorkspaces.AnyAsync(w => w.Id == policy.RepositoryWorkspaceId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var existing = await _db.AutomationPolicies
            .FirstOrDefaultAsync(p => p.RepositoryWorkspaceId == policy.RepositoryWorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new AutomationPolicy { Id = Guid.NewGuid(), RepositoryWorkspaceId = policy.RepositoryWorkspaceId };
            _db.AutomationPolicies.Add(existing);
        }

        // Switching automation on starts a new window: only tasks created from now on are handled automatically.
        var wasActive = existing.Level != AutomationLevel.Manual;
        var isActive = policy.Level != AutomationLevel.Manual;
        existing.ActiveSince = !isActive ? null : wasActive && existing.ActiveSince.HasValue ? existing.ActiveSince : now;

        existing.Level = policy.Level;
        existing.Paused = policy.Paused;
        existing.MaxFilesChanged = policy.MaxFilesChanged;
        existing.MaxLinesChanged = policy.MaxLinesChanged;
        existing.MaxParallelExecutions = policy.MaxParallelExecutions;
        existing.ProtectedPaths = policy.ProtectedPaths;
        existing.RequireGreenCiForMerge = policy.RequireGreenCiForMerge;
        existing.ConflictMode = policy.ConflictMode;
        existing.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return existing;
    }

    public async Task<IReadOnlyList<AutomationPolicy>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        await _db.AutomationPolicies
            .AsNoTracking()
            .Where(p => p.Level != AutomationLevel.Manual && !p.Paused && p.ActiveSince != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

public sealed class EfAutomationWorkReader : IAutomationWorkReader
{
    private readonly DevPilotDbContext _db;

    public EfAutomationWorkReader(DevPilotDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<DevelopmentTask>> GetTasksReadyToStartAsync(
        Guid repositoryWorkspaceId,
        DateTime since,
        CancellationToken cancellationToken = default) =>
        await _db.DevelopmentTasks
            .AsNoTracking()
            .Where(t => t.RepositoryWorkspaceId == repositoryWorkspaceId && t.CreatedAt >= since)
            // A goal decides when its own tasks start (see GoalOrchestrator), so they are never started here.
            .Where(t => !_db.GoalTasks.Any(g => g.DevelopmentTaskId == t.Id))
            .Where(t => t.Status == DevelopmentTaskStatus.AwaitingApproval
                || (t.Status == DevelopmentTaskStatus.Approved
                    && !_db.TaskExecutions.Any(e => e.DevelopmentTaskId == t.Id)))
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<int> CountActiveExecutionsAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
        _db.TaskExecutions.CountAsync(
            e => e.DevelopmentTask.RepositoryWorkspaceId == repositoryWorkspaceId
                && (e.Status == TaskExecutionStatus.Pending || e.Status == TaskExecutionStatus.Running),
            cancellationToken);

    public Task<int> CountExecutionsForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        _db.TaskExecutions.CountAsync(e => e.DevelopmentTaskId == taskId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetRetryableFailedExecutionIdsAsync(
        Guid repositoryWorkspaceId,
        DateTime since,
        CancellationToken cancellationToken = default) =>
        await _db.TaskExecutions
            .AsNoTracking()
            .Where(e => e.DevelopmentTask.RepositoryWorkspaceId == repositoryWorkspaceId
                && e.CreatedAt >= since
                && e.Status == TaskExecutionStatus.Failed
                && e.DevelopmentTask.Status == DevelopmentTaskStatus.Failed
                // Only the newest run of the task, and only while it still has an automatic retry left.
                && !_db.TaskExecutions.Any(newer => newer.DevelopmentTaskId == e.DevelopmentTaskId
                    && newer.CreatedAt > e.CreatedAt)
                && _db.TaskExecutions.Count(any => any.DevelopmentTaskId == e.DevelopmentTaskId) < AutomationRetryRules.MaxExecutionsPerTask)
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Guid>> GetDeliverableExecutionIdsAsync(
        Guid repositoryWorkspaceId,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        // A commit or push that claimed its lease and never released it (the process died in between) is picked up
        // again once the lease is stale; the step's own crash recovery then finishes the bookkeeping.
        var staleBefore = DateTime.UtcNow - AutomationDeliveryRules.StaleLeaseAfter;

        return await _db.TaskExecutions
            .AsNoTracking()
            .Where(e => e.DevelopmentTask.RepositoryWorkspaceId == repositoryWorkspaceId
                && e.CreatedAt >= since
                && e.Status == TaskExecutionStatus.Completed
                && e.ReviewStatus != ExecutionReviewStatus.Rejected
                && (e.PullRequestStatus == ExecutionPullRequestStatus.None
                    || (e.PullRequestStatus == ExecutionPullRequestStatus.InProgress && e.PullRequestClaimedAt < staleBefore))
                && e.CommitStatus != ExecutionCommitStatus.Failed
                && e.PushStatus != ExecutionPushStatus.Failed
                && (e.CommitStatus != ExecutionCommitStatus.InProgress
                    || e.CommitClaimedAt < staleBefore)
                && (e.PushStatus != ExecutionPushStatus.InProgress
                    || e.PushClaimedAt < staleBefore))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> GetOpenPullRequestExecutionIdsAsync(
        Guid repositoryWorkspaceId,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        // A merge that claimed its lease and never finished (a refusal the old client read as a transport problem, or a
        // process that died) is retried once the lease is stale; the merge command first checks what the host says.
        var staleBefore = DateTime.UtcNow - AutomationDeliveryRules.StaleLeaseAfter;

        return await _db.TaskExecutions
            .AsNoTracking()
            .Where(e => e.DevelopmentTask.RepositoryWorkspaceId == repositoryWorkspaceId
                && e.CreatedAt >= since
                && e.PullRequestStatus == ExecutionPullRequestStatus.Open
                && (e.MergeStatus == ExecutionMergeStatus.None
                    || (e.MergeStatus == ExecutionMergeStatus.InProgress && e.MergeClaimedAt < staleBefore)))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
