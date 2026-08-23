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

        var parsedActivities = activities
            .Select((a, idx) => (Activity: a, Metadata: ParseMetadata(a.MetadataJson), Index: idx))
            .ToList();

        var workspaceFailed = parsedActivities.Any(p =>
            p.Activity.Stage == ExecutionStage.Workspace &&
            p.Activity.Status == ExecutionActivityStatus.Failed);

        if (workspaceFailed)
        {
            return ExecutionVerificationOutcome.Failed;
        }

        var terminalDeveloperAgentStatus = GetTerminalDeveloperAgentStatus(parsedActivities);
        if (terminalDeveloperAgentStatus == ExecutionActivityStatus.Failed)
        {
            return ExecutionVerificationOutcome.Failed;
        }

        if (execution.Status == TaskExecutionStatus.Failed &&
            terminalDeveloperAgentStatus != ExecutionActivityStatus.Completed)
        {
            return ExecutionVerificationOutcome.Failed;
        }

        // 1. VerificationInfrastructureError: Infrastructure failure during check discovery or execution
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

        // 2. Identify check activities and derive the latest (terminal) result per repository check identity.
        // Intermediate failure activities that were subsequently superseded by successful repair must not force NeedsReview.
        var checkActivities = parsedActivities
            .Where(p => p.Activity.Stage == ExecutionStage.Build || p.Activity.Stage == ExecutionStage.Test)
            .ToList();

        var checkGroups = checkActivities
            .GroupBy(p => !string.IsNullOrWhiteSpace(p.Metadata?.RepositoryCheckId)
                ? p.Metadata.RepositoryCheckId
                : $"activity_{p.Index}")
            .ToList();

        var terminalCheckResults = new List<(string CheckKey, ExecutionActivity Activity, ExecutionActivityMetadata? Metadata, bool Passed, bool IsPreExisting, bool IsUnresolvedFailure)>();

        foreach (var group in checkGroups)
        {
            var terminalActivity = group
                .Where(p => p.Activity.Status == ExecutionActivityStatus.Completed ||
                            p.Activity.Status == ExecutionActivityStatus.Failed ||
                            !string.IsNullOrWhiteSpace(p.Metadata?.VerificationOutcome))
                .OrderBy(p => p.Index)
                .LastOrDefault();

            if (terminalActivity.Activity != null)
            {
                var isCompleted = terminalActivity.Activity.Status == ExecutionActivityStatus.Completed;
                var outcomeStr = terminalActivity.Metadata?.VerificationOutcome;
                var baseClassification = terminalActivity.Metadata?.BaselineClassification;

                var isPreExisting = string.Equals(outcomeStr, nameof(ExecutionVerificationOutcome.NoNewRegressions), StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(baseClassification, "PreExisting", StringComparison.OrdinalIgnoreCase) ||
                                   (isCompleted && terminalActivity.Activity.Stage == ExecutionStage.Test && terminalActivity.Activity.Message.StartsWith("No new regressions", StringComparison.OrdinalIgnoreCase));

                var isNeedsReviewExplicit = string.Equals(outcomeStr, nameof(ExecutionVerificationOutcome.NeedsReview), StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(baseClassification, "Unknown", StringComparison.OrdinalIgnoreCase);

                var isUnresolvedFailure = isNeedsReviewExplicit ||
                                         (!isCompleted && !isPreExisting);

                terminalCheckResults.Add((
                    group.Key,
                    terminalActivity.Activity,
                    terminalActivity.Metadata,
                    Passed: isCompleted || isPreExisting,
                    IsPreExisting: isPreExisting,
                    IsUnresolvedFailure: isUnresolvedFailure));
            }
        }

        // 3. NeedsReview: Any check ended with an unresolved failure (same failure, unhandled failure, new regression, unknown baseline)
        if (terminalCheckResults.Any(t => t.IsUnresolvedFailure))
        {
            return ExecutionVerificationOutcome.NeedsReview;
        }

        var hasExplicitGlobalNeedsReview = parsedActivities.Any(p =>
            p.Activity.Stage != ExecutionStage.Build && p.Activity.Stage != ExecutionStage.Test &&
            string.Equals(p.Metadata?.VerificationOutcome, nameof(ExecutionVerificationOutcome.NeedsReview), StringComparison.OrdinalIgnoreCase));

        if (hasExplicitGlobalNeedsReview)
        {
            return ExecutionVerificationOutcome.NeedsReview;
        }

        // 4. NoNewRegressions: Pre-existing failure proven by baseline comparison on build or test
        if (terminalCheckResults.Any(t => t.IsPreExisting))
        {
            return ExecutionVerificationOutcome.NoNewRegressions;
        }

        // 5. VerificationUnavailable: No checks discovered or unconfigured
        var hasUnavailable = parsedActivities.Any(p =>
            string.Equals(p.Metadata?.VerificationOutcome, nameof(ExecutionVerificationOutcome.VerificationUnavailable), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Metadata?.VerificationFailureCategory, "Unconfigured", StringComparison.OrdinalIgnoreCase));

        var buildCount = terminalCheckResults.Count(t => t.Activity.Stage == ExecutionStage.Build);
        var testCount = terminalCheckResults.Count(t => t.Activity.Stage == ExecutionStage.Test);

        if (hasUnavailable || (buildCount == 0 && testCount == 0))
        {
            return ExecutionVerificationOutcome.VerificationUnavailable;
        }

        // 6. Check if preflight reported unresolved verification (e.g. partial discovery / unresolved scripts)
        var hasUnresolvedVerification = parsedActivities.Any(p => p.Metadata?.VerificationUnresolved == true);

        // 7. Clean build passed and no tests discovered
        if (buildCount > 0 && testCount == 0)
        {
            return ExecutionVerificationOutcome.PartiallyVerified;
        }

        if (hasUnresolvedVerification)
        {
            return ExecutionVerificationOutcome.PartiallyVerified;
        }

        // 8. Verified: Build+Test or Test-only passed with full verification
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

    internal static ExecutionActivityStatus? GetTerminalDeveloperAgentStatus(
        IReadOnlyList<(ExecutionActivity Activity, ExecutionActivityMetadata? Metadata, int Index)> parsedActivities)
    {
        var terminal = parsedActivities
            .Where(p => p.Activity.Stage == ExecutionStage.DeveloperAgent)
            .Where(p => p.Activity.Status is ExecutionActivityStatus.Completed or ExecutionActivityStatus.Failed)
            .Where(p => !IsDeveloperAgentAttemptTelemetry(p.Metadata))
            .OrderBy(p => p.Activity.CreatedAt)
            .ThenBy(p => p.Index)
            .LastOrDefault();

        return terminal.Activity?.Status;
    }

    internal static bool IsDeveloperAgentAttemptTelemetry(ExecutionActivityMetadata? metadata)
    {
        if (metadata == null)
        {
            return false;
        }

        if (string.Equals(metadata.EventKind, "ProviderCall", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(metadata.ProviderCallKind);
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
