namespace DevPilot.Application.Executions.Models;

/// <summary>
/// Tightly-allowlisted metadata structure for execution activity telemetry.
/// Strictly prevents raw AI outputs, source code, stdout/stderr, or secrets from being stored.
/// </summary>
public sealed record ExecutionActivityMetadata(
    string? BranchName = null,
    int? ModifiedFileCount = null,
    bool? BuildPassed = null,
    bool? TestPassed = null,
    string? Model = null,
    string? EventKind = null,
    int? LogicalProviderCallCount = null,
    string? ProviderCallKind = null,
    int? ProviderAttemptCount = null,
    int? RequestedOutputTokens = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    long? StageDurationMs = null,
    string? TargetFile = null,
    string? RepairKind = null,
    int? RepairRound = null,
    IReadOnlyList<string>? RepairFiles = null,
    string? FailureFingerprint = null,
    string? BeforeChangeFingerprint = null,
    string? AfterChangeFingerprint = null,
    string? ProgressResult = null);
