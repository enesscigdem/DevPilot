using DevPilot.Application.Automation;
using DevPilot.Application.Executions.Commands.StartExecution;
using DevPilot.Application.Tasks.Commands.ApproveTask;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Goals;

public interface IGoalOrchestrator
{
    /// <summary>Moves one goal as far as it can go right now. Idempotent: safe to call every few seconds.</summary>
    Task AdvanceAsync(Guid goalId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides, task by task, when a goal's work may start. A task waits only for earlier tasks expected to change
/// the same code (and for what it explicitly depends on); once those have ended, merged or not, it goes, and a
/// task that failed never holds anything back. Starting a task is analysis first (on a base that already holds
/// the earlier merges), then, if automation allows it, approval and start, with a last check that the real
/// plan does not collide with work already in flight.
/// </summary>
public sealed class GoalOrchestrator : IGoalOrchestrator
{
    public const int MaxAnalysisAttempts = 2;

    private readonly IGoalStore _store;
    private readonly IAutomationPolicyStore _policies;
    private readonly IAutomationWorkReader _work;
    private readonly IGoalAnalysisDispatcher _analysis;
    private readonly IApproveTaskCommandHandler _approveTask;
    private readonly IStartExecutionCommandHandler _startExecution;
    private readonly ILogger<GoalOrchestrator> _logger;
    private readonly TimeProvider _time;

    public GoalOrchestrator(
        IGoalStore store,
        IAutomationPolicyStore policies,
        IAutomationWorkReader work,
        IGoalAnalysisDispatcher analysis,
        IApproveTaskCommandHandler approveTask,
        IStartExecutionCommandHandler startExecution,
        ILogger<GoalOrchestrator> logger,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _store = store;
        _policies = policies;
        _work = work;
        _analysis = analysis;
        _approveTask = approveTask;
        _startExecution = startExecution;
        _logger = logger;
    }

    public async Task AdvanceAsync(Guid goalId, CancellationToken cancellationToken = default)
    {
        var goal = await _store.GetAsync(goalId, null, cancellationToken).ConfigureAwait(false);
        if (goal is null || goal.Status != GoalStatus.Active)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var ordered = goal.Tasks.OrderBy(t => t.Position).ToList();
        var phase = ordered.ToDictionary(t => t.Key, t => GoalPhases.Of(t, now), StringComparer.Ordinal);

        if (phase.Values.All(GoalPhases.IsSettled))
        {
            // Every try has ended, but a failed task means the goal's work is not delivered. The goal stays active so the
            // failure remains visible and a retry of that task is picked up again instead of landing on a closed goal.
            if (phase.Values.Any(p => p == GoalPhase.Failed))
            {
                return;
            }

            await _store.SetStatusAsync(goal.Id, GoalStatus.Completed, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Goal {GoalId} is complete.", goal.Id);
            return;
        }

        var policy = await _policies.GetAsync(goal.RepositoryWorkspaceId, cancellationToken).ConfigureAwait(false);
        var slots = Math.Max(1, policy?.MaxParallelExecutions ?? AutomationPolicy.DefaultMaxParallelExecutions);

        await RequestAnalysesAsync(ordered, phase, slots, cancellationToken).ConfigureAwait(false);

        var automatic = policy is { Paused: false } && policy.Level >= AutomationLevel.SemiAuto;
        if (automatic)
        {
            await ApproveAndStartAsync(goal, ordered, phase, slots, policy!.ConflictMode, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequestAnalysesAsync(
        IReadOnlyList<GoalTaskState> ordered,
        Dictionary<string, GoalPhase> phase,
        int slots,
        CancellationToken ct)
    {
        var free = slots - phase.Values.Count(p => p == GoalPhase.Analyzing);
        foreach (var task in ordered.Where(t => phase[t.Key] == GoalPhase.Waiting))
        {
            if (!task.BlockedBy.All(key => !phase.TryGetValue(key, out var p) || GoalPhases.IsSettled(p)))
            {
                continue;
            }

            if (task.AnalysisAttempts >= MaxAnalysisAttempts)
            {
                await SetNoteIfChangedAsync(task, "DevPilot could not start the analysis of this task. Open it to retry.", ct).ConfigureAwait(false);
                continue;
            }

            if (free <= 0)
            {
                break;
            }

            await _store.MarkAnalysisRequestedAsync(task.GoalTaskId, ct).ConfigureAwait(false);
            _analysis.EnqueueAnalysis(task.TaskId);
            phase[task.Key] = GoalPhase.Analyzing;
            free--;
        }
    }

    private async Task ApproveAndStartAsync(
        GoalState goal,
        IReadOnlyList<GoalTaskState> ordered,
        Dictionary<string, GoalPhase> phase,
        int slots,
        ConflictMode mode,
        CancellationToken ct)
    {
        var active = await _work.CountActiveExecutionsAsync(goal.RepositoryWorkspaceId, ct).ConfigureAwait(false);
        var free = slots - active;

        // Files already being changed by tasks of this goal that have started and not ended.
        var inFlight = ordered
            .Where(t => GoalPhases.IsInFlight(phase[t.Key]))
            .Select(t => (Task: t, Files: FilesOf(t)))
            .ToList();

        foreach (var task in ordered.Where(t => phase[t.Key] == GoalPhase.PlanReady))
        {
            var files = FilesOf(task);
            var clash = inFlight
                .Select(other => (other.Task, Shared: GoalConflictRules.Clashes(
                    mode, files, other.Files, task.StructuralFiles, other.Task.StructuralFiles)))
                .FirstOrDefault(x => x.Shared.Count > 0);
            if (clash.Task is not null)
            {
                await SetNoteIfChangedAsync(
                    task,
                    $"Waits for “{clash.Task.Title}”: both change {clash.Shared[0]}.",
                    ct).ConfigureAwait(false);
                continue;
            }

            if (free <= 0)
            {
                continue;
            }

            var approved = await _approveTask.HandleAsync(new ApproveTaskCommand(task.TaskId), ct).ConfigureAwait(false);
            if (!approved.Success)
            {
                await SetNoteIfChangedAsync(task, approved.ErrorMessage ?? "The plan could not be approved.", ct).ConfigureAwait(false);
                continue;
            }

            var started = await _startExecution.HandleAsync(new StartExecutionCommand(task.TaskId), ct).ConfigureAwait(false);
            if (!started.Success)
            {
                await SetNoteIfChangedAsync(task, started.ErrorMessage ?? "The task could not be started.", ct).ConfigureAwait(false);
                continue;
            }

            free--;
            phase[task.Key] = GoalPhase.Running;
            inFlight.Add((task, files));
            await SetNoteIfChangedAsync(task, null, ct).ConfigureAwait(false);
            _logger.LogInformation("Goal {GoalId}: approved and started task {TaskId} ({Key}).", goal.Id, task.TaskId, task.Key);
        }
    }

    /// <summary>What a task will really change once its analysis exists, otherwise what the planner guessed.</summary>
    private static IReadOnlyList<string> FilesOf(GoalTaskState task) =>
        task.ImpactedFiles.Count > 0 ? task.ImpactedFiles : task.Areas;

    private Task SetNoteIfChangedAsync(GoalTaskState task, string? note, CancellationToken ct) =>
        string.Equals(task.Note, note, StringComparison.Ordinal)
            ? Task.CompletedTask
            : _store.SetNoteAsync(task.GoalTaskId, note, ct);
}
