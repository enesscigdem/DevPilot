namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Persistence for "request changes": the reviewer's feedback is applied on the execution's own branch and
/// worktree, so the same pull request is updated instead of a new execution being started.
/// </summary>
public interface IExecutionRevisionStore
{
    /// <summary>
    /// Moves a completed, not-yet-merged execution back to Running, takes a lease and records the feedback.
    /// Returns false when the row is no longer eligible, a delivery step is in flight, or another execution
    /// for the task is already active. The review decision is left alone until the code actually changes.
    /// </summary>
    Task<bool> ClaimCompletedForRevisionAsync(
        Guid executionId,
        Guid leaseToken,
        string feedback,
        DateTime requestedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the execution back to Completed when the background job could not be queued.
    /// Only the worker that holds <paramref name="leaseToken"/> can restore it.
    /// </summary>
    Task<bool> RestoreCompletedAfterRevisionDispatchFailureAsync(
        Guid executionId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Called once the worktree holds new, unreviewed changes. Counts the revision, removes the review approval,
    /// clears the commit and push markers of the earlier delivery (the pull request number and the last pushed SHA
    /// stay, so the same pull request is updated) and forgets the CI result of the previous head.
    /// </summary>
    Task<bool> MarkRevisionPendingDeliveryAsync(
        Guid executionId,
        CancellationToken cancellationToken = default);

    /// <summary>Stores a short outcome of the latest requested fix, shown next to the feedback in the review.</summary>
    Task SetRevisionResultAsync(
        Guid executionId,
        string result,
        CancellationToken cancellationToken = default);
}
