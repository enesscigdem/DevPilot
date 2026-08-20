using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

    public async Task UpdateAsync(
        TaskImpactAnalysisEntity analysis,
        CancellationToken cancellationToken = default)
    {
        _dbContext.TaskImpactAnalyses.Update(analysis);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> StartAnalysisAtomicAsync(
        TaskImpactAnalysisEntity analysis,
        DevelopmentTask task,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _dbContext.TaskImpactAnalyses.Add(analysis);
            _dbContext.DevelopmentTasks.Update(task);

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex)
            when ((ex.GetBaseException() is PostgresException pg &&
                   pg.SqlState == "23505" &&
                   (pg.ConstraintName == "IX_TaskImpactAnalyses_ActivePerTask" ||
                    (pg.MessageText != null && pg.MessageText.Contains("IX_TaskImpactAnalyses_ActivePerTask", StringComparison.OrdinalIgnoreCase))))
                  || ex.InnerException?.Message.Contains("IX_TaskImpactAnalyses_ActivePerTask", StringComparison.OrdinalIgnoreCase) == true)
        {
            _dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<bool> HasActiveAnalysisForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TaskImpactAnalyses
            .AsNoTracking()
            .AnyAsync(
                a => a.DevelopmentTaskId == taskId && a.Status == ImpactAnalysisStatus.InProgress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> ReconcileStaleAnalysesAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        const string staleReason = "Impact analysis did not complete before the execution timeout.";

        var staleAnalyses = await _dbContext.TaskImpactAnalyses
            .Where(a => a.Status == ImpactAnalysisStatus.InProgress && a.CreatedAt < cutoffUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (staleAnalyses.Count == 0)
            return 0;

        int reconciledCount = 0;
        foreach (var analysis in staleAnalyses)
        {
            var affected = await _dbContext.TaskImpactAnalyses
                .Where(a => a.Id == analysis.Id && a.Status == ImpactAnalysisStatus.InProgress)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(a => a.Status, ImpactAnalysisStatus.Failed)
                        .SetProperty(a => a.CompletedAt, now)
                        .SetProperty(a => a.ErrorMessage, staleReason),
                    cancellationToken)
                .ConfigureAwait(false);

            if (affected > 0)
            {
                await _dbContext.DevelopmentTasks
                    .Where(t => t.Id == analysis.DevelopmentTaskId && t.Status == DevelopmentTaskStatus.Analyzing)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(t => t.Status, DevelopmentTaskStatus.Failed)
                            .SetProperty(t => t.UpdatedAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);

                reconciledCount++;
            }
        }

        return reconciledCount;
    }
}

