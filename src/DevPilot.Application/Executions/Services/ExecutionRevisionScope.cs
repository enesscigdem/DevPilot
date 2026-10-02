using DevPilot.Application.Executions.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

/// <summary>
/// A requested fix reopens a finished execution. While it runs, the old run's progress, duration and green checks
/// must not describe it, so everything shown for it is taken from the activity since the request.
/// </summary>
public static class ExecutionRevisionScope
{
    public static bool HasRevision(TaskExecution execution) =>
        execution.LastChangeRequestAt.HasValue && execution.ChangeRequestCount > 0;

    /// <summary>The fix is still being made (the result is only stored when it ends).</summary>
    public static bool IsActive(TaskExecution execution) =>
        HasRevision(execution) &&
        execution.Status == TaskExecutionStatus.Running &&
        execution.LastChangeRequestResult == null;

    public static IReadOnlyList<ExecutionActivity> Since(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities)
    {
        if (!execution.LastChangeRequestAt.HasValue)
        {
            return activities;
        }

        var since = execution.LastChangeRequestAt.Value;
        return activities.Where(a => a.CreatedAt >= since).ToList();
    }

    /// <summary>Review and pull request come after the fix, so they cannot already be "done" while it runs.</summary>
    public static IReadOnlyList<ExecutionStageStepDto> ForActiveRevision(IReadOnlyList<ExecutionStageStepDto> stages) =>
        stages
            .Select(stage => stage.StageKey is "review" or "pr"
                ? new ExecutionStageStepDto { StageKey = stage.StageKey, Label = stage.Label, State = ExecutionStageStepState.Todo }
                : stage)
            .ToList();
}
