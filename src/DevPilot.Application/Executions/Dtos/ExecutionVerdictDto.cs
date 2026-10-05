namespace DevPilot.Application.Executions.Dtos;

/// <summary>One explained fact behind a verdict (severity: info | success | warning | danger).</summary>
public sealed record VerdictFindingDto(string Kind, string Severity, string Message);

/// <summary>
/// Human-readable explanation of an execution's verification outcome: what happened, why it ended this way
/// and what to do next. Built deterministically from recorded execution activities.
/// </summary>
public sealed record ExecutionVerdictDto(
    string Outcome,
    string Severity,
    string Headline,
    string? RecommendedAction,
    IReadOnlyList<VerdictFindingDto> Findings,
    bool BaselineUnverified,
    bool FlakeConfirmed,
    bool TestWeakeningSuspected,
    bool StaleBase,
    int CompileRepairRounds,
    int TestRepairRounds,
    int CompactRetries,
    int ApplicabilityRepairs,
    IReadOnlyList<string> ChecksNotRun,
    string? BaseFreshness = null,
    int? BaseBehindCount = null,
    string? BaseCommitSha = null,
    int FailingTestCount = 0,
    IReadOnlyList<string>? FailingTestGroups = null,
    string? SuggestedFix = null);

public sealed record ExecutionStageTimingDto(string Stage, long DurationMs);

/// <summary>AI usage and latency aggregated from recorded provider-call activities.</summary>
public sealed record ExecutionUsageDto(
    int ProviderCalls,
    int FailedProviderCalls,
    int CallsWithoutTokenData,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long ProviderTimeMs,
    decimal? EstimatedCostUsd,
    IReadOnlyList<ExecutionStageTimingDto> StageTimings);

/// <summary>Persisted terminal verification record (history + list/overview queries without replaying logs).</summary>
public sealed record ExecutionVerificationSnapshot(
    int SchemaVersion,
    DateTime CapturedAt,
    ExecutionVerdictDto Verdict,
    ExecutionUsageDto Usage);
