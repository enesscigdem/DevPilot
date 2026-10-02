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
}
