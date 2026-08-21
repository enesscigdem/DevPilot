namespace DevPilot.Application.Executions.Options;

public sealed class ExecutionReliabilityOptions
{
    public const string SectionName = "ExecutionReliability";

    public int MaxCompileRepairRounds { get; set; } = 2;
    public int MaxTestRepairRounds { get; set; } = 2;
    public int MaxGenerationCalls { get; set; } = 15;
    public int MaxConcurrentFileGenerations { get; set; } = 1;
    public int MaxOutputTokens { get; set; } = 32768;
    public int MaxCompactRetryOutputTokens { get; set; } = 8192;
}
