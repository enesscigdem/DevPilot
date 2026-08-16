using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Application.Executions.Commands.MergeExecution;

public sealed record MergeExecutionCommand(Guid ExecutionId);

public enum MergeExecutionResultStatus
{
    Success,
    Created,
    NotFound,
    Conflict,
    ExternalFailure
}

public sealed record MergeExecutionResponseDto(
    Guid ExecutionId,
    string MergeStatus,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string BaseBranch,
    string HeadBranch,
    string ApprovedHeadSha,
    string? MergeCommitSha,
    DateTime? MergedAt,
    string? MergeMethod);

public sealed class MergeExecutionResult
{
    public MergeExecutionResultStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    public MergeExecutionResponseDto? Response { get; private set; }

    public static MergeExecutionResult Ok(MergeExecutionResponseDto response) =>
        new() { Status = MergeExecutionResultStatus.Success, Response = response };

    public static MergeExecutionResult Created(MergeExecutionResponseDto response) =>
        new() { Status = MergeExecutionResultStatus.Created, Response = response };

    public static MergeExecutionResult NotFound(string message) =>
        new() { Status = MergeExecutionResultStatus.NotFound, ErrorMessage = message };

    public static MergeExecutionResult Conflict(string message) =>
        new() { Status = MergeExecutionResultStatus.Conflict, ErrorMessage = message };

    public static MergeExecutionResult ExternalFailure(string message) =>
        new() { Status = MergeExecutionResultStatus.ExternalFailure, ErrorMessage = message };
}

public interface IMergeExecutionCommandHandler
{
    Task<MergeExecutionResult> HandleAsync(
        MergeExecutionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class MergeExecutionCommandHandler : IMergeExecutionCommandHandler
{
    private static readonly TimeSpan MergeLeaseTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SyncLeaseTimeout = TimeSpan.FromSeconds(30);

    private readonly IExecutionRepository _executionRepository;
    private readonly IGitHubPullRequestClient _githubClient;
    private readonly IExecutionGitHubSyncService _githubSyncService;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly IExecutionActivityRepository _activityRepository;
    private readonly IOptions<MergePolicyOptions> _mergePolicyOptions;
    private readonly ILogger<MergeExecutionCommandHandler> _logger;

    public MergeExecutionCommandHandler(
        IExecutionRepository executionRepository,
        IGitHubPullRequestClient githubClient,
        IExecutionGitHubSyncService githubSyncService,
        IExecutionActivityRecorder activityRecorder,
        IExecutionActivityRepository activityRepository,
        IOptions<MergePolicyOptions> mergePolicyOptions,
        ILogger<MergeExecutionCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _githubClient = githubClient;
        _githubSyncService = githubSyncService;
        _activityRecorder = activityRecorder;
        _activityRepository = activityRepository;
        _mergePolicyOptions = mergePolicyOptions;
        _logger = logger;
    }

    public async Task<MergeExecutionResult> HandleAsync(
        MergeExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return MergeExecutionResult.NotFound("Execution not found.");
        }

        // 1. Idempotency Check: If already merged locally, return persisted result without issuing GitHub calls or duplicate activity
        if (execution.MergeStatus == ExecutionMergeStatus.Merged)
        {
            return MergeExecutionResult.Ok(BuildResponse(execution));
        }

        // 2. Fetch local Build & Test evidence
        var activities = await _activityRepository.GetByExecutionIdAsync(execution.Id, cancellationToken).ConfigureAwait(false);
        var buildPassed = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Completed);
        var testPassed = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Completed);

        var allowNoChecks = _mergePolicyOptions.Value.AllowNoChecks;

        // 3. Local Preconditions Check
        if (!ExecutionMergeEligibility.CalculateCanRequestMerge(execution, allowNoChecks, buildPassed, testPassed, allowInProgress: true))
        {
            return MergeExecutionResult.Conflict("Execution local lifecycle or CI preconditions do not allow merge.");
        }

        // 4. Claim DB Merge Lease
        var now = DateTime.UtcNow;
        var attemptId = Guid.NewGuid();

        var claimed = await _executionRepository.TryClaimMergeLeaseAsync(execution.Id, attemptId, now, SyncLeaseTimeout, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            claimed = await _executionRepository.TryReclaimStaleMergeLeaseAsync(execution.Id, attemptId, now, MergeLeaseTimeout, SyncLeaseTimeout, cancellationToken).ConfigureAwait(false);
        }

