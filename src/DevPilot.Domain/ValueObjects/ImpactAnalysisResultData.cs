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

    public ChangeBrief? ChangeBrief { get; set; }

    public List<ChangeDimensionImpact> Dimensions { get; set; } = new();

    public List<string> Unknowns { get; set; } = new();

    public List<string> RiskReasons { get; set; } = new();

    public DatabaseImpact? DatabaseImpact { get; set; }

    public bool IsGroundingUnresolved { get; set; }

    public string? UnresolvedSubject { get; set; }

    public string? UnresolvedReason { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }
}
