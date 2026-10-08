using System.Text.Json.Serialization;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Goals;

public sealed record GoalTaskDto(
    string Key,
    Guid TaskId,
    string Title,
    string Description,
    int Wave,
    string Size,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] GoalPhase Phase,
    IReadOnlyList<string> Areas,
    IReadOnlyList<string> WaitingFor,
    string? Note,
    Guid? ExecutionId,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string? ErrorMessage,
    int ImpactedFileCount,
    bool NeedsPerson);

public sealed record GoalProgressDto(
    int Total,
    int Merged,
    int PullRequests,
    int Running,
    int Failed,
    int Stopped);

public sealed record GoalSummaryDto(
    Guid Id,
    string Title,
    GoalStatus Status,
    string PlanSource,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    GoalProgressDto Progress,
    long EstimatedInputTokens,
    long EstimatedOutputTokens,
    decimal? EstimatedUsd);

public sealed record GoalDetailDto(
    Guid Id,
    string Title,
    string Text,
    GoalStatus Status,
    string PlanSource,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    GoalProgressDto Progress,
    long EstimatedInputTokens,
    long EstimatedOutputTokens,
    decimal? EstimatedUsd,
    int WaveCount,
    bool AutomationActive,
    IReadOnlyList<GoalTaskDto> Tasks);

public static class GoalDtoMapper
{
    public static GoalSummaryDto ToSummary(GoalState goal, DateTime nowUtc) =>
        new(
            goal.Id,
            goal.Title,
            goal.Status,
            goal.PlanSource,
            goal.CreatedAt,
            goal.CompletedAt,
            ProgressOf(goal, nowUtc),
            goal.EstimatedInputTokens,
            goal.EstimatedOutputTokens,
            goal.EstimatedUsd);

    /// <param name="automationActive">True when DevPilot approves plans on its own; a ready plan is then not waiting for a person.</param>
    /// <param name="automationDelivers">True when DevPilot also approves reviews that pass its safety gate; a finished run is then
    /// only waiting for a person once the gate has handed it back.</param>
    public static GoalDetailDto ToDetail(GoalState goal, DateTime nowUtc, bool automationActive, bool automationDelivers = false)
    {
        var phase = goal.Tasks.ToDictionary(t => t.Key, t => GoalPhases.Of(t, nowUtc), StringComparer.Ordinal);
        var tasks = goal.Tasks
            .OrderBy(t => t.Position)
            .Select(t =>
            {
                var current = phase[t.Key];
                // Only a task that has not started is waiting; for the others the question does not arise.
                var waitingFor = current == GoalPhase.Waiting
                    ? t.BlockedBy.Where(key => phase.TryGetValue(key, out var p) && !GoalPhases.IsSettled(p)).ToList()
                    : new List<string>();
                var run = t.Execution;
                return new GoalTaskDto(
                    t.Key,
                    t.TaskId,
                    t.Title,
                    t.Description,
                    t.Wave,
                    t.Size,
                    current,
                    t.Areas,
                    waitingFor,
                    t.Note,
                    run?.Id,
                    run?.PullRequestNumber,
                    run?.PullRequestUrl,
                    current == GoalPhase.Failed ? run?.ErrorMessage : null,
                    t.ImpactedFiles.Count,
                    NeedsPerson(current, run, automationActive, automationDelivers));
            })
            .ToList();

        return new GoalDetailDto(
            goal.Id,
            goal.Title,
            goal.Text,
            goal.Status,
            goal.PlanSource,
            goal.CreatedAt,
            goal.CompletedAt,
            ProgressOf(goal, nowUtc),
            goal.EstimatedInputTokens,
            goal.EstimatedOutputTokens,
            goal.EstimatedUsd,
            goal.Tasks.Select(t => t.Wave).DefaultIfEmpty(0).Max(),
            automationActive,
            tasks);
    }

    /// <summary>True only when the task cannot move on without a person: automation neither will nor may do the next step.</summary>
    public static bool NeedsPerson(GoalPhase phase, GoalExecutionState? run, bool automationActive, bool automationDelivers) =>
        phase switch
        {
            GoalPhase.PlanReady => !automationActive,
            GoalPhase.InReview => !automationDelivers || run?.AutomationHeld == true,
            GoalPhase.Failed => true,
            _ => false
        };

    private static GoalProgressDto ProgressOf(GoalState goal, DateTime nowUtc)
    {
        var phases = goal.Tasks.Select(t => GoalPhases.Of(t, nowUtc)).ToList();
        return new GoalProgressDto(
            phases.Count,
            phases.Count(p => p == GoalPhase.Merged),
            phases.Count(p => p == GoalPhase.PullRequest),
            phases.Count(p => p is GoalPhase.Running or GoalPhase.Analyzing or GoalPhase.Delivering),
            phases.Count(p => p == GoalPhase.Failed),
            phases.Count(p => p == GoalPhase.Stopped));
    }
}
