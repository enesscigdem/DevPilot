using System.Text.Json;

namespace DevPilot.Domain.ValueObjects;

public sealed class ImpactAnalysisResultData
{
    public string Summary { get; set; } = string.Empty;

    public int Confidence { get; set; }

    public List<ImpactedFile> ImpactedFiles { get; set; } = new();

    public List<ProposedPlanStep> ProposedPlan { get; set; } = new();

    public List<SystemImpact> SystemImpacts { get; set; } = new();

    public List<Risk> Risks { get; set; } = new();

    public Dictionary<string, JsonElement>? Metadata { get; set; }
}
