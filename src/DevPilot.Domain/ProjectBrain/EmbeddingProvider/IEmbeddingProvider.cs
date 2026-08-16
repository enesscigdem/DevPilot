namespace DevPilot.Domain.ProjectBrain;

public sealed class EmbeddingResult
{
    public bool Success { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    public List<float[]> Embeddings { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public static EmbeddingResult NotConfigured(string providerName)
    {
        return new EmbeddingResult
        {
            Success = false,
            ProviderName = providerName,
            ErrorMessage = "Embedding provider not configured",
        };
    }
}

public interface IEmbeddingProvider
{
    string ProviderName { get; }

    Task<EmbeddingResult> GenerateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
