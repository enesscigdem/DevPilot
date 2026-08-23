using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

public enum LocalTestEvidence
{
    Passed,
    NoNewRegressions,
    Failed,
    SuiteNotDiscovered,
    NotExecuted,
    Inconclusive
}

public static class ExecutionMergeEligibility
{
    public static (bool CanMerge, string? BlockedReason) EvaluateFromActivities(
        TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities,
        bool allowNoChecks,
        bool allowInProgress = false)
    {
        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
        if (outcome is ExecutionVerificationOutcome.NeedsReview
            or ExecutionVerificationOutcome.Failed
            or ExecutionVerificationOutcome.Blocked)
        {
            return (false, $"Execution cannot be merged because verification outcome is '{outcome}'.");
        }

        var stages = ExecutionReviewStageClassifier.Classify(execution, activities);
        var buildPassed = IsAcceptableBuildStatus(stages.Build.Status);
        var testEvidence = ClassifyTestEvidence(outcome, stages.Test.Status);
        return EvaluateMergeEligibility(execution, allowNoChecks, buildPassed, testEvidence, allowInProgress);
    }

    public static (bool CanMerge, string? BlockedReason) EvaluateMergeEligibility(
        TaskExecution execution,
        bool allowNoChecks,
        bool buildPassed = true,
        bool testPassed = true,
        bool allowInProgress = false)
    {
        return EvaluateMergeEligibility(
            execution,
            allowNoChecks,
            buildPassed,
            testPassed ? LocalTestEvidence.Passed : LocalTestEvidence.Failed,
            allowInProgress);
    }

    public static (bool CanMerge, string? BlockedReason) EvaluateMergeEligibility(
        TaskExecution execution,
        bool allowNoChecks,
        bool buildPassed,
        LocalTestEvidence testEvidence,
        bool allowInProgress = false)
    {
        if (execution is null)
            return (false, "Execution not found.");

        if (execution.Status != TaskExecutionStatus.Completed)
            return (false, $"Execution is {execution.Status}, expected Completed.");

        if (execution.ReviewStatus != ExecutionReviewStatus.Approved)
            return (false, $"Review is {execution.ReviewStatus}, expected Approved.");

        if (execution.CommitStatus != ExecutionCommitStatus.Committed || string.IsNullOrWhiteSpace(execution.CommitSha))
            return (false, "Changes are not committed.");

        if (execution.PushStatus != ExecutionPushStatus.Pushed || string.IsNullOrWhiteSpace(execution.RemoteCommitSha))
            return (false, "Execution branch is not pushed remotely.");

        if (execution.PullRequestStatus != ExecutionPullRequestStatus.Open)
            return (false, "Pull request is not open.");

        if (!execution.PullRequestNumber.HasValue || execution.PullRequestNumber.Value <= 0)
            return (false, "Pull request number is missing.");

        if (string.IsNullOrWhiteSpace(execution.PullRequestUrl) || string.IsNullOrWhiteSpace(execution.PullRequestBaseBranch))
            return (false, "Pull request URL or base branch is not configured.");

        if (string.IsNullOrWhiteSpace(execution.BranchName) || string.IsNullOrWhiteSpace(execution.RemoteBranchName))
            return (false, "Branch name or remote branch name is not configured.");

        if (execution.BranchName != execution.RemoteBranchName)
            return (false, $"Local branch '{execution.BranchName}' does not match remote branch '{execution.RemoteBranchName}'.");

        if (execution.CommitSha != execution.RemoteCommitSha)
            return (false, $"Committed SHA '{execution.CommitSha}' does not match pushed SHA '{execution.RemoteCommitSha}'.");

        if (execution.MergeStatus != ExecutionMergeStatus.None && execution.MergeStatus != ExecutionMergeStatus.Failed && !(allowInProgress && execution.MergeStatus == ExecutionMergeStatus.InProgress))
            return (false, $"Merge status is {execution.MergeStatus}.");

        if (execution.PullRequestRemoteState != ExecutionPullRequestRemoteState.Open &&
            execution.PullRequestRemoteState != ExecutionPullRequestRemoteState.Unknown)
            return (false, $"Pull request remote state is {execution.PullRequestRemoteState}, expected Open.");

        if (execution.PullRequestIntegrityStatus != ExecutionPullRequestIntegrityStatus.Valid &&
            execution.PullRequestIntegrityStatus != ExecutionPullRequestIntegrityStatus.Unknown)
            return (false, $"Pull request integrity is {execution.PullRequestIntegrityStatus}, expected Valid.");

        var (ciEligible, ciReason) = EvaluateCiEligibilityDetailed(execution.CiStatus, allowNoChecks, buildPassed, testEvidence);
        if (!ciEligible)
            return (false, ciReason);

        return (true, null);
    }

