using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.ProjectBrain.Queries.SemanticSearch;

public interface ISemanticSearchQueryHandler
{
    Task<SemanticSearchResult> HandleAsync(
        SemanticSearchQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class SemanticSearchQueryHandler : ISemanticSearchQueryHandler
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ISemanticSearchService _searchService;
    private readonly ILogger<SemanticSearchQueryHandler> _logger;

    public SemanticSearchQueryHandler(
        IEmbeddingProvider embeddingProvider,
        ISemanticSearchService searchService,
        ILogger<SemanticSearchQueryHandler> logger)
    {
        _embeddingProvider = embeddingProvider;
        _searchService = searchService;
        _logger = logger;
    }

    public async Task<SemanticSearchResult> HandleAsync(
        SemanticSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!query.RepositoryWorkspaceId.HasValue && string.IsNullOrWhiteSpace(query.WorkspacePath))
        {
            return new SemanticSearchResult
            {
                Success = false,
                ErrorMessage = "RepositoryWorkspaceId or WorkspacePath is required.",
            };
        }

        if (string.IsNullOrWhiteSpace(query.QueryText))
        {
            return new SemanticSearchResult
            {
                Success = false,
                ErrorMessage = "QueryText is required.",
            };
        }

        float[]? queryEmbedding = null;
        try
        {
            var queryEmbeddingResult = await _embeddingProvider.GenerateAsync(
                new[] { query.QueryText },
                cancellationToken).ConfigureAwait(false);

            if (queryEmbeddingResult.Success && queryEmbeddingResult.Embeddings.Count > 0)
            {
                queryEmbedding = queryEmbeddingResult.Embeddings[0];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed; proceeding with lexical search");
        }

        return await _searchService.SearchAsync(
            query,
            queryEmbedding,
            cancellationToken).ConfigureAwait(false);
    }
}
