using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Ports;

public sealed record GitHubSyncResultDto(
    bool Success,
    bool IsConfigurationError,
    bool IsExternalFailure,
    string? ErrorMessage,
    ExecutionPullRequestRemoteState RemoteState,
    ExecutionPullRequestIntegrityStatus IntegrityStatus,
    DateTime? ClosedAt,
    DateTime? MergedAt,
    ExecutionCiStatus CiStatus,
    IReadOnlyList<ExecutionCiCheck> Checks);

public interface IExecutionGitHubSyncService
{
    Task<GitHubSyncResultDto> SyncPullRequestAndCiAsync(
        TaskExecution execution,
        CancellationToken cancellationToken = default);
}
