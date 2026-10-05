using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Commands.CreatePullRequest;
using DevPilot.Application.Executions.Commands.MergeExecution;
using DevPilot.Application.Executions.Commands.PushExecution;
using DevPilot.Application.Executions.Commands.StartExecution;
using DevPilot.Application.Executions.Commands.SyncPullRequest;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.Tasks.Commands.ApproveTask;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Automation;

public interface IAutomationOrchestrator
{
    /// <summary>Moves the work of one workspace as far as its policy allows. Safe to call repeatedly.</summary>
    Task AdvanceAsync(AutomationPolicy policy, CancellationToken cancellationToken = default);
}

/// <summary>
/// Drives the existing task pipeline (approve, start, review, commit, push, pull request, merge) on behalf of
/// a person, one idempotent step at a time. It never bypasses a rule of the pipeline: every step goes through the
/// same command handler a person would use, and anything the safety gate does not clear is left for a person
/// together with the reason.
/// </summary>
public sealed class AutomationOrchestrator : IAutomationOrchestrator
{
    private const string DecisionEvent = "AutomationDecision";

    private readonly IAutomationWorkReader _work;
    private readonly IApproveTaskCommandHandler _approveTask;
    private readonly IStartExecutionCommandHandler _startExecution;
    private readonly IExecutionRepository _executions;
    private readonly IExecutionActivityRepository _activities;
    private readonly IExecutionActivityRecorder _recorder;
    private readonly IExecutionChangeFingerprintCalculator _fingerprint;
    private readonly IExecutionGitDiffReader _diffReader;
    private readonly IApproveExecutionReviewCommandHandler _approveReview;
    private readonly ICommitExecutionCommandHandler _commit;
    private readonly IPushExecutionCommandHandler _push;
    private readonly ICreatePullRequestCommandHandler _createPullRequest;
    private readonly ISyncPullRequestCommandHandler _syncPullRequest;
    private readonly IMergeExecutionCommandHandler _merge;
    private readonly AutomationDecisionLedger _ledger;
    private readonly ILogger<AutomationOrchestrator> _logger;
    private readonly IVisualArtifactReader? _visualReader;

    public AutomationOrchestrator(
        IAutomationWorkReader work,
        IApproveTaskCommandHandler approveTask,
        IStartExecutionCommandHandler startExecution,
        IExecutionRepository executions,
        IExecutionActivityRepository activities,
        IExecutionActivityRecorder recorder,
        IExecutionChangeFingerprintCalculator fingerprint,
        IExecutionGitDiffReader diffReader,
        IApproveExecutionReviewCommandHandler approveReview,
        ICommitExecutionCommandHandler commit,
        IPushExecutionCommandHandler push,
        ICreatePullRequestCommandHandler createPullRequest,
        ISyncPullRequestCommandHandler syncPullRequest,
        IMergeExecutionCommandHandler merge,
        AutomationDecisionLedger ledger,
        ILogger<AutomationOrchestrator> logger,
        IVisualArtifactReader? visualReader = null)
    {
        _work = work;
        _approveTask = approveTask;
        _startExecution = startExecution;
        _executions = executions;
        _activities = activities;
        _recorder = recorder;
        _fingerprint = fingerprint;
        _diffReader = diffReader;
        _approveReview = approveReview;
        _commit = commit;
        _push = push;
        _createPullRequest = createPullRequest;
        _syncPullRequest = syncPullRequest;
        _merge = merge;
        _ledger = ledger;
        _logger = logger;
        _visualReader = visualReader;
    }

