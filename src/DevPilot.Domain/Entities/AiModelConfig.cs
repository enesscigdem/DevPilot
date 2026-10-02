using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

/// <summary>A model the user registered in the panel (endpoint, credentials and capabilities).</summary>
public class AiModelConfig
{
    public Guid Id { get; set; }

    /// <summary>Label shown in the UI, for example "DeepSeek V3 (work)".</summary>
    public string Name { get; set; } = string.Empty;

    public AiAdapterType AdapterType { get; set; } = AiAdapterType.OpenAiCompatible;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The vendor's model identifier, for example "deepseek-chat" or "claude-opus-5-5".</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>API key encrypted with ASP.NET Data Protection. Never returned by the API.</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>Last characters of the key, kept so the UI can show "sk-...a3f9" without decrypting.</summary>
    public string? ApiKeyHint { get; set; }

    public int? MaxOutputTokens { get; set; }

    public bool SupportsReasoningEffort { get; set; }

    /// <summary>Send <c>max_completion_tokens</c> instead of <c>max_tokens</c> (newer OpenAI models).</summary>
    public bool UseMaxCompletionTokens { get; set; }

    public decimal? InputPricePerMillionTokensUsd { get; set; }

    public decimal? OutputPricePerMillionTokensUsd { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Used for every stage that has no explicit assignment. At most one model is the default.</summary>
    public bool IsDefault { get; set; }

    public DateTime? LastTestedAt { get; set; }

    public bool? LastTestSucceeded { get; set; }

    public string? LastTestMessage { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
