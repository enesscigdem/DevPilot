using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.CreatePullRequest;

public sealed record CreatePullRequestCommand(Guid ExecutionId);

public enum CreatePullRequestResultStatus
{
    Success,
    Created,
    NotFound,
    Conflict,
    ExternalFailure
}

public sealed record CreatePullRequestResponseDto(
    Guid ExecutionId,
    string PullRequestStatus,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string BaseBranch,
    string HeadBranch,
    string HeadCommitSha,
    DateTime? CreatedAt);

public sealed class CreatePullRequestResult
{
    public CreatePullRequestResultStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    public CreatePullRequestResponseDto? Response { get; private set; }

    public static CreatePullRequestResult Ok(CreatePullRequestResponseDto response) =>
        new() { Status = CreatePullRequestResultStatus.Success, Response = response };

    public static CreatePullRequestResult Created(CreatePullRequestResponseDto response) =>
        new() { Status = CreatePullRequestResultStatus.Created, Response = response };

    public static CreatePullRequestResult NotFound(string message) =>
        new() { Status = CreatePullRequestResultStatus.NotFound, ErrorMessage = message };

    public static CreatePullRequestResult Conflict(string message) =>
        new() { Status = CreatePullRequestResultStatus.Conflict, ErrorMessage = message };

    public static CreatePullRequestResult ExternalFailure(string message) =>
        new() { Status = CreatePullRequestResultStatus.ExternalFailure, ErrorMessage = message };
}