    public async Task AdvanceAsync(AutomationPolicy policy, CancellationToken cancellationToken = default)
    {
        if (policy.Paused || policy.Level == AutomationLevel.Manual || policy.ActiveSince is null)
        {
            return;
        }

        var since = policy.ActiveSince.Value;

        if (policy.Level >= AutomationLevel.SemiAuto)
        {
            await StartReadyTasksAsync(policy, since, cancellationToken).ConfigureAwait(false);
        }

        if (policy.Level >= AutomationLevel.AutoPr)
        {
            foreach (var id in await _work.GetDeliverableExecutionIdsAsync(policy.RepositoryWorkspaceId, since, cancellationToken).ConfigureAwait(false))
            {
                await TryDeliverAsync(policy, id, cancellationToken).ConfigureAwait(false);
            }
        }

        if (policy.Level >= AutomationLevel.FullAuto)
        {
            foreach (var id in await _work.GetOpenPullRequestExecutionIdsAsync(policy.RepositoryWorkspaceId, since, cancellationToken).ConfigureAwait(false))
            {
                await TryMergeAsync(policy, id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task StartReadyTasksAsync(AutomationPolicy policy, DateTime since, CancellationToken ct)
    {
        var active = await _work.CountActiveExecutionsAsync(policy.RepositoryWorkspaceId, ct).ConfigureAwait(false);
        var capacity = Math.Max(1, policy.MaxParallelExecutions) - active;
        if (capacity <= 0)
        {
            return;
        }

        var tasks = await _work.GetTasksReadyToStartAsync(policy.RepositoryWorkspaceId, since, ct).ConfigureAwait(false);
        foreach (var task in tasks)
        {
            if (capacity <= 0)
            {
                break;
            }

            if (task.Status == DevelopmentTaskStatus.AwaitingApproval)
            {
                var approved = await _approveTask.HandleAsync(new ApproveTaskCommand(task.Id), ct).ConfigureAwait(false);
                if (!approved.Success)
                {
                    // Typically the impact analysis is not finished yet; the next poll tries again.
                    _logger.LogDebug("Automation could not approve task {TaskId}: {Reason}", task.Id, approved.ErrorMessage);
                    continue;
                }
            }

            var started = await _startExecution.HandleAsync(new StartExecutionCommand(task.Id), ct).ConfigureAwait(false);
            if (started.Success)
            {
                capacity--;
                _logger.LogInformation("Automation approved and started task {TaskId} as execution {ExecutionId}.", task.Id, started.Execution?.Id);
            }
            else
            {
                _logger.LogDebug("Automation could not start task {TaskId}: {Reason}", task.Id, started.ErrorMessage);
            }
        }
    }

    private async Task TryDeliverAsync(AutomationPolicy policy, Guid executionId, CancellationToken ct)
    {
        try
        {
            var execution = await _executions.GetByIdAsync(executionId, ct).ConfigureAwait(false);
            if (execution is null || execution.Status != TaskExecutionStatus.Completed)
            {
                return;
            }

            if (execution.ReviewStatus == ExecutionReviewStatus.Pending && !await ApproveIfSafeAsync(policy, execution, ct).ConfigureAwait(false))
            {
                return;
            }

            await DeliverApprovedAsync(executionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Automation failed to deliver execution {ExecutionId}.", executionId);
        }
    }

    private async Task<bool> ApproveIfSafeAsync(AutomationPolicy policy, TaskExecution execution, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return false;
        }

        var activities = await _activities.GetByExecutionIdAsync(execution.Id, ct).ConfigureAwait(false);
        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        var verdict = ExecutionVerdictBuilder.Resolve(execution, activities, outcome).Verdict;

        var fingerprint = await _fingerprint.ComputeFingerprintAsync(execution.WorkspacePath, ct).ConfigureAwait(false);
        if (!fingerprint.Success || string.IsNullOrEmpty(fingerprint.Fingerprint))
        {
            await RecordBlockedAsync(execution.Id, ExecutionStage.Review, $"The change fingerprint could not be computed: {fingerprint.ErrorMessage}", ct).ConfigureAwait(false);
            return false;
        }

        var diff = await _diffReader.ReadWorkspaceDiffAsync(execution.WorkspacePath, execution.BranchName, ct).ConfigureAwait(false);
        if (!diff.Success || diff.ChangedFiles is null)
        {
            await RecordBlockedAsync(execution.Id, ExecutionStage.Review, $"The changed files could not be read: {diff.ErrorMessage}", ct).ConfigureAwait(false);
            return false;
        }

        var visualRequired = false;
        if (_visualReader is not null)
        {
            var manifest = await _visualReader.GetManifestAsync(execution.WorkspacePath, execution.Id, ct).ConfigureAwait(false);
            visualRequired = manifest is { RequiresReview: true };
        }

        var gate = AutomationGate.EvaluateDelivery(
            policy, outcome, verdict, diff.ChangedFiles, fingerprint.HasSensitiveFiles, visualRequired);
        if (!gate.Allowed)
        {
            await RecordBlockedAsync(execution.Id, ExecutionStage.Review, gate.Summary, ct).ConfigureAwait(false);
            return false;
        }

        var approval = await _approveReview
            .HandleAsync(new ApproveExecutionReviewCommand(execution.Id, fingerprint.Fingerprint), ct)
            .ConfigureAwait(false);
        if (approval.Status != ApproveExecutionReviewResultStatus.Success)
        {
            await RecordBlockedAsync(execution.Id, ExecutionStage.Review, approval.ErrorMessage ?? "The review could not be approved.", ct).ConfigureAwait(false);
            return false;
        }

        await RecordAsync(
            execution.Id,
            ExecutionStage.Review,
            $"Automation approved the review: verification '{outcome}', {diff.ChangedFiles.Count} file(s) within the policy limits.",
            ct).ConfigureAwait(false);
        return true;
    }

    private async Task DeliverApprovedAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await _executions.GetByIdAsync(executionId, ct).ConfigureAwait(false);
        if (execution is null || execution.ReviewStatus != ExecutionReviewStatus.Approved)
        {
            return;
        }

        if (execution.CommitStatus == ExecutionCommitStatus.None)
        {
            var commit = await _commit.HandleAsync(new CommitExecutionCommand(executionId), ct).ConfigureAwait(false);
            if (commit.Status != CommitExecutionResultStatus.Success)
            {
                await RecordBlockedAsync(executionId, ExecutionStage.Commit, commit.ErrorMessage ?? "The commit failed.", ct).ConfigureAwait(false);
                return;
            }

            execution = await _executions.GetByIdAsync(executionId, ct).ConfigureAwait(false);
            if (execution is null)
            {
                return;
            }
        }

        if (execution.CommitStatus == ExecutionCommitStatus.Committed && execution.PushStatus == ExecutionPushStatus.None)
        {
            var push = await _push.HandleAsync(new PushExecutionCommand(executionId), ct).ConfigureAwait(false);
            if (push.Status != PushExecutionResultStatus.Success)
            {
                await RecordBlockedAsync(executionId, ExecutionStage.Push, push.ErrorMessage ?? "The push failed.", ct).ConfigureAwait(false);
                return;
            }

            execution = await _executions.GetByIdAsync(executionId, ct).ConfigureAwait(false);
            if (execution is null)
            {
                return;
            }
        }

        if (execution.PushStatus == ExecutionPushStatus.Pushed && execution.PullRequestStatus == ExecutionPullRequestStatus.None)
        {
            var pr = await _createPullRequest.HandleAsync(new CreatePullRequestCommand(executionId), ct).ConfigureAwait(false);
            if (pr.Status is CreatePullRequestResultStatus.Success or CreatePullRequestResultStatus.Created)
            {
                await RecordAsync(executionId, ExecutionStage.PullRequest, $"Automation opened pull request #{pr.Response?.PullRequestNumber}.", ct).ConfigureAwait(false);
            }
            else
            {
                await RecordBlockedAsync(executionId, ExecutionStage.PullRequest, pr.ErrorMessage ?? "The pull request could not be opened.", ct).ConfigureAwait(false);
            }
        }
    }

    private async Task TryMergeAsync(AutomationPolicy policy, Guid executionId, CancellationToken ct)
    {
        try
        {
            await _syncPullRequest.HandleAsync(new SyncPullRequestCommand(executionId), ct).ConfigureAwait(false);

            var execution = await _executions.GetByIdAsync(executionId, ct).ConfigureAwait(false);
            if (execution is null || execution.MergeStatus != ExecutionMergeStatus.None)
            {
                return;
            }

            var decision = AutomationGate.EvaluateMerge(
                policy, execution.PullRequestRemoteState, execution.PullRequestIntegrityStatus, execution.CiStatus);
            switch (decision.Readiness)
            {
                case AutomationMergeReadiness.Waiting:
                    return;
                case AutomationMergeReadiness.Blocked:
                    await RecordBlockedAsync(executionId, ExecutionStage.Merge, decision.Reason, ct).ConfigureAwait(false);
                    return;
            }

            var merge = await _merge.HandleAsync(new MergeExecutionCommand(executionId), ct).ConfigureAwait(false);
            if (merge.Status is MergeExecutionResultStatus.Success or MergeExecutionResultStatus.Created)
            {
                await RecordAsync(executionId, ExecutionStage.Merge, $"Automation merged the pull request: {decision.Reason}", ct).ConfigureAwait(false);
            }
            else
            {
                await RecordBlockedAsync(executionId, ExecutionStage.Merge, merge.ErrorMessage ?? "The merge was refused.", ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Automation failed to merge execution {ExecutionId}.", executionId);
        }
    }

    private Task RecordBlockedAsync(Guid executionId, ExecutionStage stage, string reason, CancellationToken ct) =>
        _ledger.ShouldRecord(executionId, $"{stage}:{reason}")
            ? RecordAsync(executionId, stage, $"Automation left this for a person: {reason}", ct)
            : Task.CompletedTask;

    private async Task RecordAsync(Guid executionId, ExecutionStage stage, string message, CancellationToken ct)
    {
        try
        {
            await _recorder
                .RecordActivityAsync(
                    executionId,
                    stage,
                    ExecutionActivityStatus.Completed,
                    message,
                    new ExecutionActivityMetadata(EventKind: DecisionEvent),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record an automation decision for execution {ExecutionId}.", executionId);
        }
    }
}
