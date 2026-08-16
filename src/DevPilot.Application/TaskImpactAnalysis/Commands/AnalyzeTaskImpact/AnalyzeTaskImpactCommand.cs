using DevPilot.Application.TaskImpactAnalysis.Dtos;

namespace DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;

public sealed record AnalyzeTaskImpactCommand(Guid TaskId);

public sealed class AnalyzeTaskImpactResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public Guid? AnalysisId { get; set; }

    public ImpactAnalysisDto? Analysis { get; set; }
}
