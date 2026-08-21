using DevPilot.Domain.Enums;

namespace DevPilot.Domain.ValueObjects;

public sealed class ExpectedVerificationCheck
{
    public string CheckId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public bool Required { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? DiscoveryEvidence { get; set; }
}

public sealed class ChangeBrief
{
    public int FileCount { get; set; }

    public int ProjectCount { get; set; }

    public RiskLevel RiskLevel { get; set; } = RiskLevel.Low;

    public List<string> RiskReasons { get; set; } = new();

    public string? ApiSummary { get; set; }

    public string? DataSummary { get; set; }

    public string? RuntimeSummary { get; set; }

    public string? TestsSummary { get; set; }

    public string? VerificationSummary { get; set; }

    public List<ExpectedVerificationCheck> ExpectedChecks { get; set; } = new();

    public DatabaseImpact? DatabaseImpact { get; set; }

    public List<string> Unknowns { get; set; } = new();
}
