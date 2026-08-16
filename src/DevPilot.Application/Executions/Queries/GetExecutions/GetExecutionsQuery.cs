using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;

namespace DevPilot.Application.Executions.Queries.GetExecutions;

public sealed record GetExecutionsQuery();

public sealed class GetExecutionsResult
{
    public IReadOnlyList<ExecutionListItemDto> Executions { get; set; } = [];
}

public interface IGetExecutionsQueryHandler
{
    Task<GetExecutionsResult> HandleAsync(
        GetExecutionsQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GetExecutionsQueryHandler : IGetExecutionsQueryHandler
{
    private readonly IExecutionRepository _executionRepository;

    public GetExecutionsQueryHandler(IExecutionRepository executionRepository)
    {
        _executionRepository = executionRepository;
    }

    public async Task<GetExecutionsResult> HandleAsync(
        GetExecutionsQuery query,
        CancellationToken cancellationToken = default)
    {
        var executions = await _executionRepository
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        return new GetExecutionsResult
        {
            Executions = executions.Select(MapToDto).ToList(),
        };
    }

    private static ExecutionListItemDto MapToDto(TaskExecution execution) =>
        new()
        {
            Id = execution.Id,
            DevelopmentTaskId = execution.DevelopmentTaskId,
            TaskTitle = execution.DevelopmentTask.Title,
            RepositoryName = execution.DevelopmentTask.RepositoryWorkspace.Repository,
            Status = execution.Status,
            CreatedAt = execution.CreatedAt,
        };
}
