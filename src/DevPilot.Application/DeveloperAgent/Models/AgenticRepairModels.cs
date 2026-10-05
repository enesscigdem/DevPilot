namespace DevPilot.Application.DeveloperAgent.Models;

/// <summary>Result of one run of the repository's verification, as shown to the repairing model.</summary>
/// <param name="Success">Every check (build and tests) passed.</param>
/// <param name="Summary">Bounded, model-readable description of what failed.</param>
/// <param name="FailureCount">Number of failing tests, or 1 for a non-test failure; 0 on success.</param>
/// <param name="Fingerprint">Stable identity of the failing set, used to detect "no progress".</param>
public sealed record AgenticCheckObservation(bool Success, string Summary, int FailureCount, string? Fingerprint);

public sealed record AgenticRepairRequest(
    Guid ExecutionId,
    string TaskTitle,
    string? AcceptanceCriteria,
    string WorkspacePath,
    string InitialFailure,
    IReadOnlyList<string> TouchedFiles,
    string? Model,
    Func<CancellationToken, Task<AgenticCheckObservation>> RunChecksAsync);

public sealed record AgenticRepairOutcome(
    bool Success,
    string StopReason,
    int Turns,
    int CheckRuns,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyDictionary<string, string> OriginalContents,
    string? Model);
