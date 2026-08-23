using System.Text.Json;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

public static class ExecutionVerificationEvaluator
{
    public static ExecutionVerificationOutcome DetermineOutcome(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities)
    {
        if (execution == null)
        {
            return ExecutionVerificationOutcome.Failed;
        }

        if (execution.Status == TaskExecutionStatus.Cancelled)
        {
            return ExecutionVerificationOutcome.Blocked;
        }

        var workspaceFailed = activities.Any(a => a.Stage == ExecutionStage.Workspace && a.Status == ExecutionActivityStatus.Failed);
        var devAgentFailed = activities.Any(a => a.Stage == ExecutionStage.DeveloperAgent && a.Status == ExecutionActivityStatus.Failed);

        if (workspaceFailed || (execution.Status == TaskExecutionStatus.Failed && !activities.Any(a => a.Stage == ExecutionStage.DeveloperAgent && a.Status == ExecutionActivityStatus.Completed)))
        {
            return ExecutionVerificationOutcome.Failed;
        }

        if (devAgentFailed)
        {
            return ExecutionVerificationOutcome.Failed;
        }

        // 1. Check if an activity explicitly sets VerificationOutcome
        var explicitMeta = activities
            .Where(a => !string.IsNullOrWhiteSpace(a.MetadataJson))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => ParseMetadata(a.MetadataJson))
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m?.VerificationOutcome));

        if (explicitMeta != null && Enum.TryParse<ExecutionVerificationOutcome>(explicitMeta.VerificationOutcome, ignoreCase: true, out var parsedOutcome))
        {
            return parsedOutcome;
        }

        // 2. Evaluate from activities
        var buildActivities = activities.Where(a => a.Stage == ExecutionStage.Build).ToList();
        var testActivities = activities.Where(a => a.Stage == ExecutionStage.Test).ToList();

        var buildPassed = buildActivities.Any(a => a.Status == ExecutionActivityStatus.Completed);
        var buildFailed = buildActivities.Any(a => a.Status == ExecutionActivityStatus.Failed);
        var testPassed = testActivities.Any(a => a.Status == ExecutionActivityStatus.Completed);
        var testFailed = testActivities.Any(a => a.Status == ExecutionActivityStatus.Failed);

        var hasInfraError = activities.Any(a =>
            a.MetadataJson != null &&
            (a.MetadataJson.Contains("InfrastructureFailure", StringComparison.OrdinalIgnoreCase) ||
             a.MetadataJson.Contains("VerificationInfrastructureError", StringComparison.OrdinalIgnoreCase)));

        if (hasInfraError)
        {
            return ExecutionVerificationOutcome.VerificationInfrastructureError;
        }

        var isNoNewRegressions = testActivities.Any(a =>
            a.Status == ExecutionActivityStatus.Completed &&
            (a.Message.StartsWith("No new regressions", StringComparison.OrdinalIgnoreCase) ||
             (a.MetadataJson != null && (a.MetadataJson.Contains("NoNewRegressions", StringComparison.OrdinalIgnoreCase) || a.MetadataJson.Contains("PreExisting", StringComparison.OrdinalIgnoreCase)))));

        if (isNoNewRegressions)
        {
            return ExecutionVerificationOutcome.NoNewRegressions;
        }

        if (buildFailed || testFailed)
        {
            return ExecutionVerificationOutcome.NeedsReview;
        }

        if (buildPassed && testPassed)
        {
            return ExecutionVerificationOutcome.Verified;
        }

        if (testPassed && buildActivities.Count == 0)
        {
            return ExecutionVerificationOutcome.Verified;
        }

        if (buildPassed && testActivities.Count == 0)
        {
            return ExecutionVerificationOutcome.PartiallyVerified;
        }

        if (buildActivities.Count == 0 && testActivities.Count == 0)
        {
            return ExecutionVerificationOutcome.VerificationUnavailable;
        }

        return ExecutionVerificationOutcome.Verified;
    }

    public static bool IsDeliveryEligible(ExecutionVerificationOutcome outcome)
    {
        return outcome switch
        {
            ExecutionVerificationOutcome.Verified => true,
            ExecutionVerificationOutcome.NoNewRegressions => true,
            ExecutionVerificationOutcome.PartiallyVerified => true,
            ExecutionVerificationOutcome.VerificationUnavailable => true,
            ExecutionVerificationOutcome.VerificationInfrastructureError => true,
            ExecutionVerificationOutcome.NeedsReview => false,
            ExecutionVerificationOutcome.Failed => false,
            ExecutionVerificationOutcome.Blocked => false,
            _ => false
        };
    }

    private static ExecutionActivityMetadata? ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<ExecutionActivityMetadata>(metadataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
