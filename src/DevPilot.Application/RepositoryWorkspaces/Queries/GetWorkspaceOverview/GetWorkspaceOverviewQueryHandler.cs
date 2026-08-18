using DevPilot.Application.RepositoryWorkspaces.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceOverview;

public sealed class GetWorkspaceOverviewQueryHandler : IGetWorkspaceOverviewQueryHandler
{
    private readonly IWorkspaceOverviewReader _overviewReader;
    private readonly ILogger<GetWorkspaceOverviewQueryHandler> _logger;

    public GetWorkspaceOverviewQueryHandler(
        IWorkspaceOverviewReader overviewReader,
        ILogger<GetWorkspaceOverviewQueryHandler> logger)
    {
        _overviewReader = overviewReader;
        _logger = logger;
    }

    public async Task<GetWorkspaceOverviewResult> HandleAsync(
        GetWorkspaceOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            return new GetWorkspaceOverviewResult
            {
                Success = false,
                ErrorMessage = "Query is required.",
            };
        }

        try
        {
            var overview = await _overviewReader
                .ReadOverviewAsync(query.WorkspaceId, cancellationToken)
                .ConfigureAwait(false);

            if (overview is null)
            {
                return new GetWorkspaceOverviewResult
                {
                    Success = false,
                    NotFound = true,
                    ErrorMessage = $"Repository workspace {query.WorkspaceId} was not found.",
                };
            }

            return new GetWorkspaceOverviewResult
            {
                Success = true,
                Overview = overview,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving overview for workspace {WorkspaceId}", query.WorkspaceId);
            return new GetWorkspaceOverviewResult
            {
                Success = false,
                ErrorMessage = "An unexpected error occurred while reading workspace overview.",
            };
        }
    }
}
