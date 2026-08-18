using DevPilot.Application.ProjectBrain.Ports;

namespace DevPilot.Infrastructure.ProjectBrain.SemanticSearch;

public sealed class NullSemanticSearchService : ISemanticSearchService
{
    public Task<SemanticSearchResult> SearchAsync(
        SemanticSearchQuery query,
        float[]? queryEmbedding,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SemanticSearchResult
        {
            Success = false,
            ErrorMessage = "Semantic search is not available until an embedding provider is configured.",
            ProviderName = "NotConfigured",
        });
    }
}
