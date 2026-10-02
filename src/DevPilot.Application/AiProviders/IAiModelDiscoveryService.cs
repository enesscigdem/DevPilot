namespace DevPilot.Application.AiProviders;

/// <summary>
/// Asks a provider which models an API key can use, so the user picks a valid model name instead of
/// typing one. Invalid input throws <see cref="ArgumentException"/>; provider problems are reported in the result.
/// </summary>
public interface IAiModelDiscoveryService
{
    Task<DiscoverAiModelsResultDto> DiscoverAsync(DiscoverAiModelsRequest request, CancellationToken cancellationToken);
}
