using DevPilot.Application.AiProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.AiProviders;

/// <summary>Builds the concrete provider for a stored model.</summary>
internal interface IAiProviderFactory
{
    /// <summary>Returns null when the adapter type is not implemented yet.</summary>
    IAiProvider? Create(AiModelConfig config, string? apiKey, bool forConnectionTest = false);

    /// <summary>Local runtimes such as Ollama and LM Studio need no API key.</summary>
    bool RequiresApiKey(AiModelConfig config);
}

internal sealed class AiProviderFactory : IAiProviderFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public AiProviderFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public IAiProvider? Create(AiModelConfig config, string? apiKey, bool forConnectionTest = false)
    {
        switch (config.AdapterType)
        {
            case AiAdapterType.OpenAiCompatible:
                var settings = new OpenAiCompatibleSettings
                {
                    ProviderName = config.Name,
                    DisplayName = config.Name,
                    BaseUrl = config.BaseUrl,
                    Model = config.ModelName,
                    ApiKey = apiKey ?? string.Empty,
                    RequiresApiKey = RequiresApiKey(config),
                    MaxOutputTokens = config.MaxOutputTokens,
                    SupportsReasoningEffort = config.SupportsReasoningEffort,
                    UseMaxCompletionTokens = config.UseMaxCompletionTokens,
                    // A connection test must answer quickly and never hammer a failing endpoint.
                    Stream = !forConnectionTest,
                    MaxAttempts = forConnectionTest ? 1 : 4,
                };

                return new OpenAiCompatibleProvider(
                    _httpClientFactory,
                    settings,
                    _loggerFactory.CreateLogger<OpenAiCompatibleProvider>());

            default:
                // Claude and Gemini adapters arrive in a later step.
                return null;
        }
    }

    public bool RequiresApiKey(AiModelConfig config) =>
        !(Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback);
}
