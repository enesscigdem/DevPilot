using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using Microsoft.Extensions.Options;

namespace DevPilot.Application.Executions.Queries.GetExecutionById;

public sealed record GetExecutionByIdQuery(Guid ExecutionId, Guid? RepositoryWorkspaceId = null);

public sealed class GetExecutionByIdResult
{
    public bool Found { get; set; }

    public string? ErrorMessage { get; set; }

    public ExecutionDto? Execution { get; set; }
}

public interface IGetExecutionByIdQueryHandler
{
    Task<GetExecutionByIdResult> HandleAsync(
        GetExecutionByIdQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GetExecutionByIdQueryHandler : IGetExecutionByIdQueryHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionActivityRepository _activityRepository;
    private readonly IImpactAnalysisRepository _impactAnalysisRepository;
    private readonly IOptions<MergePolicyOptions> _mergePolicyOptions;
    private readonly AiPricingOptions? _pricing;
    private readonly IExecutionGitDiffReader? _gitDiffReader;
    private readonly IExecutionRevisionStore? _revisionStore;

    public GetExecutionByIdQueryHandler(
        IExecutionRepository executionRepository,
        IExecutionActivityRepository activityRepository,
        IImpactAnalysisRepository impactAnalysisRepository,
        IOptions<MergePolicyOptions> mergePolicyOptions,
        AiPricingOptions? pricing = null,
        IExecutionGitDiffReader? gitDiffReader = null,
        IExecutionRevisionStore? revisionStore = null)
    {
        _pricing = pricing;
        _gitDiffReader = gitDiffReader;
        _revisionStore = revisionStore;
        _executionRepository = executionRepository;
        _activityRepository = activityRepository;
        _impactAnalysisRepository = impactAnalysisRepository;
        _mergePolicyOptions = mergePolicyOptions;
    }

    public async Task<GetExecutionByIdResult> HandleAsync(
        GetExecutionByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(query.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return new GetExecutionByIdResult
            {
                Found = false,
                ErrorMessage = "Execution not found.",
            };
        }

        if (query.RepositoryWorkspaceId.HasValue &&
            execution.DevelopmentTask.RepositoryWorkspaceId != query.RepositoryWorkspaceId.Value)
        {
            return new GetExecutionByIdResult
            {
                Found = false,
                ErrorMessage = "Execution not found.",
            };
        }

        var activities = await _activityRepository.GetByExecutionIdAsync(execution.Id, cancellationToken).ConfigureAwait(false);
        var allowNoChecks = _mergePolicyOptions.Value.AllowNoChecks;
        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        var hasActive = await _executionRepository
            .HasActiveExecutionForTaskAsync(execution.DevelopmentTaskId, cancellationToken)
            .ConfigureAwait(false);
        // Same rule as the Retry command: failed runs, the latest cancelled run, or a run that needs review.
        var isRetryableCancelled = execution.Status == DevPilot.Domain.Enums.TaskExecutionStatus.Cancelled &&
                                   Commands.RetryExecution.RetryExecutionCommandHandler.IsRetryableCancelled(
                                       execution,
                                       await _executionRepository.GetAllAsync(cancellationToken).ConfigureAwait(false));
        var canRetry = !hasActive &&
                       (execution.Status == DevPilot.Domain.Enums.TaskExecutionStatus.Failed
                        || isRetryableCancelled
                        || (execution.Status == DevPilot.Domain.Enums.TaskExecutionStatus.Completed &&
                            outcome == DevPilot.Domain.Enums.ExecutionVerificationOutcome.NeedsReview));

        var analysis = await _impactAnalysisRepository.GetLatestByTaskIdAsync(execution.DevelopmentTaskId, cancellationToken).ConfigureAwait(false);
        // While a requested fix runs, progress comes from that fix only; the first run is history.
        var revisionActive = ExecutionRevisionScope.IsActive(execution);
        var stages = ExecutionStageEvaluator.EvaluateStages(
            execution,
            execution.DevelopmentTask,
            analysis,
            revisionActive ? ExecutionRevisionScope.Since(execution, activities) : activities);
        if (revisionActive)
        {
            stages = ExecutionRevisionScope.ForActiveRevision(stages);
        }

        var progressPercentage = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        var dto = MapToDto(execution, allowNoChecks, activities, outcome, canRetry, stages, progressPercentage);
        dto.Revisions = await ExecutionRevisionProjector
            .BuildAsync(execution, _revisionStore, activities, outcome, _gitDiffReader, cancellationToken)
            .ConfigureAwait(false);
        dto.Revision = dto.Revisions.LastOrDefault();
        if (revisionActive)
        {
            dto.StartedAt = execution.LastChangeRequestAt;
            dto.VerificationOutcome = dto.Revision?.VerificationOutcome ?? string.Empty;
        }
        var snapshot = ExecutionVerdictBuilder.Resolve(execution, activities, outcome, _pricing);
        dto.Usage = snapshot.Usage;
        if (execution.Status is not (DevPilot.Domain.Enums.TaskExecutionStatus.Pending or DevPilot.Domain.Enums.TaskExecutionStatus.Running))
        {
            dto.Verdict = snapshot.Verdict;
        }

        return new GetExecutionByIdResult
        {
            Found = true,
            Execution = dto,
        };
    }

    private static ExecutionDto MapToDto(
        TaskExecution execution,
        bool allowNoChecks,
        IReadOnlyList<DevPilot.Domain.Entities.ExecutionActivity> activities,
        DevPilot.Domain.Enums.ExecutionVerificationOutcome outcome,
        bool canRetry,
        IReadOnlyList<ExecutionStageStepDto> stages,
        int progressPercentage) =>
        new()
        {
            Id = execution.Id,
            DevelopmentTaskId = execution.DevelopmentTaskId,
            TaskTitle = execution.DevelopmentTask.Title,
            RepositoryWorkspaceId = execution.DevelopmentTask.RepositoryWorkspaceId,
            RepositoryOwner = execution.DevelopmentTask.RepositoryWorkspace.Owner,
            RepositoryName = execution.DevelopmentTask.RepositoryWorkspace.Repository,
            Status = execution.Status,
            CreatedAt = execution.CreatedAt,
            StartedAt = execution.StartedAt,
            CompletedAt = execution.CompletedAt,
            ErrorMessage = execution.ErrorMessage,
            Model = !string.IsNullOrWhiteSpace(execution.Model) ? execution.Model : execution.PinnedAiModelName,
            ReviewStatus = execution.ReviewStatus.ToString(),
            CommitStatus = execution.CommitStatus.ToString(),
            CommitSha = execution.CommitSha,
            CommittedAt = execution.CommittedAt,
            PushStatus = execution.PushStatus.ToString(),
            RemoteBranchName = execution.RemoteBranchName,
            RemoteCommitSha = execution.RemoteCommitSha,
            PushedAt = execution.PushedAt,
            CanRequestPush = execution.ReviewStatus == DevPilot.Domain.Enums.ExecutionReviewStatus.Approved &&
                             execution.CommitStatus == DevPilot.Domain.Enums.ExecutionCommitStatus.Committed &&
                             (execution.PushStatus == DevPilot.Domain.Enums.ExecutionPushStatus.None || execution.PushStatus == DevPilot.Domain.Enums.ExecutionPushStatus.Failed),
            PullRequestStatus = execution.PullRequestStatus.ToString(),
            PullRequestNumber = execution.PullRequestNumber,
            PullRequestUrl = execution.PullRequestUrl,
            PullRequestCreatedAt = execution.PullRequestCreatedAt,
            CanRequestPullRequest = DevPilot.Application.Executions.Commands.CreatePullRequest.CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(execution),
            PullRequestRemoteState = execution.PullRequestRemoteState.ToString(),
            PullRequestIntegrityStatus = execution.PullRequestIntegrityStatus.ToString(),
            PullRequestLastSyncedAt = execution.PullRequestLastSyncedAt,
            CiStatus = execution.CiStatus.ToString(),
            CiChecks = (execution.CiChecks ?? Array.Empty<ExecutionCiCheck>())
                .Select(c => new Commands.SyncPullRequest.ExecutionCiCheckDto(
                    Id: c.Id,
                    ExternalId: c.ExternalId,
                    Name: c.Name,
                    Source: c.Source,
                    CheckType: c.CheckType.ToString(),
                    Status: c.Status,
                    Conclusion: c.Conclusion,
                    StartedAt: c.StartedAt,
                    CompletedAt: c.CompletedAt))
                .ToList(),
            MergeStatus = execution.MergeStatus.ToString(),
            MergeCommitSha = execution.MergeCommitSha,
            MergedAt = execution.MergedAt,
            CanRequestMerge = ExecutionMergeEligibility.EvaluateFromActivities(execution, activities, allowNoChecks).CanMerge,
            VerificationOutcome = outcome.ToString(),
            CanRetry = canRetry,
            CanCancel = DevPilot.Application.Executions.Services.ExecutionCancellationPolicy.DescribeWhyCannotCancel(execution) is null,
            CancelBlockedReason = DevPilot.Application.Executions.Services.ExecutionCancellationPolicy.DescribeWhyCannotCancel(execution),
            CanRequestChanges = DevPilot.Application.Executions.Commands.RequestExecutionChanges.RequestExecutionChangesCommandHandler
                .DescribeWhyChangesCannotBeRequested(execution) is null,
            RevisionCount = execution.RevisionCount,
            LastChangeRequest = execution.LastChangeRequest,
            LastChangeRequestAt = execution.LastChangeRequestAt,
            LastChangeRequestResult = execution.LastChangeRequestResult,
            ProgressPercentage = progressPercentage,
            Stages = stages,
        };
}
