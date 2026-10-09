using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Goals;

/// <summary>Where a goal task stands, in the words the person thinks in.</summary>
public enum GoalPhase
{
    /// <summary>Not started: waiting for earlier tasks that touch the same code, or for its turn.</summary>
    Waiting,

    /// <summary>DevPilot is reading the code and drafting the change plan.</summary>
    Analyzing,

    /// <summary>The plan is ready and waits for approval.</summary>
    PlanReady,

    /// <summary>Approved and waiting for a free slot.</summary>
    Queued,

    /// <summary>The code is being written and verified.</summary>
    Running,

    /// <summary>Finished and waiting to be reviewed.</summary>
    InReview,

    /// <summary>Approved; being committed and pushed.</summary>
    Delivering,

    /// <summary>A pull request is open.</summary>
    PullRequest,

    Merged,

    Failed,

    /// <summary>Cancelled, rejected or closed without merging.</summary>
    Stopped
}

public sealed record GoalExecutionState(
    Guid Id,
    TaskExecutionStatus Status,
    ExecutionReviewStatus ReviewStatus,
    ExecutionCommitStatus CommitStatus,
    ExecutionPushStatus PushStatus,
    ExecutionPullRequestStatus PullRequestStatus,
    ExecutionPullRequestRemoteState PullRequestRemoteState,
    ExecutionMergeStatus MergeStatus,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string? ErrorMessage,
    DateTime CreatedAt,
    bool AutomationHeld = false);

/// <summary>Everything the orchestrator and the screen need to know about one goal task, joined from its task, run and analysis.</summary>
public sealed record GoalTaskState(
    Guid GoalTaskId,
    string Key,
    int Position,
    int Wave,
    string Size,
    IReadOnlyList<string> Areas,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> BlockedBy,
    int AnalysisAttempts,
    DateTime? AnalysisRequestedAt,
    string? Note,
    Guid TaskId,
    string Title,
    string Description,
    DevelopmentTaskStatus TaskStatus,
    GoalExecutionState? Execution,
    IReadOnlyList<string> ImpactedFiles,
    IReadOnlyList<string>? StructuralFiles = null);

public sealed record GoalState(
    Guid Id,
    Guid RepositoryWorkspaceId,
    string Title,
    string Text,
    GoalStatus Status,
    string PlanSource,
    long EstimatedInputTokens,
    long EstimatedOutputTokens,
    decimal? EstimatedUsd,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<GoalTaskState> Tasks);

public static class GoalPhases
{
    /// <summary>A requested analysis that never started (a lost job) is retried after this long.</summary>
    public static readonly TimeSpan AnalysisPatience = TimeSpan.FromMinutes(10);

    public static GoalPhase Of(GoalTaskState task, DateTime nowUtc)
    {
        var run = task.Execution;
        if (run is not null)
        {
            if (run.MergeStatus == ExecutionMergeStatus.Merged || run.PullRequestRemoteState == ExecutionPullRequestRemoteState.Merged)
            {
                return GoalPhase.Merged;
            }

            switch (run.Status)
            {
                case TaskExecutionStatus.Failed:
                    return GoalPhase.Failed;
                case TaskExecutionStatus.Cancelled:
                    return GoalPhase.Stopped;
                case TaskExecutionStatus.Pending:
                case TaskExecutionStatus.Running:
                    return GoalPhase.Running;
            }

            if (run.ReviewStatus == ExecutionReviewStatus.Rejected || run.PullRequestRemoteState == ExecutionPullRequestRemoteState.Closed)
            {
                return GoalPhase.Stopped;
            }

            if (run.PullRequestStatus == ExecutionPullRequestStatus.Open)
            {
                return GoalPhase.PullRequest;
            }

            return run.ReviewStatus == ExecutionReviewStatus.Approved ? GoalPhase.Delivering : GoalPhase.InReview;
        }

        return task.TaskStatus switch
        {
            DevelopmentTaskStatus.Failed => GoalPhase.Failed,
            DevelopmentTaskStatus.Rejected => GoalPhase.Stopped,
            DevelopmentTaskStatus.Analyzing => GoalPhase.Analyzing,
            DevelopmentTaskStatus.AwaitingApproval => GoalPhase.PlanReady,
            DevelopmentTaskStatus.Approved => GoalPhase.Queued,
            DevelopmentTaskStatus.Executing => GoalPhase.Running,
            DevelopmentTaskStatus.Completed => GoalPhase.Merged,
            _ => task.AnalysisRequestedAt is { } requested && nowUtc - requested < AnalysisPatience
                ? GoalPhase.Analyzing
                : GoalPhase.Waiting
        };
    }

    /// <summary>An ended task never holds anything back: a failure of one task must not stop the others.</summary>
    public static bool IsSettled(GoalPhase phase) => phase is GoalPhase.Merged or GoalPhase.Failed or GoalPhase.Stopped;

    /// <summary>Work that has started and has not ended: its code is, or soon will be, changing.</summary>
    public static bool IsInFlight(GoalPhase phase) =>
        phase is GoalPhase.Queued or GoalPhase.Running or GoalPhase.InReview or GoalPhase.Delivering or GoalPhase.PullRequest;
}

public interface IGoalStore
{
    Task AddAsync(Goal goal, CancellationToken cancellationToken = default);

    Task<GoalState?> GetAsync(Guid goalId, Guid? repositoryWorkspaceId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoalState>> ListAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken = default);

    Task MarkAnalysisRequestedAsync(Guid goalTaskId, CancellationToken cancellationToken = default);

    Task SetNoteAsync(Guid goalTaskId, string? note, CancellationToken cancellationToken = default);

    Task SetStatusAsync(Guid goalId, GoalStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="work"/> so that everything it saves is kept only if <paramref name="shouldCommit"/> accepts the
    /// result, and is undone otherwise. The default has no transaction to offer and just runs the work.
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, Func<T, bool> shouldCommit, CancellationToken cancellationToken = default) => work();
}

/// <summary>Runs a task's impact analysis in the background so the goal loop never waits for a model.</summary>
public interface IGoalAnalysisDispatcher
{
    void EnqueueAnalysis(Guid taskId);
}
