namespace DevPilot.Application.Executions.Options;

/// <summary>
/// Single authoritative source for execution / generation reliability limits.
/// Resolved once at startup (defaults → legacy <c>DeveloperAgent:*</c> keys → <c>ExecutionReliability:*</c> section;
/// see Infrastructure <c>ExecutionReliabilityOptionsFactory</c>). The DeveloperAgent, the execution processor and
/// impact analysis all consume the same resolved instance.
/// </summary>
public sealed class ExecutionReliabilityOptions
{
    public const string SectionName = "ExecutionReliability";

    /// <summary>Hard ceiling for flake confirmation reruns per execution.</summary>
    public const int MaxFlakeRerunsCeiling = 1;

    public int MaxCompileRepairRounds { get; set; } = 3;
    public int MaxTestRepairRounds { get; set; } = 2;

    /// <summary>Flake confirmation reruns allowed per execution (0 disables, capped at <see cref="MaxFlakeRerunsCeiling"/>).</summary>
    public int MaxFlakeReruns { get; set; } = MaxFlakeRerunsCeiling;

    public int MaxGenerationCalls { get; set; } = DevPilot.Domain.Constants.ExecutionCapacityPolicy.MaxGenerationCalls;
    public int MaxConcurrentFileGenerations { get; set; } = 1;
    public int MaxOutputTokens { get; set; } = 32768;

    /// <summary>Null means "derive from <see cref="MaxOutputTokens"/>" (never below 24576).</summary>
    public int? MaxCompactRetryOutputTokens { get; set; }

    /// <summary>Normal impact-analysis output budget.</summary>
    public int ImpactAnalysisMaxOutputTokens { get; set; } = 6144;

    /// <summary>Output budget for compact recovery / grounding repair of an impact analysis.</summary>
    public int ImpactAnalysisRecoveryMaxOutputTokens { get; set; } = 8192;

    public int EffectiveMaxCompactRetryOutputTokens =>
        MaxCompactRetryOutputTokens is > 0 ? MaxCompactRetryOutputTokens.Value : Math.Max(24576, MaxOutputTokens);

    /// <summary>Clamps values into safe bounds; escalation is always bounded.</summary>
    public ExecutionReliabilityOptions Normalize()
    {
        MaxCompileRepairRounds = Math.Max(0, MaxCompileRepairRounds);
        MaxTestRepairRounds = Math.Max(0, MaxTestRepairRounds);
        MaxFlakeReruns = Math.Clamp(MaxFlakeReruns, 0, MaxFlakeRerunsCeiling);
        MaxGenerationCalls = Math.Max(1, MaxGenerationCalls);
        MaxConcurrentFileGenerations = Math.Clamp(MaxConcurrentFileGenerations, 1, 4);
        MaxOutputTokens = Math.Max(1, MaxOutputTokens);
        ImpactAnalysisMaxOutputTokens = Math.Clamp(ImpactAnalysisMaxOutputTokens, 1024, 16384);
        ImpactAnalysisRecoveryMaxOutputTokens = Math.Clamp(
            ImpactAnalysisRecoveryMaxOutputTokens,
            ImpactAnalysisMaxOutputTokens,
            16384);
        return this;
    }
}
