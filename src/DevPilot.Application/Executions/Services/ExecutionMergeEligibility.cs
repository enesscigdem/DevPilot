using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

public static class ExecutionMergeEligibility
{
    public static bool CalculateCanRequestMerge(
        TaskExecution execution,
        bool allowNoChecks,
        bool buildPassed = true,
        bool testPassed = true,
        bool allowInProgress = false)
    {
        if (execution is null) return false;

        if (execution.Status != TaskExecutionStatus.Completed) return false;
        if (execution.ReviewStatus != ExecutionReviewStatus.Approved) return false;

        if (execution.CommitStatus != ExecutionCommitStatus.Committed || string.IsNullOrWhiteSpace(execution.CommitSha)) return false;
        if (execution.PushStatus != ExecutionPushStatus.Pushed || string.IsNullOrWhiteSpace(execution.RemoteCommitSha)) return false;

        if (execution.PullRequestStatus != ExecutionPullRequestStatus.Open) return false;
        if (!execution.PullRequestNumber.HasValue || execution.PullRequestNumber.Value <= 0) return false;
        if (string.IsNullOrWhiteSpace(execution.PullRequestUrl) || string.IsNullOrWhiteSpace(execution.PullRequestBaseBranch)) return false;

        if (string.IsNullOrWhiteSpace(execution.BranchName) || string.IsNullOrWhiteSpace(execution.RemoteBranchName)) return false;
        if (execution.BranchName != execution.RemoteBranchName) return false;
        if (execution.CommitSha != execution.RemoteCommitSha) return false;

        if (execution.MergeStatus != ExecutionMergeStatus.None && execution.MergeStatus != ExecutionMergeStatus.Failed && !(allowInProgress && execution.MergeStatus == ExecutionMergeStatus.InProgress)) return false;

        if (execution.PullRequestRemoteState != ExecutionPullRequestRemoteState.Open &&
            execution.PullRequestRemoteState != ExecutionPullRequestRemoteState.Unknown) return false;

        if (execution.PullRequestIntegrityStatus != ExecutionPullRequestIntegrityStatus.Valid &&
            execution.PullRequestIntegrityStatus != ExecutionPullRequestIntegrityStatus.Unknown) return false;

        return EvaluateCiEligibility(execution.CiStatus, allowNoChecks, buildPassed, testPassed);
    }

    public static bool EvaluateCiEligibility(
        ExecutionCiStatus ciStatus,
        bool allowNoChecks,
        bool buildPassed,
        bool testPassed)
    {
        return ciStatus switch
        {
            ExecutionCiStatus.Success => true,
            ExecutionCiStatus.NoChecks => allowNoChecks && buildPassed && testPassed,
            _ => false
        };
    }
}
