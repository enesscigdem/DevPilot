using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.SyncPullRequest;

public sealed record SyncPullRequestCommand(Guid ExecutionId, Guid? RepositoryWorkspaceId = null);

public enum SyncPullRequestResultStatus
{
    Success,
    NotFound,
    Conflict,
    ExternalFailure
}

public sealed record ExecutionCiCheckDto(
    Guid Id,
    long ExternalId,
    string Name,
    string Source,
    string CheckType,
    string Status,
    string? Conclusion,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed record SyncPullRequestResponseDto(
    Guid ExecutionId,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string PullRequestRemoteState,
    string PullRequestIntegrityStatus,
    string HeadCommitSha,
    string CiStatus,
    int CheckCount,
    IReadOnlyList<ExecutionCiCheckDto> Checks,
    DateTime? LastSyncedAt,
    string? SyncError = null);

public sealed class SyncPullRequestResult
{
    public SyncPullRequestResultStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    public SyncPullRequestResponseDto? Response { get; private set; }

    public static SyncPullRequestResult Ok(SyncPullRequestResponseDto response) =>
        new() { Status = SyncPullRequestResultStatus.Success, Response = response };

    public static SyncPullRequestResult NotFound(string message) =>
        new() { Status = SyncPullRequestResultStatus.NotFound, ErrorMessage = message };

    public static SyncPullRequestResult Conflict(string message) =>
        new() { Status = SyncPullRequestResultStatus.Conflict, ErrorMessage = message };

    public static SyncPullRequestResult ExternalFailure(string message, SyncPullRequestResponseDto? existingSnapshot = null) =>
        new() { Status = SyncPullRequestResultStatus.ExternalFailure, ErrorMessage = message, Response = existingSnapshot };
}

public interface ISyncPullRequestCommandHandler
{
    Task<SyncPullRequestResult> HandleAsync(
        SyncPullRequestCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class SyncPullRequestCommandHandler : ISyncPullRequestCommandHandler
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(1);

    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionGitHubSyncService _githubSyncService;
    private readonly ILogger<SyncPullRequestCommandHandler> _logger;

    public SyncPullRequestCommandHandler(
        IExecutionRepository executionRepository,
        IExecutionGitHubSyncService githubSyncService,
        ILogger<SyncPullRequestCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _githubSyncService = githubSyncService;
        _logger = logger;
    }

    public async Task<SyncPullRequestResult> HandleAsync(
        SyncPullRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return SyncPullRequestResult.NotFound("Execution not found.");
        }

        if (command.RepositoryWorkspaceId.HasValue &&
            execution.DevelopmentTask?.RepositoryWorkspaceId != command.RepositoryWorkspaceId.Value)
        {
            return SyncPullRequestResult.NotFound("Execution not found.");
        }

        if (execution.PullRequestStatus != ExecutionPullRequestStatus.Open || !execution.PullRequestNumber.HasValue)
        {
            return SyncPullRequestResult.Conflict("Execution does not have an open pull request to synchronize.");
        }

        if (string.IsNullOrWhiteSpace(execution.RemoteCommitSha))
        {
            return SyncPullRequestResult.Conflict("Execution branch is not pushed remotely.");
        }

        var now = DateTime.UtcNow;

        // Freshness window: if synced less than 10s ago, return current persisted snapshot without calling GitHub
        if (execution.PullRequestLastSyncedAt.HasValue && (now - execution.PullRequestLastSyncedAt.Value) < FreshnessWindow)
        {
            return SyncPullRequestResult.Ok(MapToResponseDto(execution));
        }

        // Acquire sync lease
        var attemptId = Guid.NewGuid();
        var claimed = await _executionRepository
            .TryClaimPullRequestSyncLeaseAsync(execution.Id, attemptId, now, cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            var reclaimed = await _executionRepository
                .TryReclaimStalePullRequestSyncLeaseAsync(execution.Id, attemptId, now, LeaseTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!reclaimed)
            {
                return SyncPullRequestResult.Conflict("A pull request synchronization operation is currently in progress.");
            }
        }

        // Execute read-only GitHub sync
        var syncResult = await _githubSyncService
            .SyncPullRequestAndCiAsync(execution, bypassFreshnessCache: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!syncResult.Success)
        {
            await _executionRepository
                .ReleasePullRequestSyncLeaseAsync(execution.Id, attemptId, now, cancellationToken)
                .ConfigureAwait(false);

            var fallbackResponse = MapToResponseDto(execution, syncResult.ErrorMessage);
            return SyncPullRequestResult.ExternalFailure(syncResult.ErrorMessage ?? "GitHub synchronization failed.", fallbackResponse);
        }

        // Atomically replace snapshot (fails if attemptId was reclaimed by a newer attempt)
        var replaced = await _executionRepository
            .ReplacePullRequestTrackingSnapshotAsync(
                execution.Id,
                attemptId,
                syncResult.RemoteState,
                syncResult.IntegrityStatus,
                syncResult.ClosedAt,
                syncResult.MergedAt,
                syncResult.CiStatus,
                syncResult.Checks,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        if (!replaced)
        {
            return SyncPullRequestResult.Conflict("Synchronization lease was reclaimed by a newer operation.");
        }

        // Reload fresh execution state
        var updatedExecution = await _executionRepository
            .GetByIdAsync(execution.Id, cancellationToken)
            .ConfigureAwait(false);

        return SyncPullRequestResult.Ok(MapToResponseDto(updatedExecution ?? execution));
    }

    public static SyncPullRequestResponseDto MapToResponseDto(TaskExecution execution, string? syncError = null)
    {
        var checks = (execution.CiChecks ?? Array.Empty<ExecutionCiCheck>())
            .Select(c => new ExecutionCiCheckDto(
                Id: c.Id,
                ExternalId: c.ExternalId,
                Name: c.Name,
                Source: c.Source,
                CheckType: c.CheckType.ToString(),
                Status: c.Status,
                Conclusion: c.Conclusion,
                StartedAt: c.StartedAt,
                CompletedAt: c.CompletedAt))
            .ToList();

        return new SyncPullRequestResponseDto(
            ExecutionId: execution.Id,
            PullRequestNumber: execution.PullRequestNumber,
            PullRequestUrl: execution.PullRequestUrl,
            PullRequestRemoteState: execution.PullRequestRemoteState.ToString(),
            PullRequestIntegrityStatus: execution.PullRequestIntegrityStatus.ToString(),
            HeadCommitSha: execution.RemoteCommitSha ?? string.Empty,
            CiStatus: execution.CiStatus.ToString(),
            CheckCount: checks.Count,
            Checks: checks,
            LastSyncedAt: execution.PullRequestLastSyncedAt,
            SyncError: syncError);
    }
}
