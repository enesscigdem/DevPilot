using DevPilot.Application.Executions.Options;
using Microsoft.Extensions.Configuration;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Resolves the single authoritative <see cref="ExecutionReliabilityOptions"/> instance:
/// defaults → legacy <c>DeveloperAgent:*</c> keys → <c>ExecutionReliability:*</c> section (wins).
/// </summary>
public static class ExecutionReliabilityOptionsFactory
{
    public static ExecutionReliabilityOptions Create(IConfiguration? configuration)
    {
        var options = new ExecutionReliabilityOptions();
        if (configuration == null)
        {
            return options.Normalize();
        }

        // Legacy keys (still honored so existing deployments keep their proven values).
        ApplyInt(configuration, "DeveloperAgent:MaxCompileRepairRounds", 0, v => options.MaxCompileRepairRounds = v);
        ApplyInt(configuration, "DeveloperAgent:MaxTestRepairRounds", 0, v => options.MaxTestRepairRounds = v);
        ApplyInt(configuration, "DeveloperAgent:MaxGenerationCalls", 1, v => options.MaxGenerationCalls = v);
        ApplyInt(configuration, "DeveloperAgent:MaxConcurrentFileGenerations", 1, v => options.MaxConcurrentFileGenerations = v);
        ApplyInt(configuration, "DeveloperAgent:MaxOutputTokens", 1, v => options.MaxOutputTokens = v);
        ApplyInt(configuration, "DeveloperAgent:MaxCompactRetryOutputTokens", 1, v => options.MaxCompactRetryOutputTokens = v);

        // Authoritative section wins.
        var prefix = ExecutionReliabilityOptions.SectionName + ":";
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.MaxCompileRepairRounds), 0, v => options.MaxCompileRepairRounds = v);
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.MaxTestRepairRounds), 0, v => options.MaxTestRepairRounds = v);
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.MaxFlakeReruns), 0, v => options.MaxFlakeReruns = v);
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.MaxAgenticTurns), 1, v => options.MaxAgenticTurns = v);
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.MaxAgenticCheckRuns), 1, v => options.MaxAgenticCheckRuns = v);
        if (bool.TryParse(configuration[prefix + nameof(ExecutionReliabilityOptions.AgenticRepairEnabled)], out var agentic))
        {
            options.AgenticRepairEnabled = agentic;
        }
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.MaxGenerationCalls), 1, v => options.MaxGenerationCalls = v);
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.MaxConcurrentFileGenerations), 1, v => options.MaxConcurrentFileGenerations = v);
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.MaxOutputTokens), 1, v => options.MaxOutputTokens = v);
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.MaxCompactRetryOutputTokens), 1, v => options.MaxCompactRetryOutputTokens = v);
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.ImpactAnalysisMaxOutputTokens), 1, v => options.ImpactAnalysisMaxOutputTokens = v);
        ApplyInt(configuration, prefix + nameof(ExecutionReliabilityOptions.ImpactAnalysisRecoveryMaxOutputTokens), 1, v => options.ImpactAnalysisRecoveryMaxOutputTokens = v);

        return options.Normalize();
    }

    private static void ApplyInt(IConfiguration configuration, string key, int min, Action<int> apply)
    {
        if (int.TryParse(configuration[key], out var value) && value >= min)
        {
            apply(value);
        }
    }
}
