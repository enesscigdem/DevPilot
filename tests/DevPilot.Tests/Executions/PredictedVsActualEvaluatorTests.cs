using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using Xunit;
using TaskImpactAnalysisEntity = DevPilot.Domain.Entities.TaskImpactAnalysis;

namespace DevPilot.Tests.Executions;

public sealed class PredictedVsActualEvaluatorTests
{
    [Fact]
    public void Evaluate_IdentifiesMatchedUnexpectedAndMissingFilesDeterministically()
    {
        var impactAnalysis = new TaskImpactAnalysisEntity
        {
            Id = Guid.NewGuid(),
            StructuredResult = new ImpactAnalysisResultData
            {
                Summary = "Impact summary",
                ImpactedFiles = new List<ImpactedFile>
                {
                    new() { FilePath = "src/DevPilot.Api/Controllers/TasksController.cs", ChangeType = ImpactFileChangeType.Modify },
                    new() { FilePath = "src/DevPilot.Domain/Entities/Task.cs", ChangeType = ImpactFileChangeType.Modify },
                    new() { FilePath = "src/DevPilot.Application/Services/OldService.cs", ChangeType = ImpactFileChangeType.Modify }
                },
                ChangeBrief = new ChangeBrief
                {
                    ExpectedChecks = new List<ExpectedVerificationCheck>
                    {
                        new() { CheckId = "Build", DisplayName = "Build", Kind = "Build" },
                        new() { CheckId = "Test", DisplayName = "Test", Kind = "Test" }
                    }
                }
            }
        };

        var actualFiles = new List<ExecutionReviewFileDto>
        {
            new("src/DevPilot.Api/Controllers/TasksController.cs", "Modified"),
            new("src/DevPilot.Domain/Entities/Task.cs", "Modified"),
            new("src/DevPilot.Infrastructure/NewHelper.cs", "Added")
        };

        var activities = new List<ExecutionActivity>
        {
            new() { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed },
            new() { Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed }
        };

        var comparison = PredictedVsActualEvaluator.Evaluate(impactAnalysis, actualFiles, activities);

        Assert.Equal(2, comparison.MatchedFiles.Count);
        Assert.Contains("src/DevPilot.Api/Controllers/TasksController.cs", comparison.MatchedFiles);
        Assert.Contains("src/DevPilot.Domain/Entities/Task.cs", comparison.MatchedFiles);

        Assert.Single(comparison.UnexpectedFiles);
        Assert.Equal("src/DevPilot.Infrastructure/NewHelper.cs", comparison.UnexpectedFiles[0]);

        Assert.Single(comparison.MissingPredictedFiles);
        Assert.Equal("src/DevPilot.Application/Services/OldService.cs", comparison.MissingPredictedFiles[0]);

        Assert.True(comparison.AllExpectedChecksExecuted);
        Assert.Contains(comparison.DimensionObservations, obs => obs.Contains("API dimension confirmed"));
        Assert.Contains(comparison.DimensionObservations, obs => obs.Contains("DATA dimension confirmed"));
        Assert.Contains(comparison.DimensionObservations, obs => obs.Contains("1 unexpected file(s) modified"));
    }

    [Fact]
    public void Evaluate_HandlesNullImpactAnalysisGracefully()
    {
        var actualFiles = new List<ExecutionReviewFileDto>
        {
            new("src/DevPilot.Api/Controllers/TasksController.cs", "Modified")
        };

        var activities = new List<ExecutionActivity>
        {
            new() { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed }
        };

        var comparison = PredictedVsActualEvaluator.Evaluate(null, actualFiles, activities);

        Assert.Empty(comparison.PredictedFiles);
        Assert.Single(comparison.ActualFiles);
        Assert.Single(comparison.UnexpectedFiles);
        Assert.True(comparison.AllExpectedChecksExecuted);
    }
}
