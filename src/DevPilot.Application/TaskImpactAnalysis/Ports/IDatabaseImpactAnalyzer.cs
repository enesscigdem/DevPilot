using DevPilot.Application.TaskImpactAnalysis.Services;
using DevPilot.Domain.ValueObjects;

namespace DevPilot.Application.TaskImpactAnalysis.Ports;

public interface IDatabaseImpactAnalyzer
{
    DatabaseImpact AnalyzeImpact(
        IReadOnlyList<ImpactedFile> impactedFiles,
        IReadOnlyList<ChangeDimensionImpact> dimensions,
        IReadOnlyList<Risk> risks,
        RepositoryEvidenceProfile evidence,
        string? taskPrompt = null,
        string? workspaceRoot = null);
}
