using DevPilot.Application.AiProviders;

namespace DevPilot.Infrastructure.AiProviders;

internal sealed class OpenAiAiProvider : IAiProvider
{
    public string ProviderName => AiProviderNames.OpenAI;

    public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var response = new AiResponse
        {
            Model = request.Model,
            Provider = ProviderName,
            Content = $"Stub response from {ProviderName}.",
            InputTokens = 0,
            OutputTokens = 0,
            Duration = TimeSpan.Zero,
            IsSuccess = true,
        };

        return Task.FromResult(response);
    }
}
