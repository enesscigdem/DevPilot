using DevPilot.Application.RepositoryWorkspaces.Dtos;

namespace DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceAnalysis;

public sealed record GetRepositoryWorkspaceAnalysisQuery(Guid WorkspaceId, bool ForceRecompute = false);

public sealed class GetRepositoryWorkspaceAnalysisResult
{
    public bool Success { get; set; }

    public bool NotFound { get; set; }

    public bool IsConflict { get; set; }

    public string? ErrorMessage { get; set; }

    public WorkspaceAnalysisDto? Analysis { get; set; }
}

public interface IGetRepositoryWorkspaceAnalysisQueryHandler
{
    Task<GetRepositoryWorkspaceAnalysisResult> HandleAsync(
        GetRepositoryWorkspaceAnalysisQuery query,
        CancellationToken cancellationToken = default);
}
