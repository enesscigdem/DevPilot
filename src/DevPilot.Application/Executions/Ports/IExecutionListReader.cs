using DevPilot.Application.Executions.Dtos;

namespace DevPilot.Application.Executions.Ports;

public interface IExecutionListReader
{
    Task<IReadOnlyList<ExecutionListItemDto>> ReadExecutionsListAsync(
        Guid? workspaceId,
        CancellationToken cancellationToken = default);
}
