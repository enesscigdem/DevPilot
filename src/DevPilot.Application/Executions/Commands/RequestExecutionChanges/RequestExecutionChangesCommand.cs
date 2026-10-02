using DevPilot.Application.AiProviders;
using DevPilot.Application.Executions.Commands.ProcessExecution;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.RequestExecutionChanges;

public sealed record RequestExecutionChangesCommand(
    Guid ExecutionId,
    string? Feedback,
    Guid? RepositoryWorkspaceId = null);

public enum RequestExecutionChangesResultStatus
{
    Accepted,
    BadRequest,
    NotFound,
    Conflict,
    Failed,
    Skipped
}

public sealed class RequestExecutionChangesResult
{
    public RequestExecutionChangesResultStatus Status { get; set; }

    public string? ErrorMessage { get; set; }

    public int RevisionNumber { get; set; }

    public static RequestExecutionChangesResult Accepted(int revisionNumber) =>
        new() { Status = RequestExecutionChangesResultStatus.Accepted, RevisionNumber = revisionNumber };

    public static RequestExecutionChangesResult BadRequest(string message) =>
        new() { Status = RequestExecutionChangesResultStatus.BadRequest, ErrorMessage = message };

    public static RequestExecutionChangesResult NotFound(string message = "Execution not found.") =>
        new() { Status = RequestExecutionChangesResultStatus.NotFound, ErrorMessage = message };

    public static RequestExecutionChangesResult Conflict(string message) =>
        new() { Status = RequestExecutionChangesResultStatus.Conflict, ErrorMessage = message };

    public static RequestExecutionChangesResult Failed(string message) =>
        new() { Status = RequestExecutionChangesResultStatus.Failed, ErrorMessage = message };

    public static RequestExecutionChangesResult Skip() =>
        new() { Status = RequestExecutionChangesResultStatus.Skipped };
}

