namespace DevPilot.Application.AiProviders;

public sealed class AiRequest
{
    public string Model { get; set; } = string.Empty;

    public string? SystemPrompt { get; set; }

    public string UserPrompt { get; set; } = string.Empty;
}