        if (!claimed)
        {
            return MergeExecutionResult.Conflict("Merge operation is already in progress.");
        }

        // Record Merge started activity
        await SafeRecordActivityAsync(execution.Id, ExecutionStage.Merge, ExecutionActivityStatus.Started, "Merge operation started.", cancellationToken).ConfigureAwait(false);

        var repoOwner = execution.DevelopmentTask?.RepositoryWorkspace?.Owner ?? string.Empty;
        var repoName = execution.DevelopmentTask?.RepositoryWorkspace?.Repository ?? string.Empty;
        var prNumber = execution.PullRequestNumber!.Value;

        // 5. Authoritative Live Preflight (Bypass freshness cache)
        var liveSync = await _githubSyncService.SyncPullRequestAndCiAsync(execution, bypassFreshnessCache: true, cancellationToken).ConfigureAwait(false);

        if (!liveSync.Success)
        {
            if (liveSync.IsConfigurationError)
            {
                await _executionRepository.SetMergeFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return MergeExecutionResult.ExternalFailure(liveSync.ErrorMessage ?? "GitHub API configuration error.");
            }

            // Retry on stale InProgress will re-evaluate, but for preflight failure fail active attempt
            await _executionRepository.SetMergeFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return MergeExecutionResult.ExternalFailure(liveSync.ErrorMessage ?? "Live GitHub preflight synchronization failed.");
        }

        // Live PR fetch
        var livePrResult = await _githubClient.GetPullRequestAsync(repoOwner, repoName, prNumber, cancellationToken).ConfigureAwait(false);
        if (!livePrResult.IsSuccess || livePrResult.Data is null)
        {
            await _executionRepository.SetMergeFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return MergeExecutionResult.ExternalFailure(livePrResult.ErrorMessage ?? "Failed to retrieve live PR from GitHub.");
        }

        var livePr = livePrResult.Data;

