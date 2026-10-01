using DevPilot.Application.Executions.Ports;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.Executions;

public sealed class EfExecutionVerificationSnapshotStore : IExecutionVerificationSnapshotStore
{
    private readonly DevPilotDbContext _dbContext;

    public EfExecutionVerificationSnapshotStore(DevPilotDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task SaveAsync(
        Guid executionId,
        string outcome,
        string snapshotJson,
        CancellationToken cancellationToken = default)
    {
        // Targeted single-statement update: never touches lease, review, delivery or merge columns.
        await _dbContext.TaskExecutions
            .Where(e => e.Id == executionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.VerificationOutcome, outcome)
                    .SetProperty(e => e.VerificationSnapshotJson, snapshotJson),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
