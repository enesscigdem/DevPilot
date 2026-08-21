using DevPilot.Domain.Enums;

namespace DevPilot.Domain.ValueObjects;

public sealed class DatabaseImpact
{
    public bool RequiresSchemaMigration { get; set; }

    public DatabaseMigrationRequirement MigrationRequirement { get; set; } = DatabaseMigrationRequirement.None;

    public int MigrationConfidence { get; set; }

    public DatabaseChangeKind ChangeKind { get; set; } = DatabaseChangeKind.None;

    public RiskLevel DataRiskLevel { get; set; } = RiskLevel.Low;

    public bool RequiresDataMigration { get; set; }

    public DataMigrationRequirement DataMigrationRequirement { get; set; } = DataMigrationRequirement.None;

    public string Summary { get; set; } = string.Empty;

    public List<DatabaseChange> Changes { get; set; } = new();

    public List<string> Evidence { get; set; } = new();

    public List<string> Unknowns { get; set; } = new();
}
