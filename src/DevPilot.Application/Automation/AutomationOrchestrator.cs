using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Commands.CreatePullRequest;
using DevPilot.Application.Executions.Commands.MergeExecution;
using DevPilot.Application.Executions.Commands.PushExecution;
using DevPilot.Application.Executions.Commands.RetryExecution;
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

    /// <summary>Start of the activity written when the safety gate hands a run back; the goal board reads it to know a person is needed.</summary>
    public const string LeftForPersonPrefix = "Automation left this for a person";

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
    private readonly IRetryExecutionCommandHandler? _retry;
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
        IVisualArtifactReader? visualReader = null,
        IRetryExecutionCommandHandler? retry = null)
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
        _retry = retry;
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
            await RetryFailedGenerationsAsync(policy, since, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>
    /// Restarts a task whose run died while the code was being written (a patch that never applied, a provider
    /// hiccup). Once per task: AutomationRetryRules caps it so the cost cannot loop.
    /// </summary>
    private async Task RetryFailedGenerationsAsync(AutomationPolicy policy, DateTime since, CancellationToken ct)
    {
        if (_retry is null)
        {
            return;
        }

        var active = await _work.CountActiveExecutionsAsync(policy.RepositoryWorkspaceId, ct).ConfigureAwait(false);
        var capacity = Math.Max(1, policy.MaxParallelExecutions) - active;
        if (capacity <= 0)
        {
            return;
        }

        var failedIds = await _work.GetRetryableFailedExecutionIdsAsync(policy.RepositoryWorkspaceId, since, ct).ConfigureAwait(false);
        foreach (var executionId in failedIds)
        {
            if (capacity <= 0)
            {
                break;
            }

            try
            {
                var execution = await _executions.GetByIdAsync(executionId, ct).ConfigureAwait(false);
                if (execution is null || execution.Status != TaskExecutionStatus.Failed)
                {
                    continue;
                }

                var activities = await _activities.GetByExecutionIdAsync(executionId, ct).ConfigureAwait(false);
                if (!AutomationRetryRules.IsGenerationFailure(activities))
                {
                    continue;
                }

                var retried = await _retry.HandleAsync(new RetryExecutionCommand(execution.DevelopmentTaskId), ct).ConfigureAwait(false);
                if (!retried.Success)
                {
                    _logger.LogDebug("Automation could not retry task {TaskId}: {Reason}", execution.DevelopmentTaskId, retried.ErrorMessage);
                    continue;
                }

                capacity--;
                await RecordAsync(
                    executionId,
                    ExecutionStage.Execution,
                    $"Automation restarted this task after the code generation failed (attempt 2 of {AutomationRetryRules.MaxExecutionsPerTask}). A second failure is left for a person.",
                    ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Automation retried task {TaskId} after generation failure of execution {ExecutionId} as execution {NewExecutionId}.",
                    execution.DevelopmentTaskId,
                    executionId,
                    retried.Execution?.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Automation failed to retry execution {ExecutionId}.", executionId);
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
            // A run that ended broken or empty is worth one more attempt: waiting for a person would also hold back
            // every task that depends on it. Policy-limit blocks (sensitive files, size, protected paths) are not
            // retried, a second run would hit the same rule.
            if (AutomationRetryRules.IsRestartableOutcome(outcome, diff.ChangedFiles.Count) &&
                await TryRestartUnverifiedAsync(execution, gate.Summary, ct).ConfigureAwait(false))
            {
                return false;
            }

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

    /// <summary>
    /// A step whose lease was claimed and never released is called again; the step reclaims it and its crash recovery
    /// completes the record when the work itself was already done. A fresh lease is left alone.
    /// </summary>
    private static bool IsStaleInProgress(bool inProgress, DateTime? claimedAt) =>
        inProgress && AutomationDeliveryRules.IsLeaseStale(claimedAt, DateTime.UtcNow);

    private async Task DeliverApprovedAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await _executions.GetByIdAsync(executionId, ct).ConfigureAwait(false);
        if (execution is null || execution.ReviewStatus != ExecutionReviewStatus.Approved)
        {
            return;
        }

        if (execution.CommitStatus == ExecutionCommitStatus.None || IsStaleInProgress(execution.CommitStatus == ExecutionCommitStatus.InProgress, execution.CommitClaimedAt))
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

        if (execution.CommitStatus == ExecutionCommitStatus.Committed &&
            (execution.PushStatus == ExecutionPushStatus.None || IsStaleInProgress(execution.PushStatus == ExecutionPushStatus.InProgress, execution.PushClaimedAt)))
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

        // A pull request claim that never finished (the process stopped mid-call) is retried once its lease is stale;
        // the step adopts a pull request that GitHub already has for the branch instead of opening a second one.
        if (execution.PushStatus == ExecutionPushStatus.Pushed &&
            (execution.PullRequestStatus == ExecutionPullRequestStatus.None ||
             IsStaleInProgress(execution.PullRequestStatus == ExecutionPullRequestStatus.InProgress, execution.PullRequestClaimedAt)))
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
            if (execution is null ||
                (execution.MergeStatus != ExecutionMergeStatus.None &&
                 !IsStaleInProgress(execution.MergeStatus == ExecutionMergeStatus.InProgress, execution.MergeClaimedAt)))
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
            if (merge.IsBaseConflict)
            {
                await RestartAfterConflictAsync(execution, merge.ErrorMessage, ct).ConfigureAwait(false);
            }
            else if (merge.Status is MergeExecutionResultStatus.Success or MergeExecutionResultStatus.Created)
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

    /// <summary>
    /// The pull request conflicts with a base that moved on while it waited. The change is redone from the current base
    /// (a new run that reads the files as they are now) instead of asking a person to resolve a conflict in code they did
    /// not write. The conflicting pull request is left open and untouched.
    /// </summary>
    private async Task RestartAfterConflictAsync(TaskExecution conflicted, string? refusal, CancellationToken ct)
    {
        var reason = $"pull request #{conflicted.PullRequestNumber} conflicts with the base branch";

        if (_retry is null)
        {
            await RecordBlockedAsync(conflicted.Id, ExecutionStage.Merge, $"The {reason}.", ct).ConfigureAwait(false);
            return;
        }

        var runs = await _work.CountExecutionsForTaskAsync(conflicted.DevelopmentTaskId, ct).ConfigureAwait(false);
        if (!AutomationRetryRules.HasConflictRestartLeft(runs))
        {
            await RecordBlockedAsync(
                conflicted.Id,
                ExecutionStage.Merge,
                $"The {reason}, and this task already ran {runs} times. Resolve the conflict by hand or request a change.",
                ct).ConfigureAwait(false);
            return;
        }

        // The parallel limit is deliberately not applied: the merge was refused for good, so this is the only moment
        // the restart happens, and a one-off extra run just queues behind the workers already busy.
        var restarted = await _retry
            .HandleAsync(new RetryExecutionCommand(conflicted.DevelopmentTaskId, null, conflicted.Id), ct)
            .ConfigureAwait(false);
        if (!restarted.Success)
        {
            await RecordBlockedAsync(conflicted.Id, ExecutionStage.Merge, $"The {reason}, and it could not be restarted: {restarted.ErrorMessage}", ct).ConfigureAwait(false);
            return;
        }

        await RecordAsync(
            conflicted.Id,
            ExecutionStage.Merge,
            $"Automation redid this task on the current base because {reason} (run {runs + 1} of {1 + AutomationRetryRules.MaxConflictRestarts}). Pull request #{conflicted.PullRequestNumber} stays open; close it once the new one is merged.",
            ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Automation restarted task {TaskId} on the current base after the merge of execution {ExecutionId} conflicted: {Refusal}",
            conflicted.DevelopmentTaskId,
            conflicted.Id,
            refusal);
    }

    /// <summary>Redoes a task whose run finished without a usable, verified change. Capped by <see cref="AutomationRetryRules.MaxExecutionsPerTask"/>.</summary>
    private async Task<bool> TryRestartUnverifiedAsync(TaskExecution execution, string reason, CancellationToken ct)
    {
        if (_retry is null)
        {
            return false;
        }

        var runs = await _work.CountExecutionsForTaskAsync(execution.DevelopmentTaskId, ct).ConfigureAwait(false);
        if (!AutomationRetryRules.HasRetryLeft(runs))
        {
            return false;
        }

        var restarted = await _retry
            .HandleAsync(new RetryExecutionCommand(execution.DevelopmentTaskId, null, execution.Id), ct)
            .ConfigureAwait(false);
        if (!restarted.Success)
        {
            _logger.LogDebug("Automation could not restart task {TaskId}: {Reason}", execution.DevelopmentTaskId, restarted.ErrorMessage);
            return false;
        }

        await RecordAsync(
            execution.Id,
            ExecutionStage.Review,
            $"Automation restarted this task because the run ended without a verified change ({reason}) (run {runs + 1} of {AutomationRetryRules.MaxExecutionsPerTask}). A second failure is left for a person.",
            ct).ConfigureAwait(false);
        return true;
    }

    private Task RecordBlockedAsync(Guid executionId, ExecutionStage stage, string reason, CancellationToken ct) =>
        _ledger.ShouldRecord(executionId, $"{stage}:{reason}")
            ? RecordAsync(executionId, stage, $"{LeftForPersonPrefix}: {reason}", ct)
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
