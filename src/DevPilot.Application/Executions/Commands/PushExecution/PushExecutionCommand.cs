using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.PushExecution;

public sealed record PushExecutionCommand(Guid ExecutionId, Guid? RepositoryWorkspaceId = null);

public enum PushExecutionResultStatus
{
    Success,
    NotFound,
    Conflict
}

public sealed record PushExecutionResponseDto(
    Guid ExecutionId,
    string BranchName,
    string PushStatus,
    string RemoteCommitSha,
    DateTime PushedAt);

public sealed class PushExecutionResult
{
    public PushExecutionResultStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    public PushExecutionResponseDto? Response { get; private set; }

    public static PushExecutionResult Ok(PushExecutionResponseDto response) =>
        new() { Status = PushExecutionResultStatus.Success, Response = response };

    public static PushExecutionResult NotFound(string message) =>
        new() { Status = PushExecutionResultStatus.NotFound, ErrorMessage = message };

    public static PushExecutionResult Conflict(string message) =>
        new() { Status = PushExecutionResultStatus.Conflict, ErrorMessage = message };
}

public interface IPushExecutionCommandHandler
{
    Task<PushExecutionResult> HandleAsync(
        PushExecutionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class PushExecutionCommandHandler : IPushExecutionCommandHandler
{
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(2);

    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionGitPushService _gitPushService;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly ILogger<PushExecutionCommandHandler> _logger;

    public PushExecutionCommandHandler(
        IExecutionRepository executionRepository,
        IExecutionGitPushService gitPushService,
        IExecutionActivityRecorder activityRecorder,
        ILogger<PushExecutionCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _gitPushService = gitPushService;
        _activityRecorder = activityRecorder;
        _logger = logger;
    }

    public async Task<PushExecutionResult> HandleAsync(
        PushExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return PushExecutionResult.NotFound("Execution not found.");
        }

        if (command.RepositoryWorkspaceId.HasValue &&
            execution.DevelopmentTask?.RepositoryWorkspaceId != command.RepositoryWorkspaceId.Value)
        {
            return PushExecutionResult.NotFound("Execution not found.");
        }

        // Precondition checks — DO NOT alter DB push status for invalid lifecycle preconditions
        if (execution.Status != TaskExecutionStatus.Completed)
        {
            return PushExecutionResult.Conflict(
                $"Execution status is '{execution.Status}' and cannot be pushed.");
        }

        if (execution.ReviewStatus != ExecutionReviewStatus.Approved)
        {
            return PushExecutionResult.Conflict(
                $"Execution review status is '{execution.ReviewStatus}' and cannot be pushed.");
        }

        if (execution.CommitStatus != ExecutionCommitStatus.Committed || string.IsNullOrWhiteSpace(execution.CommitSha))
        {
            return PushExecutionResult.Conflict(
                "Execution is not committed locally and cannot be pushed.");
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return PushExecutionResult.Conflict(
                "Execution workspace path or branch name is missing.");
        }

        // Check idempotency on already Pushed state
        if (execution.PushStatus == ExecutionPushStatus.Pushed && !string.IsNullOrEmpty(execution.RemoteCommitSha))
        {
            return PushExecutionResult.Ok(new PushExecutionResponseDto(
                ExecutionId: execution.Id,
                BranchName: execution.RemoteBranchName ?? execution.BranchName,
                PushStatus: ExecutionPushStatus.Pushed.ToString(),
                RemoteCommitSha: execution.RemoteCommitSha,
                PushedAt: execution.PushedAt ?? DateTime.UtcNow));
        }

        Guid attemptId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        if (execution.PushStatus == ExecutionPushStatus.None || execution.PushStatus == ExecutionPushStatus.Failed)
        {
            attemptId = Guid.NewGuid();
            var claimed = await _executionRepository
                .TryClaimNewPushLeaseAsync(execution.Id, attemptId, now, cancellationToken)
                .ConfigureAwait(false);

            if (!claimed)
            {
                return PushExecutionResult.Conflict("Concurrent push request detected. Please retry.");
            }

            execution.PushAttemptId = attemptId;
            execution.PushStatus = ExecutionPushStatus.InProgress;
        }
        else if (execution.PushStatus == ExecutionPushStatus.InProgress)
        {
            var isFresh = execution.PushClaimedAt.HasValue && (now - execution.PushClaimedAt.Value) < LeaseTimeout;
            if (isFresh)
            {
                return PushExecutionResult.Conflict("A push operation for this execution is currently in progress.");
            }

            attemptId = Guid.NewGuid();
            var reclaimed = await _executionRepository
                .TryReclaimStalePushLeaseAsync(execution.Id, attemptId, now, LeaseTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!reclaimed)
            {
                return PushExecutionResult.Conflict("Failed to reclaim stale push lease.");
            }

            execution.PushAttemptId = attemptId;
        }

        // Record Push started activity ONLY after lease is acquired
        try
        {
            await _activityRecorder.RecordActivityAsync(
                execution.Id,
                ExecutionStage.Push,
                ExecutionActivityStatus.Started,
                "Push started",
                metadata: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record Push started activity for execution {ExecutionId}", execution.Id);
        }

        var pushResult = await _gitPushService
            .PushExecutionBranchAsync(execution, execution.PushAttemptId ?? Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);

        if (!pushResult.Success)
        {
            return PushExecutionResult.Conflict(pushResult.ErrorMessage ?? "Remote push execution failed.");
        }

        var pushedAt = pushResult.PushedAt ?? DateTime.UtcNow;
        var remoteBranch = pushResult.RemoteBranchName ?? execution.BranchName;
        var remoteSha = pushResult.RemoteCommitSha ?? execution.CommitSha;

        await _executionRepository
            .SetPushCompletedAsync(execution.Id, attemptId, remoteBranch, remoteSha, pushedAt, cancellationToken)
            .ConfigureAwait(false);

        // Record Push completed activity after SetPushCompletedAsync succeeds
        try
        {
            await _activityRecorder.RecordActivityAsync(
                execution.Id,
                ExecutionStage.Push,
                ExecutionActivityStatus.Completed,
                "Push completed",
                metadata: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record Push completed activity for execution {ExecutionId}", execution.Id);
        }

        return PushExecutionResult.Ok(new PushExecutionResponseDto(
            ExecutionId: execution.Id,
            BranchName: pushResult.RemoteBranchName ?? execution.BranchName,
            PushStatus: ExecutionPushStatus.Pushed.ToString(),
            RemoteCommitSha: pushResult.RemoteCommitSha ?? execution.CommitSha,
            PushedAt: pushedAt));
    }
}
