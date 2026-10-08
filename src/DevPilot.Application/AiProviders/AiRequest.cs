using DevPilot.Domain.Enums;

namespace DevPilot.Application.AiProviders;

public sealed class AiRequest
{
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Pipeline stage issuing the call. The router uses it to pick the model the user assigned to
    /// that stage; when null (or unassigned) the default model is used.
    /// </summary>
    public AiStage? Stage { get; set; }

    public string? SystemPrompt { get; set; }

    public string UserPrompt { get; set; } = string.Empty;

    public int? MaxTokens { get; set; }

    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Caps the provider's own retries for this call (never raises them). The router lowers it when another model can
    /// take over, so a rate-limited model is not retried for minutes before the fallback gets its turn.
    /// </summary>
    public int? MaxAttempts { get; set; }

    /// <summary>Called as attempts time out, fail or are retried, so the caller can show it. Never throws into the provider.</summary>
    public Func<AiAttemptEvent, Task>? OnAttempt { get; set; }
}
