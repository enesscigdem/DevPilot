using DevPilot.Domain.Entities;

namespace DevPilot.Application.Executions.Ports;

public interface IExecutionGitHubPullRequestService
{
    Task<ExecutionPullRequestServiceResult> CreateOrAdoptPullRequestAsync(
        TaskExecution execution,
        Guid attemptId,
        CancellationToken cancellationToken = default);
}

public sealed record ExecutionPullRequestServiceResult(
    bool Success,
    bool IsConfigurationError,
    bool IsConflict,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string? BaseBranch,
    DateTime? CreatedAt,
    string? ErrorMessage,
    bool WasPostSent = false,
    bool IsDefinitiveNoMutationFailure = false);