public interface ICreatePullRequestCommandHandler
{
    Task<CreatePullRequestResult> HandleAsync(
        CreatePullRequestCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class CreatePullRequestCommandHandler : ICreatePullRequestCommandHandler
{
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(2);

    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionGitHubPullRequestService _githubPrService;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly ILogger<CreatePullRequestCommandHandler> _logger;

    public CreatePullRequestCommandHandler(
        IExecutionRepository executionRepository,
        IExecutionGitHubPullRequestService githubPrService,
        IExecutionActivityRecorder activityRecorder,
        ILogger<CreatePullRequestCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _githubPrService = githubPrService;
        _activityRecorder = activityRecorder;
        _logger = logger;
    }

    public static bool CalculateCanRequestPullRequest(TaskExecution execution) =>
        execution.ReviewStatus == ExecutionReviewStatus.Approved &&
        execution.CommitStatus == ExecutionCommitStatus.Committed &&
        execution.PushStatus == ExecutionPushStatus.Pushed &&
        (execution.PullRequestStatus == ExecutionPullRequestStatus.None ||
         execution.PullRequestStatus == ExecutionPullRequestStatus.Failed);

    public async Task<CreatePullRequestResult> HandleAsync(
        CreatePullRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return CreatePullRequestResult.NotFound("Execution not found.");
        }

        // 1. Lifecycle Precondition Checks (DO NOT alter DB PR status for pre-lease failures)
        if (execution.Status != TaskExecutionStatus.Completed)
        {
            return CreatePullRequestResult.Conflict($"Execution status is '{execution.Status}' and cannot request pull request.");
        }

        if (execution.ReviewStatus != ExecutionReviewStatus.Approved)
        {
            return CreatePullRequestResult.Conflict($"Execution review status is '{execution.ReviewStatus}' and cannot request pull request.");
        }

        if (execution.CommitStatus != ExecutionCommitStatus.Committed || string.IsNullOrWhiteSpace(execution.CommitSha))
        {
            return CreatePullRequestResult.Conflict("Execution is not committed locally and cannot request pull request.");
        }

        if (execution.PushStatus != ExecutionPushStatus.Pushed || string.IsNullOrWhiteSpace(execution.RemoteCommitSha))
        {
            return CreatePullRequestResult.Conflict("Execution branch is not pushed remotely and cannot request pull request.");
        }

        if (!string.Equals(execution.CommitSha, execution.RemoteCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return CreatePullRequestResult.Conflict("Local commit SHA does not match remote commit SHA.");
        }

        var headBranch = execution.RemoteBranchName ?? execution.BranchName;
        if (string.IsNullOrWhiteSpace(headBranch))
        {
            return CreatePullRequestResult.Conflict("Execution remote branch name is missing.");
        }

        if (!string.Equals(execution.BranchName, headBranch, StringComparison.OrdinalIgnoreCase))
        {
            return CreatePullRequestResult.Conflict("Local branch name does not match remote branch name.");
        }

        var baseBranch = execution.DevelopmentTask?.RepositoryWorkspace?.Branch;
        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            return CreatePullRequestResult.Conflict("Repository workspace base branch is missing or not configured.");
        }

        if (string.Equals(headBranch, baseBranch, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headBranch, "master", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headBranch, "main", StringComparison.OrdinalIgnoreCase))
        {
            return CreatePullRequestResult.Conflict($"Head branch '{headBranch}' is invalid or matches base branch.");
        }

        // 2. Idempotency Check on already Open state
        if (execution.PullRequestStatus == ExecutionPullRequestStatus.Open && execution.PullRequestNumber.HasValue)
        {
            return CreatePullRequestResult.Ok(new CreatePullRequestResponseDto(
                ExecutionId: execution.Id,
                PullRequestStatus: ExecutionPullRequestStatus.Open.ToString(),
                PullRequestNumber: execution.PullRequestNumber,
                PullRequestUrl: execution.PullRequestUrl,
                BaseBranch: execution.PullRequestBaseBranch ?? baseBranch,
                HeadBranch: headBranch,
                HeadCommitSha: execution.RemoteCommitSha!,
                CreatedAt: execution.PullRequestCreatedAt));
        }

        // 3. Lease Acquisition
        Guid attemptId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        if (execution.PullRequestStatus == ExecutionPullRequestStatus.None || execution.PullRequestStatus == ExecutionPullRequestStatus.Failed)
        {
            attemptId = Guid.NewGuid();
            var claimed = await _executionRepository
                .TryClaimNewPullRequestLeaseAsync(execution.Id, attemptId, now, cancellationToken)
                .ConfigureAwait(false);

            if (!claimed)
            {
                return CreatePullRequestResult.Conflict("Concurrent pull request operation detected. Please retry.");
            }

            execution.PullRequestAttemptId = attemptId;
            execution.PullRequestStatus = ExecutionPullRequestStatus.InProgress;
        }
        else if (execution.PullRequestStatus == ExecutionPullRequestStatus.InProgress)
        {
            var isFresh = execution.PullRequestClaimedAt.HasValue && (now - execution.PullRequestClaimedAt.Value) < LeaseTimeout;
            if (isFresh)
            {
                return CreatePullRequestResult.Conflict("A pull request operation for this execution is currently in progress.");
            }

            attemptId = Guid.NewGuid();
            var reclaimed = await _executionRepository
                .TryReclaimStalePullRequestLeaseAsync(execution.Id, attemptId, now, LeaseTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!reclaimed)
            {
                return CreatePullRequestResult.Conflict("Failed to reclaim stale pull request lease.");
            }

            execution.PullRequestAttemptId = attemptId;
        }

        // 4. Record Activity (only after lease acquired)
        try
        {
            await _activityRecorder.RecordActivityAsync(
                execution.Id,
                ExecutionStage.PullRequest,
                ExecutionActivityStatus.Started,
                "Pull request creation started",
                metadata: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record PullRequest started activity for execution {ExecutionId}", execution.Id);
        }

        // 5. Invoke GitHub PR Service
        var prServiceResult = await _githubPrService
            .CreateOrAdoptPullRequestAsync(execution, attemptId, cancellationToken)
            .ConfigureAwait(false);

        if (prServiceResult.IsConfigurationError)
        {
            if (prServiceResult.IsDefinitiveNoMutationFailure || !prServiceResult.WasPostSent)
            {
                await _executionRepository.SetPullRequestFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            }
            return CreatePullRequestResult.ExternalFailure(prServiceResult.ErrorMessage ?? "GitHub API configuration or authentication error.");
        }

        if (!prServiceResult.Success)
        {
            if (prServiceResult.IsConflict)
            {
                if (prServiceResult.IsDefinitiveNoMutationFailure || !prServiceResult.WasPostSent)
                {
                    await _executionRepository.SetPullRequestFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                }
                return CreatePullRequestResult.Conflict(prServiceResult.ErrorMessage ?? "Pull request creation conflict.");
            }

            // Post was sent with uncertain outcome -> keep InProgress for stale retry recovery
            if (prServiceResult.IsDefinitiveNoMutationFailure || !prServiceResult.WasPostSent)
            {
                await _executionRepository.SetPullRequestFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            }

            return CreatePullRequestResult.ExternalFailure(prServiceResult.ErrorMessage ?? "Pull request operation failed on GitHub.");
        }

        // 6. Persistence & Activity Recording on Success
        var prNumber = prServiceResult.PullRequestNumber!.Value;
        var prUrl = prServiceResult.PullRequestUrl!;
        var resolvedBase = prServiceResult.BaseBranch ?? baseBranch;
        var createdAt = prServiceResult.CreatedAt ?? DateTime.UtcNow;

        await _executionRepository.SetPullRequestOpenedAsync(
            execution.Id,
            attemptId,
            prNumber,
            prUrl,
            resolvedBase,
            createdAt,
            cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _activityRecorder.RecordActivityAsync(
                execution.Id,
                ExecutionStage.PullRequest,
                ExecutionActivityStatus.Completed,
                "Pull request opened",
                metadata: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record PullRequest completed activity for execution {ExecutionId}", execution.Id);
        }

        var responseDto = new CreatePullRequestResponseDto(
            ExecutionId: execution.Id,
            PullRequestStatus: ExecutionPullRequestStatus.Open.ToString(),
            PullRequestNumber: prNumber,
            PullRequestUrl: prUrl,
            BaseBranch: resolvedBase,
            HeadBranch: headBranch,
            HeadCommitSha: execution.RemoteCommitSha!,
            CreatedAt: createdAt);

        return CreatePullRequestResult.Created(responseDto);
    }
}
