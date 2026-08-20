using DevPilot.Application.TaskImpactAnalysis.Dtos;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using TaskImpactAnalysisEntity = DevPilot.Domain.Entities.TaskImpactAnalysis;
namespace DevPilot.Application.TaskImpactAnalysis.Queries.GetTaskImpactAnalysis;

public interface IGetTaskImpactAnalysisQueryHandler
{
    Task<GetTaskImpactAnalysisResult> HandleAsync(
        GetTaskImpactAnalysisQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GetTaskImpactAnalysisQueryHandler : IGetTaskImpactAnalysisQueryHandler
{
    private readonly IImpactAnalysisRepository _analysisRepository;

    public GetTaskImpactAnalysisQueryHandler(IImpactAnalysisRepository analysisRepository)
    {
        _analysisRepository = analysisRepository;
    }

    public async Task<GetTaskImpactAnalysisResult> HandleAsync(
        GetTaskImpactAnalysisQuery query,
        CancellationToken cancellationToken = default)
    {
        var analysis = await _analysisRepository
            .GetLatestByTaskIdAsync(query.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (analysis is null)
        {
            return new GetTaskImpactAnalysisResult
            {
                ErrorMessage = "No impact analysis found for this task.",
            };
        }

        // Auto-reconcile stale InProgress analysis (e.g. after crash / restart)
        if (analysis.Status == ImpactAnalysisStatus.InProgress &&
            analysis.CreatedAt < DateTime.UtcNow - TimeSpan.FromMinutes(5))
        {
            await _analysisRepository
                .ReconcileStaleAnalysesAsync(DateTime.UtcNow - TimeSpan.FromMinutes(5), cancellationToken)
                .ConfigureAwait(false);

            analysis = await _analysisRepository
                .GetLatestByTaskIdAsync(query.TaskId, cancellationToken)
                .ConfigureAwait(false);

            if (analysis is null)
            {
                return new GetTaskImpactAnalysisResult
                {
                    ErrorMessage = "No impact analysis found for this task.",
                };
            }
        }

        return new GetTaskImpactAnalysisResult
        {
            Found = true,
            Analysis = MapToDto(analysis),
        };
    }

    private static ImpactAnalysisDto MapToDto(TaskImpactAnalysisEntity analysis)
    {
        return new ImpactAnalysisDto
        {
            Id = analysis.Id,
            DevelopmentTaskId = analysis.DevelopmentTaskId,
            Status = analysis.Status,
            Summary = analysis.Summary,
            Confidence = analysis.Confidence,
            Model = analysis.Model,
            ProviderName = analysis.ProviderName,
            RawResponse = analysis.RawResponse,
            ErrorMessage = analysis.ErrorMessage,
            StructuredResult = analysis.StructuredResult is null
                ? null
                : MapStructuredResult(analysis.StructuredResult),
            CreatedAt = analysis.CreatedAt,
            CompletedAt = analysis.CompletedAt,
        };
    }

    private static StructuredResultDto MapStructuredResult(ImpactAnalysisResultData data)
    {
        return new StructuredResultDto
        {
            Summary = data.Summary,
            Confidence = data.Confidence,
            ImpactedFiles = data.ImpactedFiles
                .Select(f => new ImpactedFileDto
                {
                    FilePath = f.FilePath,
                    ChangeType = f.ChangeType,
                    Reason = f.Reason,
                    Confidence = f.Confidence,
                })
                .ToList(),
            ProposedPlan = data.ProposedPlan
                .Select(s => new ProposedPlanStepDto
                {
                    Order = s.Order,
                    Title = s.Title,
                    Description = s.Description,
                    RelatedFiles = s.RelatedFiles,
                })
                .ToList(),
            SystemImpacts = data.SystemImpacts
                .Select(i => new SystemImpactDto
                {
                    Area = i.Area,
                    ImpactLevel = i.ImpactLevel,
                    Description = i.Description,
                })
                .ToList(),
            Risks = data.Risks
                .Select(r => new RiskDto
                {
                    Level = r.Level,
                    Description = r.Description,
                    Mitigation = r.Mitigation,
                })
                .ToList(),
        };
    }
}
