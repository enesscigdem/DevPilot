using DevPilot.Application.AiProviders;
using DevPilot.Application.Executions.Commands.ProcessExecution;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.VerifyExecution;

public sealed record VerifyExecutionCommand(Guid ExecutionId, Guid? RepositoryWorkspaceId = null);

public enum VerifyExecutionResultStatus
{
    Accepted,
    NotFound,
    Conflict,
    Failed
}

public sealed class VerifyExecutionResult
{
    public VerifyExecutionResultStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public bool Skipped { get; set; }

    public static VerifyExecutionResult Accepted() =>
        new() { Status = VerifyExecutionResultStatus.Accepted };

    public static VerifyExecutionResult NotFound(string message = "Execution not found.") =>
        new() { Status = VerifyExecutionResultStatus.NotFound, ErrorMessage = message };

    public static VerifyExecutionResult Conflict(string message) =>
        new() { Status = VerifyExecutionResultStatus.Conflict, ErrorMessage = message };

    public static VerifyExecutionResult Failed(string message) =>
        new() { Status = VerifyExecutionResultStatus.Failed, ErrorMessage = message };

    public static VerifyExecutionResult Skip() =>
        new() { Status = VerifyExecutionResultStatus.Accepted, Skipped = true };
}

public interface IVerifyExecutionCommandHandler
{
    Task<VerifyExecutionResult> RequestAsync(
        VerifyExecutionCommand command,
        CancellationToken cancellationToken = default);

