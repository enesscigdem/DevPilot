using System.Text.Json;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

public static class ExecutionReviewStageClassifier
{
    public static (ExecutionReviewStageStatusDto Build, ExecutionReviewStageStatusDto Test) Classify(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities)
    {
        var parsedActivities = activities
            .Select((a, idx) => (Activity: a, Metadata: ParseMetadata(a.MetadataJson), Index: idx))
            .ToList();

        var buildStatus = ClassifyBuildStatus(parsedActivities);
        var (testStatus, preExistingCount, newRegressionCount, testDetailSummary) = ClassifyTestStatus(parsedActivities);

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        if (outcome == ExecutionVerificationOutcome.PartiallyVerified &&
            string.Equals(testStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            testDetailSummary ??= "No local test suite was discovered.";
        }
        else if (string.Equals(testStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            testDetailSummary ??= "Tests failed";
        }

        return (
            new ExecutionReviewStageStatusDto(buildStatus),
            new ExecutionReviewStageStatusDto(testStatus, preExistingCount, newRegressionCount, testDetailSummary));
    }

    private static string ClassifyBuildStatus(
        IReadOnlyList<(ExecutionActivity Activity, ExecutionActivityMetadata? Metadata, int Index)> parsedActivities)
    {
        var buildActivities = parsedActivities
            .Where(p => p.Activity.Stage == ExecutionStage.Build)
            .ToList();

        var buildGroups = buildActivities
            .GroupBy(p => !string.IsNullOrWhiteSpace(p.Metadata?.RepositoryCheckId)
                ? p.Metadata.RepositoryCheckId
                : $"activity_{p.Index}")
            .ToList();

        if (buildGroups.Count == 0)
        {
            return "Unknown";
        }

        var terminalBuilds = buildGroups
            .Select(g => g
                .Where(p => p.Activity.Status == ExecutionActivityStatus.Completed ||
                            p.Activity.Status == ExecutionActivityStatus.Failed ||
                            !string.IsNullOrWhiteSpace(p.Metadata?.VerificationOutcome))
                .OrderBy(p => p.Index)
                .LastOrDefault())
            .Where(t => t.Activity != null)
            .ToList();

        if (terminalBuilds.Count == 0)
        {
            return buildActivities.Any(p => p.Activity.Status == ExecutionActivityStatus.Started)
                ? "Running"
                : "Unknown";
        }

        if (terminalBuilds.Any(t =>
                string.Equals(t.Metadata?.VerificationOutcome, "NeedsReview", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Metadata?.BaselineClassification, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                (t.Activity.Status == ExecutionActivityStatus.Failed &&
                 !string.Equals(t.Metadata?.VerificationOutcome, "NoNewRegressions", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(t.Metadata?.BaselineClassification, "PreExisting", StringComparison.OrdinalIgnoreCase))))
        {
            return "Failed";
        }

        if (terminalBuilds.Any(t =>
                string.Equals(t.Metadata?.VerificationOutcome, "NoNewRegressions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Metadata?.BaselineClassification, "PreExisting", StringComparison.OrdinalIgnoreCase)))
        {
            return "NoNewRegressions";
        }

        return terminalBuilds.All(t => t.Activity.Status == ExecutionActivityStatus.Completed)
            ? "Passed"
            : "Unknown";
    }

    private static (string Status, int PreExistingCount, int NewRegressionCount, string? DetailSummary) ClassifyTestStatus(
        IReadOnlyList<(ExecutionActivity Activity, ExecutionActivityMetadata? Metadata, int Index)> parsedActivities)
    {
        var testActivities = parsedActivities
            .Where(p => p.Activity.Stage == ExecutionStage.Test)
            .ToList();

        var testGroups = testActivities
            .GroupBy(p => !string.IsNullOrWhiteSpace(p.Metadata?.RepositoryCheckId)
                ? p.Metadata.RepositoryCheckId
                : $"activity_{p.Index}")
            .ToList();

        if (testGroups.Count == 0)
        {
            return ("Unknown", 0, 0, null);
        }

        var terminalTests = testGroups
            .Select(g => g
                .Where(p => p.Activity.Status == ExecutionActivityStatus.Completed ||
                            p.Activity.Status == ExecutionActivityStatus.Failed ||
                            !string.IsNullOrWhiteSpace(p.Metadata?.VerificationOutcome))
                .OrderBy(p => p.Index)
                .LastOrDefault())
            .Where(t => t.Activity != null)
            .ToList();

        if (terminalTests.Count == 0)
        {
            return testActivities.Any(p => p.Activity.Status == ExecutionActivityStatus.Started)
                ? ("Running", 0, 0, null)
                : ("Unknown", 0, 0, null);
        }

        var preExistingCount = terminalTests
            .Select(t => t.Metadata?.PreExistingFailureCount)
            .FirstOrDefault(c => c.HasValue) ?? 0;
        var newRegressionCount = terminalTests
            .Select(t => t.Metadata?.NewRegressionCount)
            .FirstOrDefault(c => c.HasValue) ?? 0;

        var isFailed = terminalTests.Any(t =>
            string.Equals(t.Metadata?.VerificationOutcome, "NeedsReview", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Metadata?.BaselineClassification, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            (t.Activity.Status == ExecutionActivityStatus.Failed &&
             !string.Equals(t.Metadata?.VerificationOutcome, "NoNewRegressions", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(t.Metadata?.BaselineClassification, "PreExisting", StringComparison.OrdinalIgnoreCase)));

        var isNoNewRegressions = terminalTests.Any(t =>
            string.Equals(t.Metadata?.VerificationOutcome, "NoNewRegressions", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Metadata?.BaselineClassification, "PreExisting", StringComparison.OrdinalIgnoreCase) ||
            (t.Activity.Status == ExecutionActivityStatus.Completed &&
             t.Activity.Message.StartsWith("No new regressions", StringComparison.OrdinalIgnoreCase)));

        if (isFailed)
        {
            var detail = newRegressionCount > 0
                ? $"{newRegressionCount} new regression(s) introduced"
                : "Tests failed";
            return ("Failed", preExistingCount, newRegressionCount, detail);
        }

        if (isNoNewRegressions)
        {
            var detail = preExistingCount > 0
                ? $"{preExistingCount} pre-existing repository failure(s) remain"
                : "No new regressions";
            return ("NoNewRegressions", preExistingCount, newRegressionCount, detail);
        }

        if (terminalTests.All(t => t.Activity.Status == ExecutionActivityStatus.Completed))
        {
            return ("Passed", preExistingCount, newRegressionCount, "All tests passed");
        }

        return ("Unknown", preExistingCount, newRegressionCount, null);
    }

    private static ExecutionActivityMetadata? ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ExecutionActivityMetadata>(
                metadataJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
