using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.TaskImpactAnalysis.Dtos;
using DevPilot.Application.TaskImpactAnalysis.Ports;
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
        IReadOnlyList<ExecutionActivity> activities,
        string? workspaceRoot = null,
        IDatabaseMigrationOperationParser? migrationParser = null,
        string? diff = null)
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

        // Database / Migration Intelligence V2: Predicted vs Actual DB comparison
        var dbComparison = EvaluateDatabaseImpact(
            impactAnalysis?.StructuredResult?.DatabaseImpact ?? impactAnalysis?.StructuredResult?.ChangeBrief?.DatabaseImpact,
            actualChangedFiles,
            workspaceRoot,
            migrationParser,
            diff);

        if (dbComparison != null && dbComparison.Observations.Count > 0)
        {
            foreach (var obs in dbComparison.Observations)
            {
                if (!observations.Contains(obs, StringComparer.OrdinalIgnoreCase))
                {
                    observations.Add(obs);
                }
            }
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
            DimensionObservations: observations,
            DatabaseImpact: dbComparison);
    }

    public static DatabasePredictedVsActualComparisonDto? EvaluateDatabaseImpact(
        DatabaseImpact? predictedDbImpact,
        IReadOnlyList<ExecutionReviewFileDto> actualChangedFiles,
        string? workspaceRoot = null,
        IDatabaseMigrationOperationParser? migrationParser = null,
        string? diff = null)
    {
        var predictedChanges = predictedDbImpact?.Changes ?? new List<DatabaseChange>();
        var predictedMigrationExpected = predictedDbImpact?.RequiresSchemaMigration == true ||
                                         predictedDbImpact?.MigrationRequirement == DatabaseMigrationRequirement.Expected;

        var actualMigrationFiles = actualChangedFiles
            .Where(f => f.Path.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) &&
                        !f.Path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) &&
                        !f.Path.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase) &&
                        (f.ChangeType.Equals("Add", StringComparison.OrdinalIgnoreCase) ||
                         f.ChangeType.Equals("Create", StringComparison.OrdinalIgnoreCase) ||
                         f.ChangeType.Equals("Added", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var actualSnapshotFiles = actualChangedFiles
            .Where(f => f.Path.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var actualMigrationCreated = actualMigrationFiles.Count > 0;

        // Parse actual database changes strictly from migration Up() operations
        var actualChanges = new List<DatabaseChange>();
        if (migrationParser != null)
        {
            foreach (var migFile in actualMigrationFiles)
            {
                string? content = null;
                if (!string.IsNullOrWhiteSpace(workspaceRoot))
                {
                    var fullPath = Path.Combine(workspaceRoot, migFile.Path);
                    if (File.Exists(fullPath))
                    {
                        try { content = File.ReadAllText(fullPath); } catch { /* Ignore read errors */ }
                    }
                }

                if (content == null && !string.IsNullOrWhiteSpace(diff))
                {
                    content = ExtractFileContentFromDiff(diff, migFile.Path);
                }

                if (!string.IsNullOrWhiteSpace(content))
                {
                    var parsed = migrationParser.ParseMigrationFile(migFile.Path, content);
                    actualChanges.AddRange(parsed);
                }
            }
        }

        // If no migration file was parsed into operations, but migration files or entity changes exist, infer basic operations
        if (actualChanges.Count == 0 && actualMigrationCreated)
        {
            foreach (var migFile in actualMigrationFiles)
            {
                actualChanges.Add(new DatabaseChange
                {
                    ObjectType = DatabaseObjectType.Unknown,
                    ObjectName = Path.GetFileNameWithoutExtension(migFile.Path),
                    Operation = DatabaseChangeOperation.Add,
                    Risk = RiskLevel.Low,
                    Evidence = $"New migration file '{Path.GetFileName(migFile.Path)}' created"
                });
            }
        }

        // If neither predicted nor actual has any database relevance, return null
        if (predictedDbImpact == null && !predictedMigrationExpected && predictedChanges.Count == 0 && !actualMigrationCreated && actualChanges.Count == 0 && actualSnapshotFiles.Count == 0)
        {
            return null;
        }

        // Perform structured matching
        var matchedChanges = new List<DatabaseChange>();
        var unmatchedActual = new List<DatabaseChange>(actualChanges);
        var unmatchedPredicted = new List<DatabaseChange>(predictedChanges);

        foreach (var pred in predictedChanges)
        {
            var match = unmatchedActual.FirstOrDefault(act => pred.Matches(act) || act.Matches(pred));
            if (match != null)
            {
                matchedChanges.Add(match);
                unmatchedActual.Remove(match);
                unmatchedPredicted.Remove(pred);
            }
        }

        var unexpectedChanges = unmatchedActual;
        var missingPredictedChanges = unmatchedPredicted;

        var destructiveWarnings = new List<string>();
        var dbObservations = new List<string>();

        // Check for destructive actual operations
        var hasDestructiveOperations = actualChanges.Any(c =>
            c.Risk >= RiskLevel.High ||
            c.Operation == DatabaseChangeOperation.Remove ||
            c.Evidence.Contains("DropColumn", StringComparison.OrdinalIgnoreCase) ||
            c.Evidence.Contains("DropTable", StringComparison.OrdinalIgnoreCase) ||
            c.Evidence.Contains("DropForeignKey", StringComparison.OrdinalIgnoreCase) ||
            c.Evidence.Contains("Custom SQL", StringComparison.OrdinalIgnoreCase));

        foreach (var unexp in unexpectedChanges.Where(c => c.Risk >= RiskLevel.High || c.Operation == DatabaseChangeOperation.Remove))
        {
            destructiveWarnings.Add($"Unexpected destructive actual database change: {unexp.Evidence}");
        }

        // Generate observations
        if (actualMigrationCreated)
        {
            dbObservations.Add($"Actual migration file created: {string.Join(", ", actualMigrationFiles.Select(f => Path.GetFileName(f.Path)))}");
        }

        if (actualSnapshotFiles.Count > 0)
        {
            dbObservations.Add($"EF Core ModelSnapshot updated as expected consequence of migration");
        }

        if (matchedChanges.Count > 0)
        {
            dbObservations.Add($"Database operations matched predicted schema changes ({matchedChanges.Count} matched)");
        }

        if (unexpectedChanges.Count > 0)
        {
            dbObservations.Add($"{unexpectedChanges.Count} unexpected database operation(s) executed in migration");
        }

        if (missingPredictedChanges.Count > 0)
        {
            dbObservations.Add($"{missingPredictedChanges.Count} predicted database change(s) not found in actual migration");
        }

        // Status derivation
        string status;
        if (destructiveWarnings.Count > 0)
        {
            status = "Unexpected";
        }
        else if (unexpectedChanges.Count > 0)
        {
            status = "Unexpected";
        }
        else if (missingPredictedChanges.Count == 0 && (predictedMigrationExpected == actualMigrationCreated || (predictedMigrationExpected && actualChanges.Count > 0)))
        {
            status = "Matched";
        }
        else if (matchedChanges.Count > 0 || (predictedMigrationExpected && actualMigrationCreated))
        {
            status = "Partial";
        }
        else if (predictedChanges.Count > 0 && actualChanges.Count == 0)
        {
            status = "Partial";
        }
        else
        {
            status = "Unknown";
        }

        return new DatabasePredictedVsActualComparisonDto(
            Status: status,
            PredictedMigrationExpected: predictedMigrationExpected,
            ActualMigrationCreated: actualMigrationCreated,
            PredictedChanges: predictedChanges.Select(MapChangeToDto).ToList(),
            ActualChanges: actualChanges.Select(MapChangeToDto).ToList(),
            MatchedChanges: matchedChanges.Select(MapChangeToDto).ToList(),
            UnexpectedChanges: unexpectedChanges.Select(MapChangeToDto).ToList(),
            MissingPredictedChanges: missingPredictedChanges.Select(MapChangeToDto).ToList(),
            Observations: dbObservations,
            HasDestructiveOperations: hasDestructiveOperations,
            DestructiveWarnings: destructiveWarnings);
    }

    private static DatabaseChangeDto MapChangeToDto(DatabaseChange change)
    {
        return new DatabaseChangeDto
        {
            ObjectType = change.ObjectType,
            ObjectName = change.ObjectName,
            ParentObjectName = change.ParentObjectName,
            Operation = change.Operation,
            Before = change.Before,
            After = change.After,
            Risk = change.Risk,
            Evidence = change.Evidence
        };
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string? ExtractFileContentFromDiff(string diff, string filePath)
    {
        if (string.IsNullOrWhiteSpace(diff)) return null;

        if (!diff.Contains("diff --git") && (diff.Contains("class ") || diff.Contains("MigrationBuilder") || diff.Contains("namespace ") || diff.Contains("migrationBuilder.")))
        {
            return diff;
        }

        var normPath = NormalizePath(filePath);
        var lines = diff.Split('\n');
        var inTargetFile = false;
        var sb = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var p = NormalizePath(line.Replace("+++ b/", "").Replace("+++ ", "").Trim());
                inTargetFile = string.Equals(p, normPath, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line.StartsWith("diff --git", StringComparison.Ordinal))
            {
                inTargetFile = false;
                continue;
            }

            if (inTargetFile)
            {
                if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    sb.AppendLine(line.Substring(1));
                }
                else if (!line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("@@", StringComparison.Ordinal))
                {
                    sb.AppendLine(line);
                }
            }
        }

        var res = sb.ToString();
        return string.IsNullOrWhiteSpace(res) ? null : res;
    }
}
