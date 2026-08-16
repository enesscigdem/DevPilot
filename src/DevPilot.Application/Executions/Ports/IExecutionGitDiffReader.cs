using DevPilot.Application.Executions.Dtos;

namespace DevPilot.Application.Executions.Ports;

public sealed record ExecutionGitDiffResult(
    bool Success,
    string? ErrorMessage = null,
    IReadOnlyList<ExecutionReviewFileDto>? ChangedFiles = null,
    string DiffText = "",
    bool DiffTruncated = false);

public interface IExecutionGitDiffReader
{
    Task<ExecutionGitDiffResult> ReadWorkspaceDiffAsync(
        string workspacePath,
        string branchName,
        CancellationToken cancellationToken = default);
}
