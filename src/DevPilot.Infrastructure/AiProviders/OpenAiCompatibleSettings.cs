namespace DevPilot.Infrastructure.AiProviders;

/// <summary>
/// Everything <see cref="OpenAiCompatibleProvider"/> needs to talk to one endpoint/model.
/// Built either from a stored <c>AiModelConfig</c> or from the legacy <c>AiProvider:Kimi</c> config.
/// </summary>
internal sealed record OpenAiCompatibleSettings
{
    public required string ProviderName { get; init; }

    /// <summary>Human readable name used in error messages (for example "Kimi" or the model's label).</summary>
    public required string DisplayName { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public bool RequiresApiKey { get; init; } = true;

    public string? ReasoningEffort { get; init; }

    public int? MaxOutputTokens { get; init; }

    public bool Stream { get; init; } = true;

    /// <summary>Only send <c>reasoning_effort</c> to models that accept it; others reject unknown fields.</summary>
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>Newer OpenAI models want <c>max_completion_tokens</c> instead of <c>max_tokens</c>.</summary>
    public bool UseMaxCompletionTokens { get; init; }

    public int BaseDelayMs { get; init; } = 1000;

    public int MaxAttempts { get; init; } = 4;

    public int MaxRetryAfterMs { get; init; } = 30000;

    /// <summary>Deadline for one attempt, from sending the request to the last byte of the stream. Zero disables it.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a stream may send nothing (keep-alive comments count) before the attempt is abandoned. Zero disables it.</summary>
    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(90);
}
