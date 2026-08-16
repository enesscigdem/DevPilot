namespace DevPilot.Application.Executions.Ports;

public sealed record ExecutionFingerprintResult(
    bool Success,
    string? Fingerprint = null,
    string? BaseHeadSha = null,
    bool HasSensitiveFiles = false,
    int ChangedFileCount = 0,
    string? ErrorMessage = null);

public interface IExecutionChangeFingerprintCalculator
{
    Task<ExecutionFingerprintResult> ComputeFingerprintAsync(
        string workspacePath,
        CancellationToken cancellationToken = default);

    Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(
        string workspacePath,
        string treeSha,
        string baseHeadSha,
        CancellationToken cancellationToken = default);
}
