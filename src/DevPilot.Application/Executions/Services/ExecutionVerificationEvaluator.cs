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

        var parsedActivities = activities
            .Select(a => (Activity: a, Metadata: ParseMetadata(a.MetadataJson)))
            .ToList();

        // 1. NeedsReview: Any unresolved real regression, inconclusive baseline, or unhandled check failure
        var hasNeedsReview = parsedActivities.Any(p =>
            string.Equals(p.Metadata?.VerificationOutcome, nameof(ExecutionVerificationOutcome.NeedsReview), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Metadata?.BaselineClassification, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            (p.Activity.Status == ExecutionActivityStatus.Failed &&
             (p.Activity.Stage == ExecutionStage.Build || p.Activity.Stage == ExecutionStage.Test) &&
             !string.Equals(p.Metadata?.VerificationOutcome, nameof(ExecutionVerificationOutcome.VerificationInfrastructureError), StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(p.Metadata?.VerificationOutcome, nameof(ExecutionVerificationOutcome.NoNewRegressions), StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(p.Metadata?.BaselineClassification, "PreExisting", StringComparison.OrdinalIgnoreCase)));

        if (hasNeedsReview)
        {
            return ExecutionVerificationOutcome.NeedsReview;
        }

        // 2. VerificationInfrastructureError: Any infrastructure failure during check discovery or execution
        var hasInfraError = parsedActivities.Any(p =>
            string.Equals(p.Metadata?.VerificationOutcome, nameof(ExecutionVerificationOutcome.VerificationInfrastructureError), StringComparison.OrdinalIgnoreCase) ||
            (p.Metadata?.VerificationFailureCategory != null && p.Metadata.VerificationFailureCategory.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)) ||
            (p.Activity.MetadataJson != null &&
             (p.Activity.MetadataJson.Contains("InfrastructureFailure", StringComparison.OrdinalIgnoreCase) ||
              p.Activity.MetadataJson.Contains("VerificationInfrastructureError", StringComparison.OrdinalIgnoreCase))));

        if (hasInfraError)
        {
            return ExecutionVerificationOutcome.VerificationInfrastructureError;
        }

        // 3. NoNewRegressions: Pre-existing failure proven by baseline comparison on build or test
        var hasPreExisting = parsedActivities.Any(p =>
            string.Equals(p.Metadata?.VerificationOutcome, nameof(ExecutionVerificationOutcome.NoNewRegressions), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Metadata?.BaselineClassification, "PreExisting", StringComparison.OrdinalIgnoreCase) ||
            (p.Activity.Stage == ExecutionStage.Test && p.Activity.Status == ExecutionActivityStatus.Completed && p.Activity.Message.StartsWith("No new regressions", StringComparison.OrdinalIgnoreCase)));

        if (hasPreExisting)
        {
            return ExecutionVerificationOutcome.NoNewRegressions;
        }

        // 4. VerificationUnavailable: No checks discovered or unconfigured
        var hasUnavailable = parsedActivities.Any(p =>
            string.Equals(p.Metadata?.VerificationOutcome, nameof(ExecutionVerificationOutcome.VerificationUnavailable), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Metadata?.VerificationFailureCategory, "Unconfigured", StringComparison.OrdinalIgnoreCase));

        var buildActivities = activities.Where(a => a.Stage == ExecutionStage.Build).ToList();
        var testActivities = activities.Where(a => a.Stage == ExecutionStage.Test).ToList();

        if (hasUnavailable || (buildActivities.Count == 0 && testActivities.Count == 0))
        {
            return ExecutionVerificationOutcome.VerificationUnavailable;
        }

        // 5. Check if preflight reported unresolved verification (e.g. partial discovery / unresolved scripts)
        var hasUnresolvedVerification = parsedActivities.Any(p => p.Metadata?.VerificationUnresolved == true);

        // 6. Clean build passed and no tests discovered
        var buildPassed = buildActivities.Any(a => a.Status == ExecutionActivityStatus.Completed);
        var testPassed = testActivities.Any(a => a.Status == ExecutionActivityStatus.Completed);

        if (buildPassed && testActivities.Count == 0)
        {
            return ExecutionVerificationOutcome.PartiallyVerified;
        }

        if (hasUnresolvedVerification)
        {
            return ExecutionVerificationOutcome.PartiallyVerified;
        }

        // 7. Verified: Build+Test or Test-only passed with full verification
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
