using DevPilot.Application.ProjectBrain.Models;

namespace DevPilot.Application.ProjectBrain.Queries.GetBrainStatus;

public sealed record GetBrainStatusQuery(Guid WorkspaceId);

public sealed class GetBrainStatusResult
{
    public bool Success { get; set; }

    public bool NotFound { get; set; }

    public string? ErrorMessage { get; set; }

    public BrainStatusDto? Status { get; set; }
}

public interface IGetBrainStatusQueryHandler
{
    Task<GetBrainStatusResult> HandleAsync(
        GetBrainStatusQuery query,
        CancellationToken cancellationToken = default);
}
