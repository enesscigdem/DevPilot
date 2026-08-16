using DevPilot.Domain.Enums;

namespace DevPilot.Domain.ValueObjects;

public sealed class SystemImpact
{
    public string Area { get; set; } = string.Empty;

    public SystemImpactLevel ImpactLevel { get; set; }

    public string Description { get; set; } = string.Empty;
}
