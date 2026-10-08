using DevPilot.Application.Automation;
using DevPilot.Application.Goals;
using DevPilot.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

public sealed record PlanGoalRequest(string? Text);

public sealed class ArrangeGoalRequest
{
    public List<GoalTaskInput>? Tasks { get; set; }

    public decimal? InputPerMillionUsd { get; set; }

    public decimal? OutputPerMillionUsd { get; set; }
}

public sealed class StartGoalRequest
{
    public string? Text { get; set; }

    public string? PlanSource { get; set; }

    public List<GoalTaskInput>? Tasks { get; set; }

    public long EstimatedInputTokens { get; set; }

    public long EstimatedOutputTokens { get; set; }

    public decimal? EstimatedUsd { get; set; }
}

[ApiController]
[Route("api/repositoryworkspaces/{workspaceId:guid}/goals")]
[Produces("application/json")]
public class GoalsController : ControllerBase
{
    private readonly IGoalPlanner _planner;
    private readonly IStartGoalCommandHandler _start;
    private readonly IGoalStore _store;
    private readonly IAutomationPolicyStore _policies;

    public GoalsController(IGoalPlanner planner, IStartGoalCommandHandler start, IGoalStore store, IAutomationPolicyStore policies)
    {
        _policies = policies;
        _planner = planner;
        _start = start;
        _store = store;
    }

    /// <summary>Plans a goal: tasks, waves, conflicts and a cost estimate. Creates nothing.</summary>
    [HttpPost("plan", Name = nameof(PlanGoal))]
    public async Task<IActionResult> PlanGoal(
        [FromRoute] Guid workspaceId,
        [FromBody] PlanGoalRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _planner.PlanAsync(workspaceId, request.Text ?? string.Empty, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            return Ok(result.Plan);
        }

        return result.NotFound
            ? NotFound(new { error = result.ErrorMessage })
            : BadRequest(new { error = result.ErrorMessage });
    }

    /// <summary>Re-arranges edited tasks into waves and re-estimates them. No model call, so it is instant and free.</summary>
    [HttpPost("arrange", Name = nameof(ArrangeGoal))]
    public async Task<IActionResult> ArrangeGoal(
        [FromRoute] Guid workspaceId,
        [FromBody] ArrangeGoalRequest request,
        CancellationToken cancellationToken)
    {
        var tasks = (request.Tasks ?? new List<GoalTaskInput>())
            .Select(t => new GoalTaskPlan(
                (t.Key ?? string.Empty).Trim(),
                (t.Title ?? string.Empty).Trim(),
                (t.Description ?? string.Empty).Trim(),
                (t.Areas ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
                (t.DependsOn ?? new List<string>()).Select(d => d.Trim()).ToList(),
                t.Size ?? "medium",
                t.AreasGuessed))
            .ToList();

        if (tasks.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count() != tasks.Count || tasks.Any(t => t.Key.Length == 0))
        {
            return BadRequest(new { error = "Every task needs its own key." });
        }

        var result = await _planner
            .ArrangeAsync(workspaceId, tasks, request.InputPerMillionUsd, request.OutputPerMillionUsd, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
        {
            return Ok(result.Plan);
        }

        return result.NotFound
            ? NotFound(new { error = result.ErrorMessage })
            : BadRequest(new { error = result.ErrorMessage });
    }

    /// <summary>Starts a goal from a plan the person has seen: creates its tasks and begins moving them forward.</summary>
    [HttpPost(Name = nameof(StartGoal))]
    public async Task<IActionResult> StartGoal(
        [FromRoute] Guid workspaceId,
        [FromBody] StartGoalRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _start
            .HandleAsync(
                new StartGoalCommand(
                    workspaceId,
                    request.Text ?? string.Empty,
                    request.PlanSource,
                    request.Tasks ?? new List<GoalTaskInput>(),
                    request.EstimatedInputTokens,
                    request.EstimatedOutputTokens,
                    request.EstimatedUsd),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return result.NotFound
                ? NotFound(new { error = result.ErrorMessage })
                : BadRequest(new { error = result.ErrorMessage });
        }

        var goal = await _store.GetAsync(result.GoalId!.Value, workspaceId, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetGoal), new { workspaceId, goalId = result.GoalId }, await ToDetailAsync(goal!, workspaceId, cancellationToken));
    }

    [HttpGet(Name = nameof(GetGoals))]
    public async Task<IActionResult> GetGoals([FromRoute] Guid workspaceId, CancellationToken cancellationToken)
    {
        var goals = await _store.ListAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        return Ok(goals.Select(g => GoalDtoMapper.ToSummary(g, now)));
    }

    [HttpGet("{goalId:guid}", Name = nameof(GetGoal))]
    public async Task<IActionResult> GetGoal([FromRoute] Guid workspaceId, [FromRoute] Guid goalId, CancellationToken cancellationToken)
    {
        var goal = await _store.GetAsync(goalId, workspaceId, cancellationToken).ConfigureAwait(false);
        return goal is null
            ? NotFound(new { error = "Goal not found." })
            : Ok(await ToDetailAsync(goal, workspaceId, cancellationToken));
    }

    /// <summary>Stops moving the goal forward. Tasks that already started keep running.</summary>
    [HttpPost("{goalId:guid}/cancel", Name = nameof(CancelGoal))]
    public async Task<IActionResult> CancelGoal([FromRoute] Guid workspaceId, [FromRoute] Guid goalId, CancellationToken cancellationToken)
    {
        var goal = await _store.GetAsync(goalId, workspaceId, cancellationToken).ConfigureAwait(false);
        if (goal is null)
        {
            return NotFound(new { error = "Goal not found." });
        }

        if (goal.Status == GoalStatus.Active)
        {
            await _store.SetStatusAsync(goalId, GoalStatus.Cancelled, cancellationToken).ConfigureAwait(false);
        }

        var updated = await _store.GetAsync(goalId, workspaceId, cancellationToken).ConfigureAwait(false);
        return Ok(await ToDetailAsync(updated!, workspaceId, cancellationToken));
    }

    private async Task<GoalDetailDto> ToDetailAsync(GoalState goal, Guid workspaceId, CancellationToken cancellationToken)
    {
        var policy = await _policies.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var running = policy is { Paused: false };
        return GoalDtoMapper.ToDetail(
            goal,
            DateTime.UtcNow,
            running && policy!.Level >= AutomationLevel.SemiAuto,
            running && policy!.Level >= AutomationLevel.AutoPr);
    }
}
