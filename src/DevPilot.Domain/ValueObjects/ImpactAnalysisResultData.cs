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

    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>Base commit / freshness evidence captured when this analysis was produced (null for legacy rows).</summary>
    public AnalysisBaseSnapshot? BaseSnapshot { get; set; }
}

public sealed class AnalysisBaseSnapshot
{
    public string? BranchName { get; set; }

    public string? BaseCommitSha { get; set; }

    public string? RemoteCommitSha { get; set; }

    public int BehindCount { get; set; }

    public int AheadCount { get; set; }

    /// <summary>UpToDate, FastForwarded, Behind, Diverged, Ahead, FetchFailed or NotApplicable.</summary>
    public string Freshness { get; set; } = "NotApplicable";

    public bool IsStale { get; set; }

    public string? Message { get; set; }

    public DateTime CapturedAt { get; set; }
}
