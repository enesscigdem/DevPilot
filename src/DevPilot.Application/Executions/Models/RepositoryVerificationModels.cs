namespace DevPilot.Application.Executions.Models;

public enum RepositoryCheckKind
{
    Build = 0,
    TypeCheck = 1,
    Lint = 2,
    Test = 3,
    Other = 4
}

public enum RepositoryCheckSource
{
    DotNetManifest = 0,
    PackageJsonScript = 1,
    PythonToolConfiguration = 2
}

public enum RepositoryVerificationState
{
    Configured = 0,
    Unconfigured = 1,
    InfrastructureFailure = 2
}

public enum RepositoryCheckFailureCategory
{
    None = 0,
    VerificationFailure = 1,
    InfrastructureFailure = 2
}

/// <summary>
/// A deterministic verification operation discovered from repository-owned evidence.
/// Commands are data from trusted discovery, never model output.
/// </summary>
public sealed record RepositoryCheck(
    string Id,
    string DisplayName,
    RepositoryCheckKind Kind,
    string Ecosystem,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    bool Required,
    TimeSpan Timeout,
    RepositoryCheckSource Source,
    string EvidencePath,
    string? EvidenceFingerprint = null,
    bool SupportsSkipBuild = false,
    bool SupportsTargetedTest = false,
    int Order = 0,
    string? DiscoveryEvidence = null);

public sealed record RepositoryProfile(
    RepositoryVerificationState State,
    IReadOnlyList<string> Ecosystems,
    IReadOnlyList<RepositoryCheck> Checks,
    string? Message = null,
    bool HasUnresolvedVerification = false)
{
    public bool HasRequiredChecks => Checks.Any(check => check.Required);
}

public sealed record RepositoryPreflightRequest(
    string WorkspacePath,
    string BranchName);

public sealed record RepositoryCheckExecutionRequest(
    string WorkspacePath,
    string BranchName,
    RepositoryCheck Check,
    bool SkipBuild = false,
    string? TestFilter = null);

public enum VerificationOutcome
{
    Passed = 0,
    NoNewRegressions = 1,
    Failed = 2,
    Unknown = 3
}

public sealed record RepositoryCheckResult : ExecutionValidationResult
{
    public string CheckId { get; init; } = string.Empty;
    public string CheckDisplayName { get; init; } = string.Empty;
    public RepositoryCheckKind CheckKind { get; init; }
    public RepositoryCheckFailureCategory FailureCategory { get; init; }
    public VerificationOutcome Outcome { get; init; } = VerificationOutcome.Passed;
    public int PreExistingFailureCount { get; init; }
    public int NewRegressionCount { get; init; }
}
