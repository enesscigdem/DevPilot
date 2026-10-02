using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

/// <summary>Pins one pipeline stage to a specific model.</summary>
public class AiStageAssignment
{
    public Guid Id { get; set; }

    public AiStage Stage { get; set; }

    public Guid AiModelConfigId { get; set; }

    public AiModelConfig? AiModelConfig { get; set; }

    public DateTime UpdatedAt { get; set; }
}
