using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

/// <summary>
/// The single rule for whether an execution can be cancelled, shared by the cancel command and the execution DTO the UI reads.
/// Cancelling stops the work that is running now (the original run or the active revision). Earlier commits, pushes and pull
/// requests stay as they are; only a delivery step that is executing at this moment blocks cancellation, because stopping
/// it half way would leave the remote in an unknown state.
/// </summary>
public static class ExecutionCancellationPolicy
{
    public static string? DescribeWhyCannotCancel(TaskExecution execution)
    {
        if (execution.Status is TaskExecutionStatus.Completed or TaskExecutionStatus.Failed or TaskExecutionStatus.Cancelled)
        {
            return $"Execution is already in a terminal state ({execution.Status}).";
        }

        if (execution.CommitStatus == ExecutionCommitStatus.InProgress ||
            execution.PushStatus == ExecutionPushStatus.InProgress ||
            execution.PullRequestStatus == ExecutionPullRequestStatus.InProgress ||
            execution.MergeStatus == ExecutionMergeStatus.InProgress)
        {
            return "A delivery step (commit/push/pull request/merge) is running right now and cannot be cancelled until it finishes.";
        }

        return null;
    }
}
