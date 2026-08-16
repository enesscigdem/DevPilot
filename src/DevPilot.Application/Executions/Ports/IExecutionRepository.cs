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
}
