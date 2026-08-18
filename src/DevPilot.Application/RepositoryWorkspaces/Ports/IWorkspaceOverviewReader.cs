using DevPilot.Application.RepositoryWorkspaces.Dtos;

namespace DevPilot.Application.RepositoryWorkspaces.Ports;

public interface IWorkspaceOverviewReader
{
    Task<WorkspaceOverviewDto?> ReadOverviewAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}
