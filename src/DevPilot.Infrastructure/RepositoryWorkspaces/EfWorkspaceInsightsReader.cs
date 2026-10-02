using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.RepositoryWorkspaces;

/// <summary>
/// Loads the newest terminal executions of a repository with their verification snapshots. Uses the persisted
/// snapshot when present; older executions without one are summarized from their recorded activities.
/// </summary>
public sealed class EfWorkspaceInsightsReader : IWorkspaceInsightsReader
{
    private readonly DevPilotDbContext _db;
    private readonly AiPricingOptions? _pricing;

    public EfWorkspaceInsightsReader(DevPilotDbContext db, AiPricingOptions? pricing = null)
    {
        _db = db;
        _pricing = pricing;
    }

    public async Task<IReadOnlyList<InsightExecutionInput>?> ReadAsync(
        Guid workspaceId,
        int maxExecutions,
        CancellationToken cancellationToken = default)
    {
        var exists = await _db.RepositoryWorkspaces.AsNoTracking()
            .AnyAsync(w => w.Id == workspaceId, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return null;
        }

        var executions = await _db.TaskExecutions.AsNoTracking()
            .Include(e => e.DevelopmentTask)
            .Where(e => e.DevelopmentTask.RepositoryWorkspaceId == workspaceId &&
                        (e.Status == TaskExecutionStatus.Completed ||
                         e.Status == TaskExecutionStatus.Failed ||
                         e.Status == TaskExecutionStatus.Cancelled))
            .OrderByDescending(e => e.CreatedAt)
            .Take(maxExecutions)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var snapshots = new Dictionary<Guid, ExecutionVerificationSnapshot>();
        var needLive = new List<TaskExecution>();
        foreach (var execution in executions)
        {
            var persisted = ExecutionVerdictBuilder.TryDeserialize(execution.VerificationSnapshotJson);
            if (persisted != null)
            {
                snapshots[execution.Id] = persisted;
            }
            else
            {
                needLive.Add(execution);
            }
        }

        if (needLive.Count > 0)
        {
            var ids = needLive.Select(e => e.Id).ToList();
            var activities = await _db.ExecutionActivities.AsNoTracking()
                .Where(a => ids.Contains(a.ExecutionId))
                .OrderBy(a => a.CreatedAt)
                .ThenBy(a => a.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var byExecution = activities
                .GroupBy(a => a.ExecutionId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ExecutionActivity>)g.ToList());

            foreach (var execution in needLive)
            {
                var list = byExecution.TryGetValue(execution.Id, out var found)
                    ? found
                    : Array.Empty<ExecutionActivity>();
                var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, list);
                snapshots[execution.Id] = ExecutionVerdictBuilder.BuildSnapshot(execution, list, outcome, _pricing);
            }
        }

        return executions
            .Select(e => new InsightExecutionInput(
                e.Id,
                e.DevelopmentTaskId,
                e.DevelopmentTask.Title,
                e.Status,
                e.CreatedAt,
                e.StartedAt,
                e.CompletedAt,
                snapshots[e.Id]))
            .ToList();
    }
}
