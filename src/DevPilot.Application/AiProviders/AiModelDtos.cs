using DevPilot.Domain.Enums;

namespace DevPilot.Application.AiProviders;

public sealed class AiModelDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public AiAdapterType AdapterType { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public bool HasApiKey { get; set; }

    /// <summary>Masked key such as "...a3f9". The full key is never returned.</summary>
    public string? ApiKeyHint { get; set; }

    public int? MaxOutputTokens { get; set; }

    public bool SupportsReasoningEffort { get; set; }

    public bool UseMaxCompletionTokens { get; set; }

    public decimal? InputPricePerMillionTokensUsd { get; set; }

    public decimal? OutputPricePerMillionTokensUsd { get; set; }

    public bool IsEnabled { get; set; }

    public bool IsDefault { get; set; }

    public DateTime? LastTestedAt { get; set; }

    public bool? LastTestSucceeded { get; set; }

    public string? LastTestMessage { get; set; }

    /// <summary>Why the last test ended: Ok, Timeout, HttpError, NetworkError or Failed.</summary>
    public string? LastTestOutcome { get; set; }

    /// <summary>HTTP status the provider answered with when the last test failed with one.</summary>
    public int? LastTestStatusCode { get; set; }
}

public sealed class SaveAiModelRequest
{
    public string Name { get; set; } = string.Empty;

    public AiAdapterType AdapterType { get; set; } = AiAdapterType.OpenAiCompatible;

    public string BaseUrl { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    /// <summary>On create: the key (optional for local runtimes). On update: null/empty keeps the stored key.</summary>
    public string? ApiKey { get; set; }

    public int? MaxOutputTokens { get; set; }

    public bool SupportsReasoningEffort { get; set; }

    public bool UseMaxCompletionTokens { get; set; }

    public decimal? InputPricePerMillionTokensUsd { get; set; }

    public decimal? OutputPricePerMillionTokensUsd { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool IsDefault { get; set; }
}

public sealed class AiModelTestResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public string? Model { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    /// <summary>Ok, Timeout (our time limit, the provider never answered), HttpError (the provider answered with an error status), NetworkError or Failed.</summary>
    public string Outcome { get; set; } = "Failed";

    public int? StatusCode { get; set; }

    /// <summary>The limit a Timeout outcome refers to.</summary>
    public int TimeLimitSeconds { get; set; }
}

public sealed class AiStageAssignmentDto
{
    public AiStage Stage { get; set; }

    /// <summary>Null means the stage uses the default model.</summary>
    public Guid? AiModelConfigId { get; set; }
}