public interface IRequestExecutionChangesCommandHandler
{
    /// <summary>Validates the request, takes the lease, clears the old approval and queues the background fix.</summary>
    Task<RequestExecutionChangesResult> RequestAsync(
        RequestExecutionChangesCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Background part: applies the feedback on the existing worktree, then runs build and test again.</summary>
    Task<RequestExecutionChangesResult> ExecuteAsync(
        Guid executionId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// "Request changes" on a reviewed or delivered execution. The feedback is given to the AI together with the code
/// that already exists, the fix is made on the execution's own branch and worktree, build and test run again, the
/// previous approval is removed (once the code changed) and the result goes back to review. When the reviewer
/// approves it, the normal commit and push update the same pull request; no new execution, branch or pull request
/// is created.
/// </summary>
public sealed class RequestExecutionChangesCommandHandler : IRequestExecutionChangesCommandHandler
{
    public const int MaxFeedbackLength = 2000;

    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionRevisionStore _revisionStore;
    private readonly IExecutionProcessor _processor;
    private readonly IExecutionRevisionDispatcher _dispatcher;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly IExecutionHeartbeatService _heartbeatService;
    private readonly IExecutionCancellationRegistry _cancellationRegistry;
    private readonly ILogger<RequestExecutionChangesCommandHandler> _logger;
    private readonly IExecutionVerificationSnapshotRecorder? _snapshotRecorder;
    private readonly IExecutionChangeFingerprintCalculator? _fingerprintCalculator;
    private readonly IAiExecutionContext? _aiContext;
    private readonly IExecutionWorktreeSnapshotService? _snapshotService;

    public RequestExecutionChangesCommandHandler(
        IExecutionRepository executionRepository,
        IExecutionRevisionStore revisionStore,
        IExecutionProcessor processor,
        IExecutionRevisionDispatcher dispatcher,
        IExecutionActivityRecorder activityRecorder,
        IExecutionHeartbeatService heartbeatService,
        IExecutionCancellationRegistry cancellationRegistry,
        ILogger<RequestExecutionChangesCommandHandler> logger,
        IExecutionVerificationSnapshotRecorder? snapshotRecorder = null,
        IExecutionChangeFingerprintCalculator? fingerprintCalculator = null,
        IAiExecutionContext? aiContext = null,
        IExecutionWorktreeSnapshotService? snapshotService = null)
    {
        _executionRepository = executionRepository;
        _revisionStore = revisionStore;
        _processor = processor;
        _dispatcher = dispatcher;
        _activityRecorder = activityRecorder;
        _heartbeatService = heartbeatService;
        _cancellationRegistry = cancellationRegistry;
        _logger = logger;
        _snapshotRecorder = snapshotRecorder;
        _fingerprintCalculator = fingerprintCalculator;
        _aiContext = aiContext;
        _snapshotService = snapshotService;
    }

    /// <summary>Control characters other than line breaks and tabs are dropped; null when nothing is left.</summary>
    public static string? NormalizeFeedback(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = new string(raw.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Null when the reviewer may ask for a fix on this execution.</summary>
    public static string? DescribeWhyChangesCannotBeRequested(TaskExecution execution)
    {
        if (execution.Status != TaskExecutionStatus.Completed)
        {
            return $"Changes can only be requested on a completed execution (status is {execution.Status}).";
        }

        if (execution.MergeStatus != ExecutionMergeStatus.None ||
            execution.PullRequestRemoteState is ExecutionPullRequestRemoteState.Merged or ExecutionPullRequestRemoteState.Closed)
        {
            return "The pull request is already merged or closed; changes can no longer be requested on this branch.";
        }

        if (execution.CommitStatus == ExecutionCommitStatus.InProgress ||
            execution.PushStatus == ExecutionPushStatus.InProgress ||
            execution.PullRequestStatus == ExecutionPullRequestStatus.InProgress)
        {
            return "A delivery step (commit, push or pull request) is still running. Wait for it to finish first.";
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return "The execution has no worktree to change.";
        }

        if (!Directory.Exists(execution.WorkspacePath))
        {
            return "The execution worktree no longer exists.";
        }

        return null;
    }

    public async Task<RequestExecutionChangesResult> RequestAsync(
        RequestExecutionChangesCommand command,
        CancellationToken cancellationToken = default)
    {
        var feedback = NormalizeFeedback(command.Feedback);
        if (feedback is null)
        {
            return RequestExecutionChangesResult.BadRequest("Describe what should be fixed.");
        }

        if (feedback.Length > MaxFeedbackLength)
        {
            return RequestExecutionChangesResult.BadRequest(
                $"Feedback cannot exceed {MaxFeedbackLength} characters.");
        }

        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return RequestExecutionChangesResult.NotFound();
        }

        if (command.RepositoryWorkspaceId.HasValue &&
            execution.DevelopmentTask?.RepositoryWorkspaceId != command.RepositoryWorkspaceId.Value)
        {
            return RequestExecutionChangesResult.NotFound();
        }

        var rejection = DescribeWhyChangesCannotBeRequested(execution);
        if (rejection != null)
        {
            return RequestExecutionChangesResult.Conflict(rejection);
        }

        if (await _executionRepository
                .HasActiveExecutionForTaskAsync(execution.DevelopmentTaskId, cancellationToken)
                .ConfigureAwait(false))
        {
            return RequestExecutionChangesResult.Conflict("Another execution for this task is already active.");
        }

        var revisionNumber = execution.ChangeRequestCount + 1; // read before the claim counts this request
        var leaseToken = Guid.NewGuid();
        var requestedAt = DateTime.UtcNow;
        var claimed = await _revisionStore
            .ClaimCompletedForRevisionAsync(execution.Id, leaseToken, feedback, requestedAt, cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            return RequestExecutionChangesResult.Conflict("Changes could not be requested for this execution right now.");
        }

        await SafeRecordActivityAsync(
            execution.Id,
            ExecutionStage.Review,
            ExecutionActivityStatus.Rejected,
            $"Changes requested (revision {revisionNumber}).",
            new ExecutionActivityMetadata(EventKind: "ChangesRequested")).ConfigureAwait(false);

        try
        {
            _dispatcher.EnqueueReviseExecution(execution.Id, leaseToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RequestExecutionChanges: failed to enqueue the fix for {ExecutionId}.", execution.Id);
            await _revisionStore
                .RestoreCompletedAfterRevisionDispatchFailureAsync(execution.Id, leaseToken, CancellationToken.None)
                .ConfigureAwait(false);
            return RequestExecutionChangesResult.Failed("Failed to enqueue the fix.");
        }

        return RequestExecutionChangesResult.Accepted(revisionNumber);
    }

    public async Task<RequestExecutionChangesResult> ExecuteAsync(
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
                "RequestExecutionChanges: execution {ExecutionId} is not held by lease {LeaseToken} — skipping.",
                executionId,
                leaseToken);
            return RequestExecutionChangesResult.Skip();
        }

        var feedback = execution.LastChangeRequest ?? string.Empty;
        var revisionNumber = execution.ChangeRequestCount; // already counted by the claim
        var workspacePath = execution.WorkspacePath;
        var wasApproved = execution.ReviewStatus == ExecutionReviewStatus.Approved;
        var fingerprintBefore = await TryComputeFingerprintAsync(workspacePath).ConfigureAwait(false);
        await CaptureSnapshotAsync(executionId, workspacePath, $"r{revisionNumber}-base", isBase: true).ConfigureAwait(false);
        string? failure = null;

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
                return await FinishCancellationAsync(executionId, leaseToken, revisionNumber).ConfigureAwait(false);
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
                    // Baseline comparisons keep using the repository base, never an earlier delivered commit of this branch.
                    VerifyOnlyWorkspace: new ExecutionVerifyOnlyWorkspace(
                        execution.WorkspacePath ?? string.Empty,
                        execution.BranchName ?? string.Empty,
                        execution.InitialBaseCommitSha ?? execution.BaseCommitSha),
                    ChangeRequest: new ExecutionChangeRequest(
                        feedback,
                        revisionNumber,
                        CommittedBaseCommitSha: execution.InitialBaseCommitSha ?? execution.BaseCommitSha));

                await _processor.ProcessAsync(context, executionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (executionToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return await FinishCancellationAsync(executionId, leaseToken, revisionNumber).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (await _executionRepository.IsCancellationRequestedAsync(executionId, CancellationToken.None).ConfigureAwait(false))
                {
                    return await FinishCancellationAsync(executionId, leaseToken, revisionNumber).ConfigureAwait(false);
                }

                _logger.LogError(ex, "RequestExecutionChanges: applying the fix failed for execution {ExecutionId}.", executionId);
                failure = ProcessExecutionCommandHandler.SanitizeErrorMessage(ex.Message);
                await SafeRecordActivityAsync(
                    executionId,
                    ExecutionStage.Execution,
                    ExecutionActivityStatus.Failed,
                    $"Requested fix failed: {failure}").ConfigureAwait(false);
            }

            if (await IsCancelledAsync(executionId, executionToken).ConfigureAwait(false))
            {
                return await FinishCancellationAsync(executionId, leaseToken, revisionNumber).ConfigureAwait(false);
            }

            // Only a worktree that really changed leaves the earlier approval and delivery behind. A fix that failed
            // or changed nothing keeps the previous state, which still describes the unchanged code.
            var fingerprintAfter = await TryComputeFingerprintAsync(workspacePath).ConfigureAwait(false);
            await CaptureSnapshotAsync(executionId, workspacePath, $"r{revisionNumber}-result", isBase: false).ConfigureAwait(false);
            var codeChanged = fingerprintBefore != null && fingerprintAfter != null &&
                              !string.Equals(fingerprintBefore, fingerprintAfter, StringComparison.Ordinal);

            if (codeChanged)
            {
                await _revisionStore
                    .MarkRevisionPendingDeliveryAsync(executionId, CancellationToken.None)
                    .ConfigureAwait(false);

                if (wasApproved)
                {
                    await SafeRecordActivityAsync(
                        executionId,
                        ExecutionStage.Execution,
                        ExecutionActivityStatus.Completed,
                        "Review approval cleared because the code changed after the requested fix.",
                        new ExecutionActivityMetadata(EventKind: "ReviewInvalidated")).ConfigureAwait(false);
                }
            }

            var revisionResult = failure != null
                ? $"Failed: {failure}"
                : codeChanged
                    ? "Applied: code updated, build and test ran again. Review it and approve to update the pull request."
                    : "No change: the AI did not change any code for this feedback.";
            await _revisionStore
                .SetRevisionResultAsync(executionId, revisionResult, CancellationToken.None)
                .ConfigureAwait(false);

            var completed = await _executionRepository
                .CompleteWithLeaseAsync(executionId, leaseToken, CancellationToken.None)
                .ConfigureAwait(false);

            if (!completed)
            {
                return RequestExecutionChangesResult.Failed("Execution lease lost while applying the requested changes.");
            }

            if (_snapshotRecorder != null)
            {
                await _snapshotRecorder.RecordAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            }

            return RequestExecutionChangesResult.Accepted(revisionNumber);
        }
        finally
        {
            _cancellationRegistry.Unregister(executionId);
        }
    }

    /// <summary>The revision's diff is the difference between the snapshot before and after; failing to take one only hides that diff.</summary>
    private async Task CaptureSnapshotAsync(Guid executionId, string? workspacePath, string label, bool isBase)
    {
        if (_snapshotService == null || string.IsNullOrWhiteSpace(workspacePath))
        {
            return;
        }

        try
        {
            var sha = await _snapshotService
                .CaptureAsync(workspacePath, executionId, label, CancellationToken.None)
                .ConfigureAwait(false);
            if (sha != null)
            {
                await _revisionStore
                    .SetRevisionSnapshotAsync(executionId, isBase ? sha : null, isBase ? null : sha, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RequestExecutionChanges: snapshot '{Label}' failed for {ExecutionId}.", label, executionId);
        }
    }

    private async Task<string?> TryComputeFingerprintAsync(string? workspacePath)
    {
        if (_fingerprintCalculator == null || string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        var fingerprint = await _fingerprintCalculator
            .ComputeFingerprintAsync(workspacePath, CancellationToken.None)
            .ConfigureAwait(false);

        return fingerprint.Success && !string.IsNullOrWhiteSpace(fingerprint.Fingerprint) ? fingerprint.Fingerprint : null;
    }

    private async Task<RequestExecutionChangesResult> FinishCancellationAsync(
        Guid executionId,
        Guid leaseToken,
        int revisionNumber)
    {
        await _executionRepository
            .AcknowledgeCancellationWithLeaseAsync(executionId, leaseToken, CancellationToken.None)
            .ConfigureAwait(false);

        await SafeRecordActivityAsync(
            executionId,
            ExecutionStage.Execution,
            ExecutionActivityStatus.Completed,
            "Requested fix cancelled.").ConfigureAwait(false);

        return RequestExecutionChangesResult.Accepted(revisionNumber);
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
            _logger.LogWarning(ex, "RequestExecutionChanges: failed to record activity for {ExecutionId}.", executionId);
        }
    }
}
