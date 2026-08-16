using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.Executions;

public sealed class EfExecutionActivityRepository : IExecutionActivityRepository
{
    private readonly DevPilotDbContext _dbContext;

    public EfExecutionActivityRepository(DevPilotDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ExecutionActivities
            .AsNoTracking()
            .Where(a => a.ExecutionId == executionId)
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
