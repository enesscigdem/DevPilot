using DevPilot.Domain.Enums;

namespace DevPilot.Application.TaskImpactAnalysis.Dtos;

public sealed class ImpactedFileDto
{
    public string FilePath { get; set; } = string.Empty;

    public ImpactFileChangeType ChangeType { get; set; }

    public string Reason { get; set; } = string.Empty;

    public int Confidence { get; set; }
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

public sealed class StructuredResultDto
{
    public string Summary { get; set; } = string.Empty;

    public int Confidence { get; set; }

    public IReadOnlyList<ImpactedFileDto> ImpactedFiles { get; set; } = Array.Empty<ImpactedFileDto>();

    public IReadOnlyList<ProposedPlanStepDto> ProposedPlan { get; set; } = Array.Empty<ProposedPlanStepDto>();

    public IReadOnlyList<SystemImpactDto> SystemImpacts { get; set; } = Array.Empty<SystemImpactDto>();

    public IReadOnlyList<RiskDto> Risks { get; set; } = Array.Empty<RiskDto>();

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
