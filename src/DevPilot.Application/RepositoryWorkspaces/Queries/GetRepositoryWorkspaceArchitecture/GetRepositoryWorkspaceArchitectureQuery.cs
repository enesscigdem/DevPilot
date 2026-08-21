using DevPilot.Application.RepositoryWorkspaces.Dtos;

namespace DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceArchitecture;

public sealed record GetRepositoryWorkspaceArchitectureQuery(Guid WorkspaceId, bool ForceRecompute = false);

public sealed class GetRepositoryWorkspaceArchitectureResult
{
    public bool Success { get; set; }

    public bool NotFound { get; set; }

    public bool IsConflict { get; set; }

    public string? ErrorMessage { get; set; }

    public WorkspaceArchitectureDto? Architecture { get; set; }
}

public interface IGetRepositoryWorkspaceArchitectureQueryHandler
{
    Task<GetRepositoryWorkspaceArchitectureResult> HandleAsync(
        GetRepositoryWorkspaceArchitectureQuery query,
        CancellationToken cancellationToken = default);
}