    Task<VerifyExecutionResult> ExecuteAsync(
        Guid executionId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs build and test again on the execution's existing worktree. It does not generate code and it does
/// not commit, push, or merge. A previous review approval is cleared when the verdict or the change set moved.
/// </summary>
public sealed class VerifyExecutionCommandHandler : IVerifyExecutionCommandHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionVerificationRerunStore _rerunStore;
    private readonly IExecutionProcessor _processor;
    private readonly IExecutionDispatcher _dispatcher;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly IExecutionActivityRepository _activityRepository;
    private readonly IExecutionHeartbeatService _heartbeatService;
    private readonly IExecutionCancellationRegistry _cancellationRegistry;
    private readonly ILogger<VerifyExecutionCommandHandler> _logger;
    private readonly IExecutionVerificationSnapshotRecorder? _snapshotRecorder;
    private readonly IExecutionChangeFingerprintCalculator? _fingerprintCalculator;
    private readonly IAiExecutionContext? _aiContext;

    public VerifyExecutionCommandHandler(
        IExecutionRepository executionRepository,
        IExecutionVerificationRerunStore rerunStore,
        IExecutionProcessor processor,
        IExecutionDispatcher dispatcher,
        IExecutionActivityRecorder activityRecorder,
        IExecutionActivityRepository activityRepository,
        IExecutionHeartbeatService heartbeatService,
        IExecutionCancellationRegistry cancellationRegistry,
        ILogger<VerifyExecutionCommandHandler> logger,
        IExecutionVerificationSnapshotRecorder? snapshotRecorder = null,
        IExecutionChangeFingerprintCalculator? fingerprintCalculator = null,
        IAiExecutionContext? aiContext = null)
    {
        _executionRepository = executionRepository;
        _rerunStore = rerunStore;
        _processor = processor;
        _dispatcher = dispatcher;
        _activityRecorder = activityRecorder;
        _activityRepository = activityRepository;
        _heartbeatService = heartbeatService;
        _cancellationRegistry = cancellationRegistry;
        _logger = logger;
        _snapshotRecorder = snapshotRecorder;
        _fingerprintCalculator = fingerprintCalculator;
        _aiContext = aiContext;
    }

    public static bool ApprovalIsStale(
        bool wasApproved,
        string? previousOutcome,
        string? newOutcome,
        string? approvedFingerprint,
        bool fingerprintSucceeded,
        string? recomputedFingerprint)
    {
        if (!wasApproved)
        {
            return false;
        }

        if (!string.Equals(previousOutcome ?? string.Empty, newOutcome ?? string.Empty, StringComparison.Ordinal))
        {
            return true;
        }

        if (!fingerprintSucceeded || string.IsNullOrWhiteSpace(recomputedFingerprint))
        {
            return true;
        }

        return !string.Equals(approvedFingerprint, recomputedFingerprint, StringComparison.Ordinal);
    }

    public async Task<VerifyExecutionResult> RequestAsync(
        VerifyExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return VerifyExecutionResult.NotFound();
        }

        if (command.RepositoryWorkspaceId.HasValue &&
            execution.DevelopmentTask?.RepositoryWorkspaceId != command.RepositoryWorkspaceId.Value)
        {
            return VerifyExecutionResult.NotFound();
        }

        var rejection = DescribeWhyVerificationCannotStart(execution);
        if (rejection != null)
        {
            return VerifyExecutionResult.Conflict(rejection);
        }

        if (await _executionRepository
                .HasActiveExecutionForTaskAsync(execution.DevelopmentTaskId, cancellationToken)
                .ConfigureAwait(false))
        {
            return VerifyExecutionResult.Conflict("Another execution for this task is already active.");
        }

        var leaseToken = Guid.NewGuid();
        var claimed = await _rerunStore
            .ClaimCompletedForVerificationAsync(execution.Id, leaseToken, cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            return VerifyExecutionResult.Conflict("Verification could not be started for this execution.");
        }

        try
        {
            _dispatcher.EnqueueVerifyExecution(execution.Id, leaseToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VerifyExecution: failed to enqueue re-verification for {ExecutionId}.", execution.Id);
            await _rerunStore
                .RestoreCompletedAfterVerificationDispatchFailureAsync(execution.Id, leaseToken, CancellationToken.None)
                .ConfigureAwait(false);
            return VerifyExecutionResult.Failed("Failed to enqueue verification.");
        }

        return VerifyExecutionResult.Accepted();
    }

    public async Task<VerifyExecutionResult> ExecuteAsync(
        Guid executionId,
        Guid leaseToken,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(executionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null ||
            execution.Status != TaskExecutionStatus.Running ||
            execution.LeaseToken != leaseToken)
        {
            _logger.LogInformation(
                "VerifyExecution: execution {ExecutionId} is not held by lease {LeaseToken} — skipping.",
                executionId,
                leaseToken);
            return VerifyExecutionResult.Skip();
        }

        var wasApproved = execution.ReviewStatus == ExecutionReviewStatus.Approved;
        var previousOutcome = execution.VerificationOutcome;
        var approvedFingerprint = execution.ApprovedChangeFingerprint;
        var workspacePath = execution.WorkspacePath;

        if (_aiContext is not null)
        {
            _aiContext.PinnedModelId = execution.PinnedAiModelId;
        }

        var task = execution.DevelopmentTask;
        var workspace = task?.RepositoryWorkspace;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var executionToken = _cancellationRegistry.Register(executionId, linkedCts.Token);
        await using var heartbeat = _heartbeatService.StartHeartbeat(
            executionId,
            leaseToken,
            interval: TimeSpan.FromSeconds(15),
            leaseDuration: TimeSpan.FromSeconds(45),
            linkedCts);

        try
        {
            if (await IsCancelledAsync(executionId, executionToken).ConfigureAwait(false))
            {
                return await FinishCancellationAsync(executionId, leaseToken, wasApproved, previousOutcome, approvedFingerprint, workspacePath)
                    .ConfigureAwait(false);
            }

            try
            {
                var context = new ExecutionProcessingContext(
                    ExecutionId: executionId,
                    TaskId: task?.Id ?? execution.DevelopmentTaskId,
                    TaskTitle: task?.Title ?? string.Empty,
                    TaskDescription: task?.Description ?? string.Empty,
                    AcceptanceCriteria: task?.AcceptanceCriteria,
                    WorkspaceId: workspace?.Id ?? Guid.Empty,
                    WorkspaceLocalPath: workspace?.LocalPath ?? execution.WorkspacePath ?? string.Empty,
                    ImpactAnalysisSummary: string.Empty,
                    RepositoryOwner: workspace?.Owner,
                    RepositoryName: workspace?.Repository,
                    BaseBranch: workspace?.Branch,
                    VerifyOnlyWorkspace: new ExecutionVerifyOnlyWorkspace(
                        execution.WorkspacePath ?? string.Empty,
                        execution.BranchName ?? string.Empty,
                        execution.BaseCommitSha));

                await _processor.ProcessAsync(context, executionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (executionToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return await FinishCancellationAsync(executionId, leaseToken, wasApproved, previousOutcome, approvedFingerprint, workspacePath)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (await _executionRepository.IsCancellationRequestedAsync(executionId, CancellationToken.None).ConfigureAwait(false))
                {
                    return await FinishCancellationAsync(executionId, leaseToken, wasApproved, previousOutcome, approvedFingerprint, workspacePath)
                        .ConfigureAwait(false);
                }

                _logger.LogError(ex, "VerifyExecution: re-verification failed for execution {ExecutionId}.", executionId);
                await SafeRecordActivityAsync(
                    executionId,
                    ExecutionStage.Execution,
                    ExecutionActivityStatus.Failed,
                    $"Re-verification failed: {ProcessExecutionCommandHandler.SanitizeErrorMessage(ex.Message)}")
                    .ConfigureAwait(false);
            }

            if (await IsCancelledAsync(executionId, executionToken).ConfigureAwait(false))
            {
                return await FinishCancellationAsync(executionId, leaseToken, wasApproved, previousOutcome, approvedFingerprint, workspacePath)
                    .ConfigureAwait(false);
            }

            var completed = await _executionRepository
                .CompleteWithLeaseAsync(executionId, leaseToken, CancellationToken.None)
                .ConfigureAwait(false);

            if (!completed)
            {
                return VerifyExecutionResult.Failed("Execution lease lost during re-verification.");
            }

            if (_snapshotRecorder != null)
            {
                await _snapshotRecorder.RecordAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            }

            await InvalidateApprovalIfStaleAsync(
                executionId,
                wasApproved,
                previousOutcome,
                approvedFingerprint,
                workspacePath,
                CancellationToken.None).ConfigureAwait(false);

            return VerifyExecutionResult.Accepted();
        }
        finally
        {
            _cancellationRegistry.Unregister(executionId);
        }
    }

    /// <summary>Null when verification may start.</summary>
    public static string? DescribeWhyVerificationCannotStart(Domain.Entities.TaskExecution execution)
    {
        if (execution.Status != TaskExecutionStatus.Completed)
        {
            return $"Only a completed execution can be verified again (status is {execution.Status}).";
        }

        if (execution.CommitStatus != ExecutionCommitStatus.None ||
            execution.PushStatus != ExecutionPushStatus.None ||
            execution.PullRequestStatus != ExecutionPullRequestStatus.None ||
            execution.MergeStatus != ExecutionMergeStatus.None)
        {
            return "Verification cannot run after commit, push, or merge.";
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return "The execution has no worktree to verify.";
        }

        if (!Directory.Exists(execution.WorkspacePath))
        {
            return "The execution worktree no longer exists.";
        }

        return null;
    }

    private async Task<VerifyExecutionResult> FinishCancellationAsync(
        Guid executionId,
        Guid leaseToken,
        bool wasApproved,
        string? previousOutcome,
        string? approvedFingerprint,
        string? workspacePath)
    {
        await InvalidateApprovalIfStaleAsync(
            executionId,
            wasApproved,
            previousOutcome,
            approvedFingerprint,
            workspacePath,
            CancellationToken.None).ConfigureAwait(false);

        await _executionRepository
            .AcknowledgeCancellationWithLeaseAsync(executionId, leaseToken, CancellationToken.None)
            .ConfigureAwait(false);

        await SafeRecordActivityAsync(
            executionId,
            ExecutionStage.Execution,
            ExecutionActivityStatus.Completed,
            "Re-verification cancelled.").ConfigureAwait(false);

        return VerifyExecutionResult.Accepted();
    }

    private async Task InvalidateApprovalIfStaleAsync(
        Guid executionId,
        bool wasApproved,
        string? previousOutcome,
        string? approvedFingerprint,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        if (!wasApproved)
        {
            return;
        }

        var activities = await _activityRepository
            .GetByExecutionIdAsync(executionId, cancellationToken)
            .ConfigureAwait(false);
        var execution = await _executionRepository
            .GetByIdAsync(executionId, cancellationToken)
            .ConfigureAwait(false);
        var newOutcome = execution == null
            ? previousOutcome
            : ExecutionVerificationEvaluator.DetermineOutcome(execution, activities).ToString();

        var fingerprintSucceeded = false;
        string? recomputed = null;
        if (_fingerprintCalculator != null && !string.IsNullOrWhiteSpace(workspacePath))
        {
            var fingerprint = await _fingerprintCalculator
                .ComputeFingerprintAsync(workspacePath, cancellationToken)
                .ConfigureAwait(false);
            fingerprintSucceeded = fingerprint.Success && !string.IsNullOrWhiteSpace(fingerprint.Fingerprint);
            recomputed = fingerprint.Fingerprint;
        }

        if (!ApprovalIsStale(wasApproved, previousOutcome, newOutcome, approvedFingerprint, fingerprintSucceeded, recomputed))
        {
            return;
        }

        var cleared = await _rerunStore
            .TryInvalidateReviewApprovalAsync(executionId, cancellationToken)
            .ConfigureAwait(false);

        if (!cleared)
        {
            return;
        }

        await SafeRecordActivityAsync(
            executionId,
            ExecutionStage.Execution,
            ExecutionActivityStatus.Completed,
            "Review approval cleared because re-verification changed the result.",
            new ExecutionActivityMetadata(EventKind: "ReviewInvalidated")).ConfigureAwait(false);
    }

    private async Task<bool> IsCancelledAsync(Guid executionId, CancellationToken executionToken) =>
        executionToken.IsCancellationRequested ||
        await _executionRepository.IsCancellationRequestedAsync(executionId, CancellationToken.None).ConfigureAwait(false);

    private async Task SafeRecordActivityAsync(
        Guid executionId,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        ExecutionActivityMetadata? metadata = null)
    {
        try
        {
            await _activityRecorder
                .RecordActivityAsync(executionId, stage, status, message, metadata, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VerifyExecution: failed to record activity for {ExecutionId}.", executionId);
        }
    }
}
