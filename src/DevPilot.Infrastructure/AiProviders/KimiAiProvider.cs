using DevPilot.Application.AiProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.AiProviders;

/// <summary>
/// Legacy, configuration-driven provider (<c>AiProvider:Kimi:*</c>). It is the fallback used when no
/// model has been added through the panel, so existing installations keep working unchanged.
/// </summary>
internal sealed class KimiAiProvider : OpenAiCompatibleProvider
{
    public KimiAiProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<KimiAiProvider> logger)
        : base(httpClientFactory, BuildSettings(configuration), logger)
    {
    }

    private static OpenAiCompatibleSettings BuildSettings(IConfiguration configuration)
    {
        var defaults = new OpenAiCompatibleSettings { ProviderName = AiProviderNames.Kimi, DisplayName = "Kimi" };

        // API key is supplied via configuration (environment variable / secret).
        // Supported keys: AiProvider:Kimi:ApiKey (mapped from AiProvider__Kimi__ApiKey) or KIMI_API_KEY.
        return defaults with
        {
            BaseUrl = configuration["AiProvider:Kimi:BaseUrl"] ?? string.Empty,
            Model = configuration["AiProvider:Kimi:Model"]
                ?? configuration["AiProvider:Model"]
                ?? "kimi-k2.7-code",
            ApiKey = configuration["AiProvider:Kimi:ApiKey"]
                ?? configuration["KIMI_API_KEY"]
                ?? string.Empty,
            ReasoningEffort = configuration["AiProvider:Kimi:ReasoningEffort"],
            SupportsReasoningEffort = true,
            MaxOutputTokens = ReadInt(configuration, "MaxOutputTokens"),
            // Streaming defaults to on to prevent idle-connection proxy drops (503/504).
            Stream = bool.TryParse(configuration["AiProvider:Kimi:Stream"], out var stream) ? stream : true,
            BaseDelayMs = ReadInt(configuration, "BaseDelayMs") ?? defaults.BaseDelayMs,
            MaxAttempts = ReadInt(configuration, "MaxAttempts") ?? defaults.MaxAttempts,
            MaxRetryAfterMs = ReadInt(configuration, "MaxRetryAfterMs") ?? defaults.MaxRetryAfterMs,
        };
    }

    private static int? ReadInt(IConfiguration configuration, string key) =>
        int.TryParse(configuration[$"AiProvider:Kimi:{key}"], out var value) && value > 0 ? value : null;
}
