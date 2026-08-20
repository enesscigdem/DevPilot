using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.Executions;

public sealed class EfExecutionListReader : IExecutionListReader
{
    private readonly DevPilotDbContext _db;

    public EfExecutionListReader(DevPilotDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ExecutionListItemDto>> ReadExecutionsListAsync(
        Guid? workspaceId,
        CancellationToken cancellationToken = default)
    {
        var query = _db.TaskExecutions
            .AsNoTracking()
            .Include(e => e.DevelopmentTask)
                .ThenInclude(t => t.RepositoryWorkspace)
            .AsQueryable();

        if (workspaceId.HasValue)
        {
            query = query.Where(e => e.DevelopmentTask.RepositoryWorkspaceId == workspaceId.Value);
        }

        var executions = await query
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (executions.Count == 0)
        {
            return Array.Empty<ExecutionListItemDto>();
        }

        var taskIds = executions.Select(e => e.DevelopmentTaskId).Distinct().ToList();
        var executionIds = executions.Select(e => e.Id).ToList();

        var impactAnalyses = await _db.TaskImpactAnalyses
            .AsNoTracking()
            .Where(a => taskIds.Contains(a.DevelopmentTaskId))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var latestAnalysisByTaskId = impactAnalyses
            .GroupBy(a => a.DevelopmentTaskId)
            .ToDictionary(g => g.Key, g => g.First());

        var activities = await _db.ExecutionActivities
            .AsNoTracking()
            .Where(a => executionIds.Contains(a.ExecutionId))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var activitiesByExecutionId = activities
            .GroupBy(a => a.ExecutionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ExecutionListItemDto>(executions.Count);
        foreach (var e in executions)
        {
            var task = e.DevelopmentTask;
            latestAnalysisByTaskId.TryGetValue(e.DevelopmentTaskId, out var analysis);
            activitiesByExecutionId.TryGetValue(e.Id, out var execActivities);
            execActivities ??= new List<ExecutionActivity>();

            var stages = ExecutionStageEvaluator.EvaluateStages(e, task, analysis, execActivities);
            var progress = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

            result.Add(new ExecutionListItemDto
            {
                Id = e.Id,
                DevelopmentTaskId = e.DevelopmentTaskId,
                TaskTitle = task?.Title ?? string.Empty,
                RepositoryName = task?.RepositoryWorkspace?.Repository ?? string.Empty,
                Status = e.Status,
                CreatedAt = e.CreatedAt,
                StartedAt = e.StartedAt,
                CompletedAt = e.CompletedAt,
                ReviewStatus = e.ReviewStatus.ToString(),
                CommitStatus = e.CommitStatus.ToString(),
                PushStatus = e.PushStatus.ToString(),
                PullRequestStatus = e.PullRequestStatus.ToString(),
                PullRequestRemoteState = e.PullRequestRemoteState.ToString(),
                CiStatus = e.CiStatus.ToString(),
                MergeStatus = e.MergeStatus.ToString(),
                ErrorMessage = e.ErrorMessage,
                ProgressPercentage = progress,
                Model = e.Model,
                Stages = stages
            });
        }

        return result;
    }
}
