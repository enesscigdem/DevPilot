using DevPilot.Domain.Entities;

namespace DevPilot.Application.Tasks.Ports;

public interface IRepositoryWorkspaceQuery
{
    Task<RepositoryWorkspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
