using DevPilot.Domain.Entities;

namespace DevPilot.Application.Executions.Ports;

public sealed record ExecutionPushResult(
    bool Success,
    bool IsAlreadyPushed = false,
    string? RemoteBranchName = null,
    string? RemoteCommitSha = null,
    DateTime? PushedAt = null,
    string? ErrorMessage = null);

public interface IExecutionGitPushService
{
    Task<ExecutionPushResult> PushExecutionBranchAsync(
        TaskExecution execution,
        Guid attemptId,
        CancellationToken cancellationToken = default);
}
