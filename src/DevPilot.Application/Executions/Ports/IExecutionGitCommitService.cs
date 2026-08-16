using DevPilot.Domain.Entities;

namespace DevPilot.Application.Executions.Ports;

public sealed record ExecutionCommitResult(
    bool Success,
    bool IsAlreadyCommitted = false,
    string? CommitSha = null,
    DateTime? CommittedAt = null,
    string? ErrorMessage = null);

public interface IExecutionGitCommitService
{
    Task<ExecutionCommitResult> CommitApprovedExecutionAsync(
        TaskExecution execution,
        string taskTitle,
        Guid attemptId,
        CancellationToken cancellationToken = default);
}
