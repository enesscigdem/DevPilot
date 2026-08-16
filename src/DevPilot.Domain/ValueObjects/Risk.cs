using DevPilot.Domain.Enums;

namespace DevPilot.Domain.ValueObjects;

public sealed class Risk
{
    public RiskLevel Level { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Mitigation { get; set; } = string.Empty;
}
