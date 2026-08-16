using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.CommitExecution;

public sealed record CommitExecutionCommand(Guid ExecutionId);

public enum CommitExecutionResultStatus
{
    Success,
    NotFound,
    Conflict
}

public sealed record CommitExecutionResponseDto(
    Guid ExecutionId,
    string BranchName,
    string CommitStatus,
    string CommitSha,
    DateTime CommittedAt);

public sealed class CommitExecutionResult
{
    public CommitExecutionResultStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    public CommitExecutionResponseDto? Response { get; private set; }

    public static CommitExecutionResult Ok(CommitExecutionResponseDto response) =>
        new() { Status = CommitExecutionResultStatus.Success, Response = response };

    public static CommitExecutionResult NotFound(string message) =>
        new() { Status = CommitExecutionResultStatus.NotFound, ErrorMessage = message };

    public static CommitExecutionResult Conflict(string message) =>
        new() { Status = CommitExecutionResultStatus.Conflict, ErrorMessage = message };
}

public interface ICommitExecutionCommandHandler
{
    Task<CommitExecutionResult> HandleAsync(
        CommitExecutionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class CommitExecutionCommandHandler : ICommitExecutionCommandHandler
{
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(2);

    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IExecutionGitCommitService _gitCommitService;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly ILogger<CommitExecutionCommandHandler> _logger;

    public CommitExecutionCommandHandler(
        IExecutionRepository executionRepository,
        IExecutionWorkspaceManager workspaceManager,
        IExecutionGitCommitService gitCommitService,
        IExecutionActivityRecorder activityRecorder,
        ILogger<CommitExecutionCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _workspaceManager = workspaceManager;
        _gitCommitService = gitCommitService;
        _activityRecorder = activityRecorder;
        _logger = logger;
    }

    public async Task<CommitExecutionResult> HandleAsync(
        CommitExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return CommitExecutionResult.NotFound("Execution not found.");
        }

        if (execution.Status != TaskExecutionStatus.Completed)
        {
            return CommitExecutionResult.Conflict(
                $"Execution is currently '{execution.Status}' and cannot be committed.");
        }

        if (execution.ReviewStatus != ExecutionReviewStatus.Approved)
        {
            return CommitExecutionResult.Conflict(
                $"Execution review status is '{execution.ReviewStatus}' and cannot be committed.");
        }

        if (string.IsNullOrWhiteSpace(execution.ApprovedChangeFingerprint))
        {
            return CommitExecutionResult.Conflict(
                "Execution review has no approved change fingerprint.");
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return CommitExecutionResult.Conflict(
                "Execution workspace path or branch name is not configured.");
        }

        // Check if already committed
        if (execution.CommitStatus == ExecutionCommitStatus.Committed && !string.IsNullOrEmpty(execution.CommitSha))
        {
            return CommitExecutionResult.Ok(new CommitExecutionResponseDto(
                ExecutionId: execution.Id,
                BranchName: execution.BranchName,
                CommitStatus: ExecutionCommitStatus.Committed.ToString(),
                CommitSha: execution.CommitSha,
                CommittedAt: execution.CommittedAt ?? DateTime.UtcNow));
        }

        var verificationResult = await _workspaceManager
            .VerifyWorkspaceStateAsync(
                execution.WorkspacePath,
                execution.BranchName,
                requireClean: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!verificationResult.IsValid)
        {
            return CommitExecutionResult.Conflict(
                $"Execution workspace verification failed: {verificationResult.ErrorMessage}");
        }

        Guid attemptId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        if (execution.CommitStatus == ExecutionCommitStatus.None || execution.CommitStatus == ExecutionCommitStatus.Failed)
        {
            attemptId = Guid.NewGuid();
            // Obtain current HEAD as baseCommitSha for a NEW claim
            var baseCommitSha = await GetCurrentHeadShaAsync(execution.WorkspacePath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(baseCommitSha))
            {
                return CommitExecutionResult.Conflict("Failed to determine current workspace HEAD commit.");
            }

            var claimed = await _executionRepository
                .TryClaimNewCommitLeaseAsync(execution.Id, attemptId, now, baseCommitSha, cancellationToken)
                .ConfigureAwait(false);

            if (!claimed)
            {
                return CommitExecutionResult.Conflict("Concurrent commit request detected. Please retry.");
            }

            execution.BaseCommitSha = baseCommitSha;
            execution.CommitAttemptId = attemptId;
            execution.CommitStatus = ExecutionCommitStatus.InProgress;
        }
        else if (execution.CommitStatus == ExecutionCommitStatus.InProgress)
        {
            // Check if lease is fresh
            var isFresh = execution.CommitClaimedAt.HasValue && (now - execution.CommitClaimedAt.Value) < LeaseTimeout;
            if (isFresh)
            {
                return CommitExecutionResult.Conflict("A commit operation for this execution is currently in progress.");
            }

            // Stale lease: DO NOT compute new BaseCommitSha from current HEAD! Preserve existing execution.BaseCommitSha.
            attemptId = Guid.NewGuid();
            var reclaimed = await _executionRepository
                .TryReclaimStaleCommitLeaseAsync(execution.Id, attemptId, now, LeaseTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!reclaimed)
            {
                return CommitExecutionResult.Conflict("Failed to reclaim stale commit lease.");
            }

            execution.CommitAttemptId = attemptId;
        }

        // Record Commit started activity only after lease is acquired
        try
        {
            await _activityRecorder.RecordActivityAsync(
                execution.Id,
                ExecutionStage.Commit,
                ExecutionActivityStatus.Started,
                "Commit started",
                metadata: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record Commit started activity for execution {ExecutionId}", execution.Id);
        }

        var taskTitle = execution.DevelopmentTask?.Title ?? "update changes";
        var commitResult = await _gitCommitService
            .CommitApprovedExecutionAsync(execution, taskTitle, execution.CommitAttemptId ?? Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);

        if (!commitResult.Success || string.IsNullOrEmpty(commitResult.CommitSha))
        {
            return CommitExecutionResult.Conflict(commitResult.ErrorMessage ?? "Local commit execution failed.");
        }

        var committedAt = commitResult.CommittedAt ?? DateTime.UtcNow;
        await _executionRepository
            .SetCommitCompletedAsync(execution.Id, attemptId, commitResult.CommitSha, committedAt, cancellationToken)
            .ConfigureAwait(false);

        // Record Commit completed activity after SetCommitCompletedAsync succeeds
        try
        {
            await _activityRecorder.RecordActivityAsync(
                execution.Id,
                ExecutionStage.Commit,
                ExecutionActivityStatus.Completed,
                "Commit completed",
                metadata: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record Commit completed activity for execution {ExecutionId}", execution.Id);
        }

        return CommitExecutionResult.Ok(new CommitExecutionResponseDto(
            ExecutionId: execution.Id,
            BranchName: execution.BranchName,
            CommitStatus: ExecutionCommitStatus.Committed.ToString(),
            CommitSha: commitResult.CommitSha,
            CommittedAt: commitResult.CommittedAt ?? DateTime.UtcNow));
    }

    private static async Task<string?> GetCurrentHeadShaAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspacePath
        };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("HEAD");

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        try
        {
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
