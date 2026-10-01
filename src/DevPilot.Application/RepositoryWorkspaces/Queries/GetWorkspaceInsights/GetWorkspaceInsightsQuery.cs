using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using DevPilot.Application.RepositoryWorkspaces.Services;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceInsights;

public sealed record GetWorkspaceInsightsQuery(Guid WorkspaceId, int WindowSize = 100);

public sealed class GetWorkspaceInsightsResult
{
    public bool Success { get; set; }

    public bool NotFound { get; set; }

    public string? ErrorMessage { get; set; }

    public WorkspaceInsightsDto? Insights { get; set; }
}

public interface IGetWorkspaceInsightsQueryHandler
{
    Task<GetWorkspaceInsightsResult> HandleAsync(
        GetWorkspaceInsightsQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GetWorkspaceInsightsQueryHandler : IGetWorkspaceInsightsQueryHandler
{
    private const int MaxWindow = 200;

    private readonly IWorkspaceInsightsReader _reader;
    private readonly ILogger<GetWorkspaceInsightsQueryHandler> _logger;

    public GetWorkspaceInsightsQueryHandler(
        IWorkspaceInsightsReader reader,
        ILogger<GetWorkspaceInsightsQueryHandler> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    public async Task<GetWorkspaceInsightsResult> HandleAsync(
        GetWorkspaceInsightsQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var window = Math.Clamp(query.WindowSize, 1, MaxWindow);
            var inputs = await _reader.ReadAsync(query.WorkspaceId, window, cancellationToken).ConfigureAwait(false);
            if (inputs is null)
            {
                return new GetWorkspaceInsightsResult { NotFound = true, ErrorMessage = "Repository workspace not found." };
            }

            return new GetWorkspaceInsightsResult
            {
                Success = true,
                Insights = WorkspaceInsightsBuilder.Build(inputs, DateTime.UtcNow),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build insights for workspace {WorkspaceId}.", query.WorkspaceId);
            return new GetWorkspaceInsightsResult { ErrorMessage = "Failed to build repository insights." };
        }
    }
}
