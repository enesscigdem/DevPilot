using DevPilot.Application.Executions.Services;
using DevPilot.Application.Goals;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.Goals;

public sealed class EfGoalHistoryReader : IGoalHistoryReader
{
    private const int RecentExecutions = 30;

    private readonly DevPilotDbContext _db;

    public EfGoalHistoryReader(DevPilotDbContext db)
    {
        _db = db;
    }

    public async Task<GoalUsageHistory?> GetRecentUsageAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default)
    {
        var snapshots = await _db.TaskExecutions
            .AsNoTracking()
            .Where(e => e.DevelopmentTask.RepositoryWorkspaceId == repositoryWorkspaceId
                && e.Status == TaskExecutionStatus.Completed
                && e.VerificationSnapshotJson != null)
            .OrderByDescending(e => e.CompletedAt)
            .Take(RecentExecutions)
            .Select(e => e.VerificationSnapshotJson)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var usage = snapshots
            .Select(json => ExecutionVerdictBuilder.TryDeserialize(json)?.Usage)
            .Where(u => u is not null && u.InputTokens > 0 && u.OutputTokens > 0)
            .ToList();

        return usage.Count == 0
            ? null
            : new GoalUsageHistory(
                (long)usage.Average(u => u!.InputTokens),
                (long)usage.Average(u => u!.OutputTokens),
                usage.Count);
    }
}
