namespace DevPilot.Infrastructure.AiProviders;

/// <summary>Connection details for the vendor-specific adapters (Claude, Gemini).</summary>
internal sealed record AiEndpointSettings
{
    public required string ProviderName { get; init; }

    /// <summary>Human readable name used in error messages.</summary>
    public required string DisplayName { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public bool RequiresApiKey { get; init; } = true;

    public int? MaxOutputTokens { get; init; }

    public int MaxAttempts { get; init; } = 4;
}
