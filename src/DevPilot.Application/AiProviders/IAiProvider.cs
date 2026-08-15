namespace DevPilot.Application.AiProviders;

public interface IAiProvider
{
    string ProviderName { get; }

    Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default);
}