        // Recovery check: PR is already merged on GitHub with exact expected properties
        if (livePr.Merged || string.Equals(livePr.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            if (livePr.Merged &&
                string.Equals(livePr.HeadRef, execution.RemoteBranchName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(livePr.HeadSha, execution.RemoteCommitSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(livePr.BaseRef, execution.PullRequestBaseBranch, StringComparison.OrdinalIgnoreCase))
            {
                var remoteMergedAt = livePr.MergedAt ?? now;
                var mergeSha = livePr.HeadSha;

                await _executionRepository.SetExecutionMergedAsync(execution.Id, attemptId, mergeSha, remoteMergedAt, "merge", cancellationToken).ConfigureAwait(false);
                await SafeRecordActivityAsync(execution.Id, ExecutionStage.Merge, ExecutionActivityStatus.Completed, "Merge confirmed (recovered remote merged state).", cancellationToken).ConfigureAwait(false);

                var reloaded = await _executionRepository.GetByIdAsync(execution.Id, cancellationToken).ConfigureAwait(false);
                return MergeExecutionResult.Ok(BuildResponse(reloaded ?? execution));
            }

            await _executionRepository.SetMergeFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return MergeExecutionResult.Conflict("GitHub Pull Request is closed or merged with mismatched head/base state.");
        }

        // Live identity & SHA integrity validation
        if (!string.Equals(livePr.HeadRef, execution.RemoteBranchName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(livePr.HeadSha, execution.RemoteCommitSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(livePr.BaseRef, execution.PullRequestBaseBranch, StringComparison.OrdinalIgnoreCase) ||
            liveSync.IntegrityStatus != ExecutionPullRequestIntegrityStatus.Valid)
        {
            await _executionRepository.SetMergeFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return MergeExecutionResult.Conflict("Live GitHub PR head SHA or branch identity differs from approved execution state.");
        }

        // Live CI eligibility re-validation
        if (!ExecutionMergeEligibility.EvaluateCiEligibility(liveSync.CiStatus, allowNoChecks, buildPassed, testPassed))
        {
            await _executionRepository.SetMergeFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return MergeExecutionResult.Conflict($"Live CI status '{liveSync.CiStatus}' does not satisfy merge policy.");
        }

        // 6. Execute Remote GitHub Merge PUT using exact RemoteCommitSha
        var commitTitle = $"DevPilot Execution: {execution.Id}";
        GitHubPullRequestClientResult<GitHubMergeResultDto> mergeResult;
        try
        {
            mergeResult = await _githubClient.MergePullRequestAsync(
                repoOwner,
                repoName,
                prNumber,
                execution.RemoteCommitSha!,
                commitTitle: commitTitle,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Merge PUT for execution {ExecutionId} threw transport exception.", execution.Id);
            return MergeExecutionResult.ExternalFailure("Merge mutation request encountered transport exception. Execution remains InProgress; subsequent attempt will verify remote state first.");
        }

        if (!mergeResult.IsSuccess)
        {
            if (mergeResult.IsConfigurationError)
            {
                await _executionRepository.SetMergeFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return MergeExecutionResult.ExternalFailure(mergeResult.ErrorMessage ?? "GitHub authentication or permissions error.");
            }

            if (mergeResult.IsConflict)
            {
                await _executionRepository.SetMergeFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return MergeExecutionResult.Conflict(mergeResult.ErrorMessage ?? "GitHub rejected merge due to SHA mismatch or branch protection rules.");
            }

            // Transport/Network uncertainty: DO NOT mark Failed! Keep attempt InProgress so retry can query GitHub truth
            _logger.LogWarning("Merge PUT for execution {ExecutionId} resulted in uncertain transport error: {Error}", execution.Id, mergeResult.ErrorMessage);
            return MergeExecutionResult.ExternalFailure("Merge mutation request encountered transport uncertainty. Execution remains InProgress; subsequent attempt will verify remote state first.");
        }

        // 7. Post-Merge Verification: GET live PR again to verify remote merged state
        var postMergePrResult = await _githubClient.GetPullRequestAsync(repoOwner, repoName, prNumber, cancellationToken).ConfigureAwait(false);
        if (!postMergePrResult.IsSuccess || postMergePrResult.Data is null || !postMergePrResult.Data.Merged)
        {
            // Post-merge GET failed or merged flag is false -> keep InProgress for retry verification
            _logger.LogWarning("Post-merge verification GET failed for execution {ExecutionId}. State remains InProgress for recovery.", execution.Id);
            return MergeExecutionResult.ExternalFailure("Post-merge verification GET failed to confirm remote merge.");
        }

        var verifiedPr = postMergePrResult.Data;
        var authoritativeMergedAt = verifiedPr.MergedAt ?? now;
        var confirmedMergeCommitSha = mergeResult.Data?.MergeCommitSha ?? verifiedPr.HeadSha;

        // Persist local Merged state atomically
        await _executionRepository.SetExecutionMergedAsync(
            execution.Id,
            attemptId,
            confirmedMergeCommitSha,
            authoritativeMergedAt,
            "merge",
            cancellationToken).ConfigureAwait(false);

        await SafeRecordActivityAsync(execution.Id, ExecutionStage.Merge, ExecutionActivityStatus.Completed, "Merge confirmed.", cancellationToken).ConfigureAwait(false);

        var finalExecution = await _executionRepository.GetByIdAsync(execution.Id, cancellationToken).ConfigureAwait(false) ?? execution;

        return MergeExecutionResult.Created(BuildResponse(finalExecution));
    }

    private static MergeExecutionResponseDto BuildResponse(TaskExecution execution) =>
        new(
            ExecutionId: execution.Id,
            MergeStatus: execution.MergeStatus.ToString(),
            PullRequestNumber: execution.PullRequestNumber,
            PullRequestUrl: execution.PullRequestUrl,
            BaseBranch: execution.PullRequestBaseBranch ?? string.Empty,
            HeadBranch: execution.RemoteBranchName ?? execution.BranchName ?? string.Empty,
            ApprovedHeadSha: execution.RemoteCommitSha ?? execution.CommitSha ?? string.Empty,
            MergeCommitSha: execution.MergeCommitSha,
            MergedAt: execution.MergedAt,
            MergeMethod: execution.MergeMethod);

    private async Task SafeRecordActivityAsync(
        Guid executionId,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await _activityRecorder.RecordActivityAsync(executionId, stage, status, message, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record stage activity for execution {ExecutionId}.", executionId);
        }
    }
}
