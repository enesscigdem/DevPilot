using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;

namespace DevPilot.Application.Executions.Queries.GetExecutions;

public sealed record GetExecutionsQuery(Guid? RepositoryWorkspaceId = null);

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
    private readonly IExecutionListReader _listReader;

    public GetExecutionsQueryHandler(IExecutionListReader listReader)
    {
        _listReader = listReader;
    }

    public async Task<GetExecutionsResult> HandleAsync(
        GetExecutionsQuery query,
        CancellationToken cancellationToken = default)
    {
        var executions = await _listReader
            .ReadExecutionsListAsync(query.RepositoryWorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        return new GetExecutionsResult
        {
            Executions = executions,
        };
    }
}
