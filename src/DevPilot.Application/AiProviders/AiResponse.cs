namespace DevPilot.Application.AiProviders;

public sealed class AiResponse
{
    public string Model { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public TimeSpan Duration { get; set; }

    public bool IsSuccess { get; set; }

    public string? ErrorMessage { get; set; }
}