    public static bool CalculateCanRequestMerge(
        TaskExecution execution,
        bool allowNoChecks,
        bool buildPassed = true,
        bool testPassed = true,
        bool allowInProgress = false)
    {
        return EvaluateMergeEligibility(execution, allowNoChecks, buildPassed, testPassed, allowInProgress).CanMerge;
    }

    public static LocalTestEvidence ClassifyTestEvidence(
        ExecutionVerificationOutcome outcome,
        string? testStatus)
    {
        if (string.Equals(testStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return LocalTestEvidence.Failed;
        }

        if (string.Equals(testStatus, "Passed", StringComparison.OrdinalIgnoreCase))
        {
            return LocalTestEvidence.Passed;
        }

        if (string.Equals(testStatus, "NoNewRegressions", StringComparison.OrdinalIgnoreCase))
        {
            return LocalTestEvidence.NoNewRegressions;
        }

        if (outcome == ExecutionVerificationOutcome.PartiallyVerified)
        {
            return LocalTestEvidence.SuiteNotDiscovered;
        }

        if (outcome is ExecutionVerificationOutcome.VerificationInfrastructureError
            or ExecutionVerificationOutcome.VerificationUnavailable)
        {
            return LocalTestEvidence.Inconclusive;
        }

        return LocalTestEvidence.NotExecuted;
    }

    public static bool IsAcceptableBuildStatus(string? buildStatus) =>
        string.Equals(buildStatus, "Passed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(buildStatus, "NoNewRegressions", StringComparison.OrdinalIgnoreCase);

    public static (bool IsEligible, string? Reason) EvaluateCiEligibilityDetailed(
        ExecutionCiStatus ciStatus,
        bool allowNoChecks,
        bool buildPassed,
        bool testPassed)
    {
        return EvaluateCiEligibilityDetailed(
            ciStatus,
            allowNoChecks,
            buildPassed,
            testPassed ? LocalTestEvidence.Passed : LocalTestEvidence.Failed);
    }

    public static (bool IsEligible, string? Reason) EvaluateCiEligibilityDetailed(
        ExecutionCiStatus ciStatus,
        bool allowNoChecks,
        bool buildPassed,
        LocalTestEvidence testEvidence)
    {
        return ciStatus switch
        {
            ExecutionCiStatus.Success => (true, null),
            ExecutionCiStatus.NoChecks => !allowNoChecks
                ? (false, "CI checks were not found (CI checks are required by merge policy).")
                : !buildPassed
                    ? (false, "Local build did not pass.")
                    : EvaluateLocalTestEvidence(testEvidence),
            ExecutionCiStatus.Pending => (false, "CI checks are still pending."),
            ExecutionCiStatus.Failure => (false, "CI checks failed."),
            ExecutionCiStatus.Neutral => (false, "CI checks returned neutral status."),
            _ => (false, "CI status is unknown. Please refresh PR status.")
        };
    }

    public static bool EvaluateCiEligibility(
        ExecutionCiStatus ciStatus,
        bool allowNoChecks,
        bool buildPassed,
        bool testPassed)
    {
        return EvaluateCiEligibilityDetailed(ciStatus, allowNoChecks, buildPassed, testPassed).IsEligible;
    }

    private static (bool IsEligible, string? Reason) EvaluateLocalTestEvidence(LocalTestEvidence testEvidence)
    {
        return testEvidence switch
        {
            LocalTestEvidence.Passed => (true, null),
            LocalTestEvidence.NoNewRegressions => (true, null),
            LocalTestEvidence.SuiteNotDiscovered => (true, null),
            LocalTestEvidence.Failed => (false, "Local tests failed."),
            LocalTestEvidence.NotExecuted => (false, "Local tests did not run."),
            LocalTestEvidence.Inconclusive => (false, "Local test results are inconclusive."),
            _ => (false, "Local test results are inconclusive.")
        };
    }
}
