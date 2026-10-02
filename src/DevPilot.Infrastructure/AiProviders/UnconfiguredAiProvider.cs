using DevPilot.Application.AiProviders;

namespace DevPilot.Infrastructure.AiProviders;

/// <summary>
/// Fallback used when no model was added in the panel and <c>AiProvider:Provider</c> does not name a
/// configured provider. Fails loudly instead of returning made-up content.
/// </summary>
internal sealed class UnconfiguredAiProvider : IAiProvider
{
    public string ProviderName => "Unconfigured";

    public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiResponse
        {
            Provider = ProviderName,
            Model = request.Model,
            IsSuccess = false,
            FailureKind = AiFailureKind.Permanent,
            ErrorMessage = "No AI model is configured. Add one under Settings > AI models.",
        });
}
