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
        if (string.IsNullOrWhiteSpace(query.WorkspacePath))
        {
            return new SemanticSearchResult
            {
                Success = false,
                ErrorMessage = "WorkspacePath is required.",
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

        var queryEmbeddingResult = await _embeddingProvider.GenerateAsync(
            new[] { query.QueryText },
            cancellationToken).ConfigureAwait(false);

        if (!queryEmbeddingResult.Success)
        {
            _logger.LogWarning(
                "Semantic search failed for workspace {WorkspacePath}: {Error}",
                query.WorkspacePath,
                queryEmbeddingResult.ErrorMessage);

            return new SemanticSearchResult
            {
                Success = false,
                ErrorMessage = queryEmbeddingResult.ErrorMessage ?? "Embedding provider not configured",
                ProviderName = _embeddingProvider.ProviderName,
            };
        }

        if (queryEmbeddingResult.Embeddings.Count == 0 || queryEmbeddingResult.Embeddings[0] is null)
        {
            return new SemanticSearchResult
            {
                Success = false,
                ErrorMessage = "Embedding provider returned an empty query embedding.",
                ProviderName = _embeddingProvider.ProviderName,
            };
        }

        return await _searchService.SearchAsync(
            query,
            queryEmbeddingResult.Embeddings[0]!,
            cancellationToken).ConfigureAwait(false);
    }
}
