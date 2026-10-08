using DevPilot.Application.Goals;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.Goals;

public sealed class EfGoalStore : IGoalStore
{
    private const int MaxListed = 30;
    private const string AutomationHeldPrefix = DevPilot.Application.Automation.AutomationOrchestrator.LeftForPersonPrefix;

    private readonly DevPilotDbContext _db;

    public EfGoalStore(DevPilotDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Goal goal, CancellationToken cancellationToken = default)
    {
        _db.Goals.Add(goal);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GoalState?> GetAsync(Guid goalId, Guid? repositoryWorkspaceId = null, CancellationToken cancellationToken = default)
    {
        var query = _db.Goals.AsNoTracking().Where(g => g.Id == goalId);
        if (repositoryWorkspaceId.HasValue)
        {
            query = query.Where(g => g.RepositoryWorkspaceId == repositoryWorkspaceId.Value);
        }

        return (await BuildAsync(query, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<GoalState>> ListAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
        await BuildAsync(
            _db.Goals.AsNoTracking()
                .Where(g => g.RepositoryWorkspaceId == repositoryWorkspaceId)
                .OrderByDescending(g => g.CreatedAt)
                .Take(MaxListed),
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken = default) =>
        await _db.Goals.AsNoTracking()
            .Where(g => g.Status == GoalStatus.Active)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task MarkAnalysisRequestedAsync(Guid goalTaskId, CancellationToken cancellationToken = default)
    {
        var task = await _db.GoalTasks.FirstOrDefaultAsync(t => t.Id == goalTaskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return;
        }

        task.AnalysisAttempts++;
        task.AnalysisRequestedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetNoteAsync(Guid goalTaskId, string? note, CancellationToken cancellationToken = default)
    {
        var task = await _db.GoalTasks.FirstOrDefaultAsync(t => t.Id == goalTaskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return;
        }

        task.Note = note is null ? null : (note.Length > 1000 ? note[..1000] : note);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetStatusAsync(Guid goalId, GoalStatus status, CancellationToken cancellationToken = default)
    {
        var goal = await _db.Goals.FirstOrDefaultAsync(g => g.Id == goalId, cancellationToken).ConfigureAwait(false);
        if (goal is null || goal.Status == status)
        {
            return;
        }

        var now = DateTime.UtcNow;
        goal.Status = status;
        goal.UpdatedAt = now;
        goal.CompletedAt = status == GoalStatus.Completed ? now : goal.CompletedAt;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GoalState>> BuildAsync(IQueryable<Goal> goals, CancellationToken ct)
    {
        var loaded = await goals
            .Include(g => g.Tasks).ThenInclude(t => t.DevelopmentTask)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (loaded.Count == 0)
        {
            return Array.Empty<GoalState>();
        }

        var taskIds = loaded.SelectMany(g => g.Tasks).Select(t => t.DevelopmentTaskId).Distinct().ToList();

        // Only the columns the screen and the orchestrator need: executions and analyses carry large payloads.
        var executions = (await _db.TaskExecutions.AsNoTracking()
                .Where(e => taskIds.Contains(e.DevelopmentTaskId))
                .Select(e => new
                {
                    e.DevelopmentTaskId,
                    e.Id,
                    e.Status,
                    e.ReviewStatus,
                    e.CommitStatus,
                    e.PushStatus,
                    e.PullRequestStatus,
                    e.PullRequestRemoteState,
                    e.MergeStatus,
                    e.PullRequestNumber,
                    e.PullRequestUrl,
                    e.ErrorMessage,
                    e.CreatedAt
                })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(x => x.DevelopmentTaskId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.CreatedAt).First());

        // Runs that automation looked at and handed back to a person: only those really wait for review.
        var latestIds = executions.Values.Select(e => e.Id).ToList();
        var held = (await _db.ExecutionActivities.AsNoTracking()
                .Where(a => latestIds.Contains(a.ExecutionId) && a.Message.StartsWith(AutomationHeldPrefix))
                .Select(a => a.ExecutionId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet();

        var states = executions.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var e = kv.Value;
                return new GoalExecutionState(
                    e.Id, e.Status, e.ReviewStatus, e.CommitStatus, e.PushStatus, e.PullRequestStatus,
                    e.PullRequestRemoteState, e.MergeStatus, e.PullRequestNumber, e.PullRequestUrl, e.ErrorMessage, e.CreatedAt,
                    held.Contains(e.Id));
            });

        var analyses = (await _db.TaskImpactAnalyses.AsNoTracking()
                .Where(a => taskIds.Contains(a.DevelopmentTaskId) && a.Status == ImpactAnalysisStatus.Completed)
                .Select(a => new { a.DevelopmentTaskId, a.CreatedAt, a.StructuredResult })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(x => x.DevelopmentTaskId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var files = g.OrderByDescending(x => x.CreatedAt).First().StructuredResult?.ImpactedFiles
                        .Where(f => !string.IsNullOrWhiteSpace(f.FilePath))
                        .ToList() ?? new List<ImpactedFile>();
                    // Files the task creates, deletes or restructures: git cannot merge those with another task's change.
                    var structural = files
                        .Where(f => f.ChangeType is ImpactFileChangeType.Add or ImpactFileChangeType.Delete or ImpactFileChangeType.Refactor)
                        .Select(f => f.FilePath)
                        .ToList();
                    return (Files: (IReadOnlyList<string>)files.Select(f => f.FilePath).ToList(), Structural: (IReadOnlyList<string>)structural);
                });

        return loaded
            .Select(g => new GoalState(
                g.Id,
                g.RepositoryWorkspaceId,
                g.Title,
                g.Text,
                g.Status,
                g.PlanSource,
                g.EstimatedInputTokens,
                g.EstimatedOutputTokens,
                g.EstimatedUsd,
                g.CreatedAt,
                g.CompletedAt,
                g.Tasks.OrderBy(t => t.Position).Select(t => new GoalTaskState(
                    t.Id,
                    t.Key,
                    t.Position,
                    t.Wave,
                    t.Size,
                    Split(t.Areas, '\n'),
                    Split(t.DependsOn, ','),
                    Split(t.BlockedBy, ','),
                    t.AnalysisAttempts,
                    t.AnalysisRequestedAt,
                    t.Note,
                    t.DevelopmentTaskId,
                    t.DevelopmentTask.Title,
                    t.DevelopmentTask.Description,
                    t.DevelopmentTask.Status,
                    states.GetValueOrDefault(t.DevelopmentTaskId),
                    analyses.TryGetValue(t.DevelopmentTaskId, out var analysis) ? analysis.Files : Array.Empty<string>(),
                    analyses.TryGetValue(t.DevelopmentTaskId, out var found) ? found.Structural : Array.Empty<string>())).ToList()))
            .ToList();
    }

    private static IReadOnlyList<string> Split(string value, char separator) =>
        value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
