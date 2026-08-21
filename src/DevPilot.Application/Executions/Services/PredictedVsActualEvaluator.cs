using DevPilot.Application.Executions.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using TaskImpactAnalysisEntity = DevPilot.Domain.Entities.TaskImpactAnalysis;

namespace DevPilot.Application.Executions.Services;

public static class PredictedVsActualEvaluator
{
    public static PredictedVsActualComparisonDto Evaluate(
        TaskImpactAnalysisEntity? impactAnalysis,
        IReadOnlyList<ExecutionReviewFileDto> actualChangedFiles,
        IReadOnlyList<ExecutionActivity> activities)
    {
        var predictedItems = new List<PredictedFileActionItemDto>();
        var expectedChecksList = new List<string>();

        if (impactAnalysis?.StructuredResult != null)
        {
            var res = impactAnalysis.StructuredResult;
            if (res.ImpactedFiles != null)
            {
                foreach (var f in res.ImpactedFiles)
                {
                    if (string.IsNullOrWhiteSpace(f.FilePath)) continue;
                    predictedItems.Add(new PredictedFileActionItemDto(
                        FilePath: NormalizePath(f.FilePath),
                        Action: f.ChangeType.ToString(),
                        EvidenceType: f.EvidenceType,
                        IsUncertain: f.IsUncertain));
                }
            }

            if (res.ChangeBrief?.ExpectedChecks != null && res.ChangeBrief.ExpectedChecks.Count > 0)
            {
                expectedChecksList.AddRange(res.ChangeBrief.ExpectedChecks.Select(c => c.DisplayName ?? c.CheckId));
            }
        }

        var actualItems = new List<ActualFileActionItemDto>();
        foreach (var f in actualChangedFiles)
        {
            if (string.IsNullOrWhiteSpace(f.Path)) continue;
            actualItems.Add(new ActualFileActionItemDto(
                FilePath: NormalizePath(f.Path),
                Action: f.ChangeType));
        }

        var predictedPaths = new HashSet<string>(predictedItems.Select(p => p.FilePath), StringComparer.OrdinalIgnoreCase);
        var actualPaths = new HashSet<string>(actualItems.Select(a => a.FilePath), StringComparer.OrdinalIgnoreCase);

        var matchedFiles = predictedPaths.Intersect(actualPaths, StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        var unexpectedFiles = actualPaths.Except(predictedPaths, StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        var missingPredictedFiles = predictedPaths.Except(actualPaths, StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();

        // Check verification execution
        var executedChecksSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var act in activities)
        {
            if (!string.IsNullOrWhiteSpace(act.MetadataJson) && act.MetadataJson.Contains("RepositoryCheckId", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(act.MetadataJson);
                    if (doc.RootElement.TryGetProperty("RepositoryCheckId", out var prop) && prop.GetString() is { } checkId && !string.IsNullOrWhiteSpace(checkId))
                    {
                        executedChecksSet.Add(checkId);
                    }
                }
                catch
                {
                    // Ignore JSON parse errors in activity metadata
                }
            }
            if (act.Stage == ExecutionStage.Build && (act.Status == ExecutionActivityStatus.Completed || act.Status == ExecutionActivityStatus.Started))
            {
                executedChecksSet.Add("Build");
            }
            if (act.Stage == ExecutionStage.Test && (act.Status == ExecutionActivityStatus.Completed || act.Status == ExecutionActivityStatus.Started))
            {
                executedChecksSet.Add("Test");
            }
        }

        var executedChecksList = executedChecksSet.OrderBy(c => c).ToList();

        var allExpectedChecksExecuted = expectedChecksList.Count == 0 ||
                                         expectedChecksList.All(exp => executedChecksSet.Any(exec => exec.Contains(exp, StringComparison.OrdinalIgnoreCase) || exp.Contains(exec, StringComparison.OrdinalIgnoreCase)));

        // Deterministic dimension observations based ONLY on real touched files
        var observations = new List<string>();

        var touchedApiFiles = actualItems
            .Where(a => a.FilePath.Contains("Controller", StringComparison.OrdinalIgnoreCase) || a.FilePath.Contains("/Controllers/", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.FilePath)
            .ToList();

        if (touchedApiFiles.Count > 0)
        {
            observations.Add($"API dimension confirmed: {touchedApiFiles.Count} controller file(s) modified in actual execution");
        }

        var touchedDataFiles = actualItems
            .Where(a => a.FilePath.Contains("DbContext", StringComparison.OrdinalIgnoreCase) ||
                        a.FilePath.Contains("/Entities/", StringComparison.OrdinalIgnoreCase) ||
                        a.FilePath.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.FilePath)
            .ToList();

        if (touchedDataFiles.Count > 0)
        {
            observations.Add($"DATA dimension confirmed: {touchedDataFiles.Count} schema/entity file(s) modified in actual execution");
        }

        var touchedTestFiles = actualItems
            .Where(a => a.FilePath.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) || a.FilePath.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.FilePath)
            .ToList();

        if (touchedTestFiles.Count > 0)
        {
            observations.Add($"TESTS dimension confirmed: {touchedTestFiles.Count} test file(s) touched in actual execution");
        }

        if (unexpectedFiles.Count > 0)
        {
            observations.Add($"{unexpectedFiles.Count} unexpected file(s) modified during execution");
        }

        if (missingPredictedFiles.Count > 0)
        {
            observations.Add($"{missingPredictedFiles.Count} predicted file(s) remained untouched");
        }

        if (observations.Count == 0 && actualItems.Count > 0)
        {
            observations.Add("All actual file modifications matched the predicted scope without unexpected side-effects");
        }

        return new PredictedVsActualComparisonDto(
            PredictedFiles: predictedItems,
            ActualFiles: actualItems,
            MatchedFiles: matchedFiles,
            UnexpectedFiles: unexpectedFiles,
            MissingPredictedFiles: missingPredictedFiles,
            ExpectedChecks: expectedChecksList,
            ExecutedChecks: executedChecksList,
            AllExpectedChecksExecuted: allExpectedChecksExecuted,
            DimensionObservations: observations);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
