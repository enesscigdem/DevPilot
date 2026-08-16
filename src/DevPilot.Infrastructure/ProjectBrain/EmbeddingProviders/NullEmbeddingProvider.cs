using DevPilot.Domain.ProjectBrain;

namespace DevPilot.Infrastructure.ProjectBrain.EmbeddingProviders;

public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public string ProviderName => "NotConfigured";

    public Task<EmbeddingResult> GenerateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(EmbeddingResult.NotConfigured(ProviderName));
    }
}
