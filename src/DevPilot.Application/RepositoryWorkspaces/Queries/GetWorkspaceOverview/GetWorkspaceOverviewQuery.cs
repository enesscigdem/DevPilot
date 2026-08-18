using DevPilot.Application.RepositoryWorkspaces.Dtos;

namespace DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceOverview;

public sealed record GetWorkspaceOverviewQuery(Guid WorkspaceId);

public sealed class GetWorkspaceOverviewResult
{
    public bool Success { get; set; }

    public bool NotFound { get; set; }

    public bool IsConflict { get; set; }

    public string? ErrorMessage { get; set; }

    public WorkspaceOverviewDto? Overview { get; set; }
}

public interface IGetWorkspaceOverviewQueryHandler
{
    Task<GetWorkspaceOverviewResult> HandleAsync(
        GetWorkspaceOverviewQuery query,
        CancellationToken cancellationToken = default);
}
