namespace DevPilot.Application.Goals;

/// <summary>
/// Estimates what carrying out a goal will use. With enough earlier runs of the repository the estimate is
/// their average, otherwise it is a rough default and says so. It is an honest range finder, never a quote.
/// </summary>
public static class GoalCostEstimator
{
    public const int MinHistorySamples = 3;

    // Typical usage of one medium task: analysis, generation and a few repairs.
    private const long DefaultInputTokens = 120_000;
    private const long DefaultOutputTokens = 25_000;

    public static GoalCostEstimate Estimate(
        IReadOnlyList<GoalTaskPlan> tasks,
        GoalUsageHistory? history,
        decimal? inputPerMillionUsd,
        decimal? outputPerMillionUsd)
    {
        var useHistory = history is { Samples: >= MinHistorySamples } && history.AverageInputTokens > 0;
        var baseInput = useHistory ? history!.AverageInputTokens : DefaultInputTokens;
        var baseOutput = useHistory ? history!.AverageOutputTokens : DefaultOutputTokens;

        double input = 0, output = 0;
        foreach (var task in tasks)
        {
            var factor = SizeFactor(task.Size);
            input += baseInput * factor;
            output += baseOutput * factor;
        }

        decimal? usd = inputPerMillionUsd.HasValue && outputPerMillionUsd.HasValue
            ? Math.Round((decimal)input / 1_000_000m * inputPerMillionUsd.Value
                         + (decimal)output / 1_000_000m * outputPerMillionUsd.Value, 2)
            : null;

        return new GoalCostEstimate(
            (long)input,
            (long)output,
            usd,
            useHistory ? "history" : "default",
            useHistory ? history!.Samples : 0,
            inputPerMillionUsd,
            outputPerMillionUsd);
    }

    public static double SizeFactor(string? size) => (size ?? string.Empty).ToLowerInvariant() switch
    {
        "small" => 0.6,
        "large" => 1.8,
        _ => 1.0
    };

    public static string NormalizeSize(string? size) => (size ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "small" or "s" or "xs" => "small",
        "large" or "l" or "xl" => "large",
        _ => "medium"
    };
}
