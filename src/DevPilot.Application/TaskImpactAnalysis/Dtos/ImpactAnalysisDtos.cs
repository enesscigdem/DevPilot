using DevPilot.Domain.Enums;

namespace DevPilot.Application.TaskImpactAnalysis.Dtos;

public sealed class ImpactedFileDto
{
    public string FilePath { get; set; } = string.Empty;

    public ImpactFileChangeType ChangeType { get; set; }

    public string Reason { get; set; } = string.Empty;

    public int Confidence { get; set; }

    public string EvidenceType { get; set; } = "Inferred";

    public string? EvidenceDetails { get; set; }

    public bool IsUncertain { get; set; }
}

public sealed class ProposedPlanStepDto
{
    public int Order { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> RelatedFiles { get; set; } = new();
}

public sealed class SystemImpactDto
{
    public string Area { get; set; } = string.Empty;

    public SystemImpactLevel ImpactLevel { get; set; }

    public string Description { get; set; } = string.Empty;
}

public sealed class RiskDto
{
    public RiskLevel Level { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Mitigation { get; set; } = string.Empty;
}

public sealed class ExpectedVerificationCheckDto
{
    public string CheckId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public bool Required { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? DiscoveryEvidence { get; set; }
}

public sealed class DatabaseChangeDto
{
    public DatabaseObjectType ObjectType { get; set; } = DatabaseObjectType.Unknown;

    public string ObjectName { get; set; } = string.Empty;

    public string? ParentObjectName { get; set; }

    public DatabaseChangeOperation Operation { get; set; } = DatabaseChangeOperation.Unknown;

    public string? Before { get; set; }

    public string? After { get; set; }

    public RiskLevel Risk { get; set; } = RiskLevel.Low;

    public string Evidence { get; set; } = string.Empty;
}

public sealed class DatabaseImpactDto
{
    public bool RequiresSchemaMigration { get; set; }

    public DatabaseMigrationRequirement MigrationRequirement { get; set; } = DatabaseMigrationRequirement.None;

    public int MigrationConfidence { get; set; }

    public DatabaseChangeKind ChangeKind { get; set; } = DatabaseChangeKind.None;

    public RiskLevel DataRiskLevel { get; set; } = RiskLevel.Low;

    public bool RequiresDataMigration { get; set; }

    public DataMigrationRequirement DataMigrationRequirement { get; set; } = DataMigrationRequirement.None;

    public string Summary { get; set; } = string.Empty;

    public IReadOnlyList<DatabaseChangeDto> Changes { get; set; } = Array.Empty<DatabaseChangeDto>();

    public IReadOnlyList<string> Evidence { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> Unknowns { get; set; } = Array.Empty<string>();
}

public sealed class ChangeBriefDto
{
    public int FileCount { get; set; }

    public int ProjectCount { get; set; }

    public RiskLevel RiskLevel { get; set; } = RiskLevel.Low;

    public IReadOnlyList<string> RiskReasons { get; set; } = Array.Empty<string>();

    public string? ApiSummary { get; set; }

    public string? DataSummary { get; set; }

    public string? RuntimeSummary { get; set; }

    public string? TestsSummary { get; set; }

    public string? VerificationSummary { get; set; }

    public IReadOnlyList<ExpectedVerificationCheckDto> ExpectedChecks { get; set; } = Array.Empty<ExpectedVerificationCheckDto>();

    public DatabaseImpactDto? DatabaseImpact { get; set; }

    public IReadOnlyList<string> Unknowns { get; set; } = Array.Empty<string>();
}

public sealed class ChangeDimensionImpactDto
{
    public string Area { get; set; } = string.Empty;

    public SystemImpactLevel ImpactLevel { get; set; }

    public string Summary { get; set; } = string.Empty;

    public IReadOnlyList<string> Details { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> Evidence { get; set; } = Array.Empty<string>();
}

public sealed class StructuredResultDto
{
    public string Summary { get; set; } = string.Empty;

    public int Confidence { get; set; }

    public IReadOnlyList<ImpactedFileDto> ImpactedFiles { get; set; } = Array.Empty<ImpactedFileDto>();

    public IReadOnlyList<ProposedPlanStepDto> ProposedPlan { get; set; } = Array.Empty<ProposedPlanStepDto>();

    public IReadOnlyList<SystemImpactDto> SystemImpacts { get; set; } = Array.Empty<SystemImpactDto>();

    public IReadOnlyList<RiskDto> Risks { get; set; } = Array.Empty<RiskDto>();

    public ChangeBriefDto? ChangeBrief { get; set; }

    public DatabaseImpactDto? DatabaseImpact { get; set; }

    public IReadOnlyList<ChangeDimensionImpactDto> Dimensions { get; set; } = Array.Empty<ChangeDimensionImpactDto>();

    public IReadOnlyList<string> Unknowns { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> RiskReasons { get; set; } = Array.Empty<string>();

    public Dictionary<string, object>? Metadata { get; set; }
}

public sealed class ImpactAnalysisDto
{
    public Guid Id { get; set; }

    public Guid DevelopmentTaskId { get; set; }

    public ImpactAnalysisStatus Status { get; set; }

    public string Summary { get; set; } = string.Empty;

    public int Confidence { get; set; }

    public string? Model { get; set; }

    public string? ProviderName { get; set; }

    public string? RawResponse { get; set; }

    public string? ErrorMessage { get; set; }

    public StructuredResultDto? StructuredResult { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
