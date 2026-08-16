using DevPilot.Domain.Entities;

namespace DevPilot.Application.Executions.Ports;

public interface IExecutionActivityRepository
{
    Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(
        Guid executionId,
        CancellationToken cancellationToken = default);
}
