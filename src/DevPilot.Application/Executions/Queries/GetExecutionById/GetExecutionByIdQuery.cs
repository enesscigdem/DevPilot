using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;

namespace DevPilot.Application.Executions.Queries.GetExecutionById;

public sealed record GetExecutionByIdQuery(Guid ExecutionId);

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

    public GetExecutionByIdQueryHandler(IExecutionRepository executionRepository)
    {
        _executionRepository = executionRepository;
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

        return new GetExecutionByIdResult
        {
            Found = true,
            Execution = MapToDto(execution),
        };
    }

    private static ExecutionDto MapToDto(TaskExecution execution) =>
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
        };
}
