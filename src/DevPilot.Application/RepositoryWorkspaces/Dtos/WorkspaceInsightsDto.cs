namespace DevPilot.Application.RepositoryWorkspaces.Dtos;

public sealed record InsightOutcomeCountDto(string Outcome, int Count);

/// <summary>A "what DevPilot caught / absorbed" counter (e.g. pre-existing failures separated, weakened tests caught).</summary>
public sealed record InsightSignalDto(string Key, string Label, string Description, int Count);

public sealed record InsightFailureReasonDto(string Reason, int Count);

public sealed record InsightExecutionRowDto(
    Guid ExecutionId,
    Guid TaskId,
    string TaskTitle,
    int AttemptNumber,
    string Status,
    string Outcome,
    string Headline,
    DateTime CreatedAt,
    int? DurationSeconds,
    int RepairRounds,
    int ProviderCalls,
    long TotalTokens,
    decimal? EstimatedCostUsd);

public sealed record InsightRetriedTaskDto(
    Guid TaskId,
    string TaskTitle,
    int Attempts,
    Guid FirstExecutionId,
    Guid LastExecutionId,
    string FirstOutcome,
    string LastOutcome,
    bool Improved);

public sealed record WorkspaceInsightsTotalsDto(
    int Executions,
    int Cancelled,
    int Measured,
    int DeliveryReady,
    double? DeliveryReadyRate,
    double? VerifiedRate,
    int FirstPass,
    double? FirstPassRate,
    int Repaired,
    int RepairedRecovered,
    double? RepairRecoveryRate,
    int? AvgDurationSeconds,
    int? MedianDurationSeconds,
    long TotalTokens,
    long? AvgTokensPerExecution,
    double? AvgProviderCalls,
    decimal? TotalCostUsd);

public sealed record WorkspaceInsightsDto(
    DateTime GeneratedAt,
    int WindowSize,
    WorkspaceInsightsTotalsDto Totals,
    IReadOnlyList<InsightOutcomeCountDto> Outcomes,
    IReadOnlyList<InsightSignalDto> Signals,
    IReadOnlyList<InsightFailureReasonDto> TopFailureReasons,
    IReadOnlyList<InsightExecutionRowDto> Recent,
    IReadOnlyList<InsightRetriedTaskDto> RetriedTasks);
