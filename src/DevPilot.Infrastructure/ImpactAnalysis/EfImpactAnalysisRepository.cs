using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using TaskImpactAnalysisEntity = DevPilot.Domain.Entities.TaskImpactAnalysis;

namespace DevPilot.Infrastructure.ImpactAnalysis;

public sealed class EfImpactAnalysisRepository : IImpactAnalysisRepository
{
    private readonly DevPilotDbContext _dbContext;

    public EfImpactAnalysisRepository(DevPilotDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<TaskImpactAnalysisEntity?> GetLatestByTaskIdAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TaskImpactAnalyses
            .AsNoTracking()
            .Include(a => a.DevelopmentTask)
            .Where(a => a.DevelopmentTaskId == taskId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(
        TaskImpactAnalysisEntity analysis,
        CancellationToken cancellationToken = default)
    {
        _dbContext.TaskImpactAnalyses.Add(analysis);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
