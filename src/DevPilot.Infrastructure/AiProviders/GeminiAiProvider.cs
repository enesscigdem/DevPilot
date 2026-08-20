using DevPilot.Application.AiProviders;

namespace DevPilot.Infrastructure.AiProviders;

internal sealed class GeminiAiProvider : IAiProvider
{
    public string ProviderName => AiProviderNames.Gemini;

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
            StatusCode = 200,
            FailureKind = AiFailureKind.None,
        };

        return Task.FromResult(response);
    }
}
