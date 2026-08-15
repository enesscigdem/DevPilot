namespace DevPilot.Application.CodeAnalysis;

public interface IRepositoryAnalyzer
{
    Task<RepositoryAnalysisResult> AnalyzeAsync(
        RepositoryAnalysisRequest request,
        CancellationToken cancellationToken = default);
}
