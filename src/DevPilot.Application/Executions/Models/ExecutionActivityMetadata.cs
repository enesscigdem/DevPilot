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
    string? Model = null);
