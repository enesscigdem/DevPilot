using DevPilot.Application.Automation;
using DevPilot.Application.Tasks.Batch;
using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Goals;

public sealed class GoalTaskInput
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string>? Areas { get; set; }

    public List<string>? DependsOn { get; set; }

    public string? Size { get; set; }

    /// <summary>True when the areas came from a code search rather than from the planner; such guesses never hold a task back.</summary>
    public bool AreasGuessed { get; set; }
}

public sealed record StartGoalCommand(
    Guid RepositoryWorkspaceId,
    string Text,
    string? PlanSource,
    IReadOnlyList<GoalTaskInput> Tasks,
    long EstimatedInputTokens,
    long EstimatedOutputTokens,
    decimal? EstimatedUsd);

public sealed class StartGoalResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public bool NotFound { get; set; }

    public Guid? GoalId { get; set; }
}

public interface IStartGoalCommandHandler
{
    Task<StartGoalResult> HandleAsync(StartGoalCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns an approved plan into a goal: creates its tasks and records the order they must keep. The server
/// recomputes the order from the tasks it receives, so the screen cannot ask for an order that causes clashes.
/// </summary>
public sealed class StartGoalCommandHandler : IStartGoalCommandHandler
{
    private readonly ICreateTaskCommandHandler _createTask;
    private readonly IGoalStore _store;
    private readonly IAutomationPolicyStore _policies;
    private readonly ILogger<StartGoalCommandHandler> _logger;

    public StartGoalCommandHandler(
        ICreateTaskCommandHandler createTask,
        IGoalStore store,
        IAutomationPolicyStore policies,
        ILogger<StartGoalCommandHandler> logger)
    {
        _policies = policies;
        _createTask = createTask;
        _store = store;
        _logger = logger;
    }

    public async Task<StartGoalResult> HandleAsync(StartGoalCommand command, CancellationToken cancellationToken = default)
    {
        var invalid = Validate(command);
        if (invalid is not null)
        {
            return new StartGoalResult { ErrorMessage = invalid };
        }

        var plan = command.Tasks
            .Select(t => new GoalTaskPlan(
                t.Key.Trim(),
                t.Title.Trim(),
                (t.Description ?? string.Empty).Trim(),
                (t.Areas ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct().ToList(),
                (t.DependsOn ?? new List<string>()).Select(d => d.Trim()).Distinct().ToList(),
                GoalCostEstimator.NormalizeSize(t.Size),
                t.AreasGuessed))
            .ToList();

        var mode = (await _policies.GetAsync(command.RepositoryWorkspaceId, cancellationToken).ConfigureAwait(false))?.ConflictMode
            ?? ConflictMode.Balanced;
        var (waves, conflicts) = GoalWavePlanner.Plan(plan, mode);
        var waveOf = waves.SelectMany(w => w.TaskKeys.Select(k => (k, w.Number))).ToDictionary(x => x.k, x => x.Number);

        var now = DateTime.UtcNow;
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = command.RepositoryWorkspaceId,
            Title = TitleOf(command.Text, plan),
            Text = command.Text.Trim(),
            Status = GoalStatus.Active,
            PlanSource = command.PlanSource == "parser" ? "parser" : "ai",
            EstimatedInputTokens = Math.Max(0, command.EstimatedInputTokens),
            EstimatedOutputTokens = Math.Max(0, command.EstimatedOutputTokens),
            EstimatedUsd = command.EstimatedUsd is >= 0 ? command.EstimatedUsd : null,
            CreatedAt = now,
            UpdatedAt = now
        };

        for (var i = 0; i < plan.Count; i++)
        {
            var task = plan[i];
            var created = await _createTask
                .HandleAsync(
                    new CreateTaskCommand(new CreateTaskDto
                    {
                        RepositoryWorkspaceId = command.RepositoryWorkspaceId,
                        Title = task.Title,
                        Description = task.Description,
                        Priority = DevelopmentTaskPriority.Medium
                    }),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!created.Success || created.Task is null)
            {
                return new StartGoalResult
                {
                    NotFound = created.ErrorMessage == "Repository workspace not found.",
                    ErrorMessage = created.ErrorMessage ?? "A task could not be created."
                };
            }

            // A task waits for what it depends on and for every earlier task expected to change the same code.
            var blockers = task.DependsOn
                .Concat(conflicts.Where(c => c.SecondKey == task.Key).Select(c => c.FirstKey))
                .Distinct()
                .ToList();

            goal.Tasks.Add(new GoalTask
            {
                Id = Guid.NewGuid(),
                GoalId = goal.Id,
                DevelopmentTaskId = created.Task.Id,
                Key = task.Key,
                Position = i,
                Wave = waveOf[task.Key],
                Size = task.Size,
                Areas = string.Join('\n', task.Areas),
                DependsOn = string.Join(',', task.DependsOn),
                BlockedBy = string.Join(',', blockers)
            });
        }

        await _store.AddAsync(goal, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Started goal {GoalId} with {Count} task(s) in workspace {WorkspaceId}.", goal.Id, plan.Count, goal.RepositoryWorkspaceId);
        return new StartGoalResult { Success = true, GoalId = goal.Id };
    }

    private static string? Validate(StartGoalCommand command)
    {
        if (command.RepositoryWorkspaceId == Guid.Empty)
        {
            return "Repository workspace is required.";
        }

        if (string.IsNullOrWhiteSpace(command.Text) || command.Text.Length > GoalPlanner.MaxGoalLength)
        {
            return "The goal description is missing or too long.";
        }

        if (command.Tasks.Count == 0 || command.Tasks.Count > GoalPlanner.MaxTasks)
        {
            return $"A goal needs between 1 and {GoalPlanner.MaxTasks} tasks.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in command.Tasks)
        {
            var key = (task.Key ?? string.Empty).Trim();
            if (key.Length == 0 || key.Length > 20 || key.Contains(',') || !seen.Add(key))
            {
                return "Every task needs its own short key.";
            }

            if (string.IsNullOrWhiteSpace(task.Title) || task.Title.Trim().Length > TaskBatchParser.MaxTitleLength)
            {
                return $"Every task needs a title of at most {TaskBatchParser.MaxTitleLength} characters.";
            }

            if ((task.Description ?? string.Empty).Length > TaskBatchParser.MaxDescriptionLength)
            {
                return "A task description is too long.";
            }
        }

        return ValidateDependencies(command);
    }

    /// <summary>A dependency must name another task of the same goal, and the dependencies must not wait on each other in a circle.</summary>
    private static string? ValidateDependencies(StartGoalCommand command)
    {
        var dependsOn = command.Tasks.ToDictionary(
            t => t.Key.Trim(),
            t => (t.DependsOn ?? new List<string>()).Select(d => d.Trim()).Where(d => d.Length > 0).Distinct().ToList(),
            StringComparer.Ordinal);

        foreach (var (key, deps) in dependsOn)
        {
            foreach (var dep in deps)
            {
                if (dep == key)
                {
                    return $"Task '{key}' cannot depend on itself.";
                }

                if (!dependsOn.ContainsKey(dep))
                {
                    return $"Task '{key}' depends on '{dep}', which is not a task of this goal.";
                }
            }
        }

        // Repeatedly remove tasks whose dependencies are all gone; anything left waits on a circle.
        var remaining = dependsOn.ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
            if (ready.Count == 0)
            {
                return $"Tasks {string.Join(", ", remaining.Keys.Select(k => $"'{k}'"))} wait on each other in a circle.";
            }

            foreach (var key in ready)
            {
                remaining.Remove(key);
            }

            foreach (var deps in remaining.Values)
            {
                deps.ExceptWith(ready);
            }
        }

        return null;
    }

    private static string TitleOf(string text, IReadOnlyList<GoalTaskPlan> tasks)
    {
        var firstLine = text.Trim().Split('\n')[0].Trim();
        var title = tasks.Count == 1 ? tasks[0].Title : firstLine;
        if (title.Length == 0)
        {
            title = tasks[0].Title;
        }

        return title.Length <= 100 ? title : title[..97] + "...";
    }
}
