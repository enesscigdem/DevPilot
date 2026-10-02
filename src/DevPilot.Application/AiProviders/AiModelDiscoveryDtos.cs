using DevPilot.Domain.Enums;

namespace DevPilot.Application.AiProviders;

public sealed class DiscoverAiModelsRequest
{
    public AiAdapterType AdapterType { get; set; } = AiAdapterType.OpenAiCompatible;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Key typed in the form. Empty means "use the stored key of <see cref="ExistingModelId"/>, if any".</summary>
    public string? ApiKey { get; set; }

    /// <summary>The model being edited; lets the server reuse its stored key without the browser ever seeing it.</summary>
    public Guid? ExistingModelId { get; set; }
}

public sealed class AiModelOptionDto
{
    /// <summary>The identifier to put in the "model name" field.</summary>
    public string Id { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
}

public sealed class DiscoverAiModelsResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<AiModelOptionDto> Models { get; set; } = new();
}
