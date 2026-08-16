using DevPilot.Domain.Enums;

namespace DevPilot.Domain.ValueObjects;

public sealed class ImpactedFile
{
    public string FilePath { get; set; } = string.Empty;

    public ImpactFileChangeType ChangeType { get; set; }

    public string Reason { get; set; } = string.Empty;

    public int Confidence { get; set; }
}
