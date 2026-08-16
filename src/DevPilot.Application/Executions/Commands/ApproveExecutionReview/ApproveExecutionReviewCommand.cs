using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.ApproveExecutionReview;

public sealed record ApproveExecutionReviewCommand(Guid ExecutionId, string ExpectedChangeFingerprint);

public enum ApproveExecutionReviewResultStatus
{
    Success,
    NotFound,
    Conflict
}

public sealed class ApproveExecutionReviewResult
{
    public ApproveExecutionReviewResultStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    public ExecutionReviewDecisionDto? Decision { get; private set; }

    public static ApproveExecutionReviewResult Ok(ExecutionReviewDecisionDto decision) =>
        new() { Status = ApproveExecutionReviewResultStatus.Success, Decision = decision };

    public static ApproveExecutionReviewResult NotFound(string message) =>
        new() { Status = ApproveExecutionReviewResultStatus.NotFound, ErrorMessage = message };

    public static ApproveExecutionReviewResult Conflict(string message) =>
        new() { Status = ApproveExecutionReviewResultStatus.Conflict, ErrorMessage = message };
}

public interface IApproveExecutionReviewCommandHandler
{
    Task<ApproveExecutionReviewResult> HandleAsync(
        ApproveExecutionReviewCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class ApproveExecutionReviewCommandHandler : IApproveExecutionReviewCommandHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IExecutionChangeFingerprintCalculator _fingerprintCalculator;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly ILogger<ApproveExecutionReviewCommandHandler> _logger;

    public ApproveExecutionReviewCommandHandler(
        IExecutionRepository executionRepository,
        IExecutionWorkspaceManager workspaceManager,
        IExecutionChangeFingerprintCalculator fingerprintCalculator,
        IExecutionActivityRecorder activityRecorder,
        ILogger<ApproveExecutionReviewCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _workspaceManager = workspaceManager;
        _fingerprintCalculator = fingerprintCalculator;
        _activityRecorder = activityRecorder;
        _logger = logger;
    }

    public async Task<ApproveExecutionReviewResult> HandleAsync(
        ApproveExecutionReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return ApproveExecutionReviewResult.NotFound("Execution not found.");
        }

        if (execution.Status != TaskExecutionStatus.Completed)
        {
            return ApproveExecutionReviewResult.Conflict(
                $"Execution is currently '{execution.Status}' and cannot be reviewed.");
        }

        if (execution.ReviewStatus != ExecutionReviewStatus.Pending)
        {
            return ApproveExecutionReviewResult.Conflict(
                $"Review has already been decided as '{execution.ReviewStatus}'.");
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return ApproveExecutionReviewResult.Conflict(
                "Execution workspace path or branch name is not configured.");
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
            return ApproveExecutionReviewResult.Conflict(
                $"Execution workspace verification failed: {verificationResult.ErrorMessage}");
        }

        // Recompute current worktree fingerprint to compare against expectedChangeFingerprint
        var fingerprintResult = await _fingerprintCalculator
            .ComputeFingerprintAsync(execution.WorkspacePath, cancellationToken)
            .ConfigureAwait(false);

        if (!fingerprintResult.Success || string.IsNullOrEmpty(fingerprintResult.Fingerprint))
        {
            return ApproveExecutionReviewResult.Conflict(
                $"Failed to compute worktree fingerprint: {fingerprintResult.ErrorMessage}");
        }

        if (fingerprintResult.HasSensitiveFiles)
        {
            return ApproveExecutionReviewResult.Conflict(
                "Execution worktree contains sensitive files that cannot be approved or committed.");
        }

        if (fingerprintResult.ChangedFileCount == 0)
        {
            return ApproveExecutionReviewResult.Conflict(
                "Execution worktree has no changed files to approve.");
        }

        if (!string.Equals(fingerprintResult.Fingerprint, command.ExpectedChangeFingerprint, StringComparison.Ordinal))
        {
            return ApproveExecutionReviewResult.Conflict(
                "Review changes have changed. Please refresh and review again.");
        }

        var decidedAt = DateTime.UtcNow;
        var updated = await _executionRepository
            .TrySetReviewDecisionWithFingerprintAsync(
                execution.Id,
                ExecutionReviewStatus.Pending,
                ExecutionReviewStatus.Approved,
                decidedAt,
                fingerprintResult.Fingerprint,
                rejectionReason: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!updated)
        {
            var reloaded = await _executionRepository
                .GetByIdAsync(execution.Id, cancellationToken)
                .ConfigureAwait(false);

            if (reloaded is null)
            {
                return ApproveExecutionReviewResult.NotFound("Execution not found.");
            }

            if (reloaded.Status != TaskExecutionStatus.Completed)
            {
                return ApproveExecutionReviewResult.Conflict(
                    $"Execution status changed to '{reloaded.Status}'.");
            }

            return ApproveExecutionReviewResult.Conflict(
                $"Review has already been decided as '{reloaded.ReviewStatus}'.");
        }

        try
        {
            await _activityRecorder.RecordActivityAsync(
                execution.Id,
                ExecutionStage.Review,
                ExecutionActivityStatus.Completed,
                "Review approved",
                metadata: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record Review approved activity for execution {ExecutionId}", execution.Id);
        }

        var decision = new ExecutionReviewDecisionDto(
            ExecutionId: execution.Id,
            ReviewStatus: ExecutionReviewStatus.Approved.ToString(),
            DecidedAt: decidedAt,
            RejectionReason: null);

        return ApproveExecutionReviewResult.Ok(decision);
    }
}
