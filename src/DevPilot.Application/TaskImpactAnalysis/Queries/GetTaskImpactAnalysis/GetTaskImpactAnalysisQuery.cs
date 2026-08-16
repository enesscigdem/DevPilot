using DevPilot.Application.TaskImpactAnalysis.Dtos;

namespace DevPilot.Application.TaskImpactAnalysis.Queries.GetTaskImpactAnalysis;

public sealed record GetTaskImpactAnalysisQuery(Guid TaskId);

public sealed class GetTaskImpactAnalysisResult
{
    public bool Found { get; set; }

    public string? ErrorMessage { get; set; }

    public ImpactAnalysisDto? Analysis { get; set; }
}
