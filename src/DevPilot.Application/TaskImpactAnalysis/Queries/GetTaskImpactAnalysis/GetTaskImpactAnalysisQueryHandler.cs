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
                    EvidenceType = f.EvidenceType,
                    EvidenceDetails = f.EvidenceDetails,
                    IsUncertain = f.IsUncertain,
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
            ChangeBrief = data.ChangeBrief is null
                ? null
                : new ChangeBriefDto
                {
                    FileCount = data.ChangeBrief.FileCount,
                    ProjectCount = data.ChangeBrief.ProjectCount,
                    RiskLevel = data.ChangeBrief.RiskLevel,
                    RiskReasons = data.ChangeBrief.RiskReasons,
                    ApiSummary = data.ChangeBrief.ApiSummary,
                    DataSummary = data.ChangeBrief.DataSummary,
                    RuntimeSummary = data.ChangeBrief.RuntimeSummary,
                    TestsSummary = data.ChangeBrief.TestsSummary,
                    VerificationSummary = data.ChangeBrief.VerificationSummary,
                    ExpectedChecks = data.ChangeBrief.ExpectedChecks
                        .Select(c => new ExpectedVerificationCheckDto
                        {
                            CheckId = c.CheckId,
                            DisplayName = c.DisplayName,
                            Kind = c.Kind,
                            Required = c.Required,
                            Source = c.Source,
                            DiscoveryEvidence = c.DiscoveryEvidence,
                        })
                        .ToList(),
                    Unknowns = data.ChangeBrief.Unknowns,
                    DatabaseImpact = MapDatabaseImpact(data.ChangeBrief.DatabaseImpact),
                },
            DatabaseImpact = MapDatabaseImpact(data.DatabaseImpact),
            Dimensions = data.Dimensions
                .Select(d => new ChangeDimensionImpactDto
                {
                    Area = d.Area,
                    ImpactLevel = d.ImpactLevel,
                    Summary = d.Summary,
                    Details = d.Details,
                    Evidence = d.Evidence,
                })
                .ToList(),
            Unknowns = data.Unknowns,
            RiskReasons = data.RiskReasons,
        };
    }

    private static DatabaseImpactDto? MapDatabaseImpact(DatabaseImpact? impact)
    {
        if (impact is null) return null;
        return new DatabaseImpactDto
        {
            RequiresSchemaMigration = impact.RequiresSchemaMigration,
            MigrationRequirement = impact.MigrationRequirement,
            MigrationConfidence = impact.MigrationConfidence,
            ChangeKind = impact.ChangeKind,
            DataRiskLevel = impact.DataRiskLevel,
            RequiresDataMigration = impact.RequiresDataMigration,
            DataMigrationRequirement = impact.DataMigrationRequirement,
            Summary = impact.Summary,
            Changes = impact.Changes.Select(c => new DatabaseChangeDto
            {
                ObjectType = c.ObjectType,
                ObjectName = c.ObjectName,
                ParentObjectName = c.ParentObjectName,
                Operation = c.Operation,
                Before = c.Before,
                After = c.After,
                Risk = c.Risk,
                Evidence = c.Evidence,
            }).ToList(),
            Evidence = impact.Evidence,
            Unknowns = impact.Unknowns,
        };
    }
}
