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
}
