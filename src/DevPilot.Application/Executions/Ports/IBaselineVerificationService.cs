using DevPilot.Application.Executions.Models;

namespace DevPilot.Application.Executions.Ports;

public enum BaselineFailureClassification
{
    PreExisting = 0,
    NewRegression = 1,
    Changed = 2,
    Unknown = 3
}

public sealed record NormalizedFailureItem(
    string FailureKey,
    string? TestName,
    string ErrorSummary,
    string NormalizedDiagnostic,
    string? Location = null);

public sealed record BaselineCheckEvidence(
    string CheckId,
    string BaseCommitSha,
    bool Success,
    IReadOnlyList<NormalizedFailureItem> Failures,
    string? ErrorSummary = null);

public sealed record BaselineFailureComparison(
    BaselineFailureClassification Classification,
    int PreExistingCount,
    int NewRegressionCount,
    int ChangedCount,
    IReadOnlyList<NormalizedFailureItem> PreExistingFailures,
    IReadOnlyList<NormalizedFailureItem> NewRegressions,
    IReadOnlyList<NormalizedFailureItem> ChangedFailures,
    string Summary,
    bool CacheHit = false,
    string? BaseCommitSha = null,
    long? DurationMs = null);

public sealed record BaselineVerificationKey(
    string RepositoryWorkspaceKey,
    string BaseCommitSha,
    string CheckId,
    string? TargetedTestFilter = null);

public interface IBaselineVerificationService
{
    Task<BaselineFailureComparison> EvaluateTestFailureAsync(
        string workspacePath,
        string sourceRepositoryPath,
        string baseCommitSha,
        RepositoryCheck check,
        RepositoryCheckResult taskCheckResult,
        CancellationToken cancellationToken = default);

    Task<BaselineFailureComparison> EvaluateCompilerFailureAsync(
        string workspacePath,
        string sourceRepositoryPath,
        string baseCommitSha,
        RepositoryCheck check,
        RepositoryCheckResult taskCheckResult,
        CancellationToken cancellationToken = default);
}
