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

    public GetExecutionByIdQueryHandler(
        IExecutionRepository executionRepository,
        IExecutionActivityRepository activityRepository,
        IImpactAnalysisRepository impactAnalysisRepository,
        IOptions<MergePolicyOptions> mergePolicyOptions)
    {
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
        var buildPassed = activities.Any(a => a.Stage == DevPilot.Domain.Enums.ExecutionStage.Build && a.Status == DevPilot.Domain.Enums.ExecutionActivityStatus.Completed);
        var testPassed = activities.Any(a => a.Stage == DevPilot.Domain.Enums.ExecutionStage.Test && a.Status == DevPilot.Domain.Enums.ExecutionActivityStatus.Completed);
        var allowNoChecks = _mergePolicyOptions.Value.AllowNoChecks;

        var analysis = await _impactAnalysisRepository.GetLatestByTaskIdAsync(execution.DevelopmentTaskId, cancellationToken).ConfigureAwait(false);
        var stages = ExecutionStageEvaluator.EvaluateStages(execution, execution.DevelopmentTask, analysis, activities);
        var progressPercentage = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        return new GetExecutionByIdResult
        {
            Found = true,
            Execution = MapToDto(execution, allowNoChecks, buildPassed, testPassed, stages, progressPercentage),
        };
    }

    private static ExecutionDto MapToDto(
        TaskExecution execution,
        bool allowNoChecks,
        bool buildPassed,
        bool testPassed,
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
            CanRequestMerge = DevPilot.Application.Executions.Services.ExecutionMergeEligibility.CalculateCanRequestMerge(execution, allowNoChecks, buildPassed, testPassed),
            ProgressPercentage = progressPercentage,
            Stages = stages,
        };
}
