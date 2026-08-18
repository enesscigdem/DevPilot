using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.RejectExecutionReview;

public sealed record RejectExecutionReviewCommand(
    Guid ExecutionId,
    string? Reason,
    Guid? RepositoryWorkspaceId = null);

public enum RejectExecutionReviewResultStatus
{
    Success,
    BadRequest,
    NotFound,
    Conflict
}

public sealed class RejectExecutionReviewResult
{
    public RejectExecutionReviewResultStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    public ExecutionReviewDecisionDto? Decision { get; private set; }

    public static RejectExecutionReviewResult Ok(ExecutionReviewDecisionDto decision) =>
        new() { Status = RejectExecutionReviewResultStatus.Success, Decision = decision };

    public static RejectExecutionReviewResult BadRequest(string message) =>
        new() { Status = RejectExecutionReviewResultStatus.BadRequest, ErrorMessage = message };

    public static RejectExecutionReviewResult NotFound(string message) =>
        new() { Status = RejectExecutionReviewResultStatus.NotFound, ErrorMessage = message };

    public static RejectExecutionReviewResult Conflict(string message) =>
        new() { Status = RejectExecutionReviewResultStatus.Conflict, ErrorMessage = message };
}

public interface IRejectExecutionReviewCommandHandler
{
    Task<RejectExecutionReviewResult> HandleAsync(
        RejectExecutionReviewCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class RejectExecutionReviewCommandHandler : IRejectExecutionReviewCommandHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly ILogger<RejectExecutionReviewCommandHandler> _logger;

    public RejectExecutionReviewCommandHandler(
        IExecutionRepository executionRepository,
        IExecutionWorkspaceManager workspaceManager,
        IExecutionActivityRecorder activityRecorder,
        ILogger<RejectExecutionReviewCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _workspaceManager = workspaceManager;
        _activityRecorder = activityRecorder;
        _logger = logger;
    }

    public async Task<RejectExecutionReviewResult> HandleAsync(
        RejectExecutionReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = NormalizeReason(command.Reason);

        if (normalizedReason is not null && normalizedReason.Length > 1000)
        {
            return RejectExecutionReviewResult.BadRequest(
                "Rejection reason cannot exceed 1000 characters.");
        }

        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return RejectExecutionReviewResult.NotFound("Execution not found.");
        }

        if (command.RepositoryWorkspaceId.HasValue &&
            execution.DevelopmentTask?.RepositoryWorkspaceId != command.RepositoryWorkspaceId.Value)
        {
            return RejectExecutionReviewResult.NotFound("Execution not found.");
        }

        if (execution.Status != TaskExecutionStatus.Completed)
        {
            return RejectExecutionReviewResult.Conflict(
                $"Execution is currently '{execution.Status}' and cannot be reviewed.");
        }

        if (execution.ReviewStatus != ExecutionReviewStatus.Pending)
        {
            return RejectExecutionReviewResult.Conflict(
                $"Review has already been decided as '{execution.ReviewStatus}'.");
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return RejectExecutionReviewResult.Conflict(
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
            return RejectExecutionReviewResult.Conflict(
                $"Execution workspace verification failed: {verificationResult.ErrorMessage}");
        }

        var decidedAt = DateTime.UtcNow;
        var updated = await _executionRepository
            .TrySetReviewDecisionAsync(
                execution.Id,
                ExecutionReviewStatus.Pending,
                ExecutionReviewStatus.Rejected,
                decidedAt,
                rejectionReason: normalizedReason,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!updated)
        {
            var reloaded = await _executionRepository
                .GetByIdAsync(execution.Id, cancellationToken)
                .ConfigureAwait(false);

            if (reloaded is null)
            {
                return RejectExecutionReviewResult.NotFound("Execution not found.");
            }

            if (reloaded.Status != TaskExecutionStatus.Completed)
            {
                return RejectExecutionReviewResult.Conflict(
                    $"Execution status changed to '{reloaded.Status}'.");
            }

            return RejectExecutionReviewResult.Conflict(
                $"Review has already been decided as '{reloaded.ReviewStatus}'.");
        }

        try
        {
            await _activityRecorder.RecordActivityAsync(
                execution.Id,
                ExecutionStage.Review,
                ExecutionActivityStatus.Rejected,
                "Review rejected",
                metadata: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record Review rejected activity for execution {ExecutionId}", execution.Id);
        }

        var decision = new ExecutionReviewDecisionDto(
            ExecutionId: execution.Id,
            ReviewStatus: ExecutionReviewStatus.Rejected.ToString(),
            DecidedAt: decidedAt,
            RejectionReason: normalizedReason);

        return RejectExecutionReviewResult.Ok(decision);
    }

    private static string? NormalizeReason(string? rawReason)
    {
        if (string.IsNullOrWhiteSpace(rawReason))
        {
            return null;
        }

        // Remove control characters (except common spaces/newlines) and trim
        var cleaned = new string(rawReason.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }
}
