namespace DevPilot.Application.Goals;

/// <summary>One task of a goal as planned: what to do, where it will probably land and what it must wait for.</summary>
public sealed record GoalTaskPlan(
    string Key,
    string Title,
    string Description,
    IReadOnlyList<string> Areas,
    IReadOnlyList<string> DependsOn,
    string Size,
    bool AreasGuessed = false);

/// <summary>Tasks that can run at the same time; a wave starts only after the previous one has been handled.</summary>
public sealed record GoalWave(int Number, IReadOnlyList<string> TaskKeys);

/// <summary>Two tasks expected to change the same code, which is why they are not in the same wave.</summary>
public sealed record GoalConflict(string FirstKey, string SecondKey, IReadOnlyList<string> Shared);

/// <summary>Rough size of the whole goal. <see cref="Basis"/> is "history" when it comes from earlier runs of this repository.</summary>
public sealed record GoalCostEstimate(
    long InputTokens,
    long OutputTokens,
    decimal? Usd,
    string Basis,
    int Samples,
    decimal? InputPerMillionUsd = null,
    decimal? OutputPerMillionUsd = null);

public sealed record GoalPlanWarning(string Code, string? TaskKey = null);

public sealed record GoalPlan(
    IReadOnlyList<GoalTaskPlan> Tasks,
    IReadOnlyList<GoalWave> Waves,
    IReadOnlyList<GoalConflict> Conflicts,
    GoalCostEstimate Estimate,
    IReadOnlyList<GoalPlanWarning> Warnings,
    string Source,
    long PlanningTokens);

public sealed class GoalPlanResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public bool NotFound { get; set; }

    public GoalPlan? Plan { get; set; }
}

/// <summary>What earlier executions of a repository used on average, so an estimate reflects reality.</summary>
public sealed record GoalUsageHistory(long AverageInputTokens, long AverageOutputTokens, int Samples);

public interface IGoalHistoryReader
{
    /// <summary>Average usage of the most recent finished executions that recorded token data; null when there are too few.</summary>
    Task<GoalUsageHistory?> GetRecentUsageAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default);
}
