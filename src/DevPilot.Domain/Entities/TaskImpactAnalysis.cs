using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;

namespace DevPilot.Domain.Entities;

public class TaskImpactAnalysis
{
    public Guid Id { get; set; }

    public Guid DevelopmentTaskId { get; set; }

    public DevelopmentTask DevelopmentTask { get; set; } = null!;

    public ImpactAnalysisStatus Status { get; set; }

    public string Summary { get; set; } = string.Empty;

    public int Confidence { get; set; }

    public string? Model { get; set; }

    public string? ProviderName { get; set; }

    public string? RawResponse { get; set; }

    public ImpactAnalysisResultData? StructuredResult { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
