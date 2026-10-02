namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Persistence for re-running build and test on an execution that already finished, without starting a new one.
/// </summary>
public interface IExecutionVerificationRerunStore
{
    /// <summary>
    /// Moves a completed, not-yet-delivered execution back to Running and takes a lease.
    /// Returns false when the row is no longer eligible or another execution for the task is already active.
    /// </summary>
    Task<bool> ClaimCompletedForVerificationAsync(
        Guid executionId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the execution back to Completed when the background job could not be queued.
    /// Only the worker that holds <paramref name="leaseToken"/> can restore it.
    /// </summary>
    Task<bool> RestoreCompletedAfterVerificationDispatchFailureAsync(
        Guid executionId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears an approval so the reviewed tree cannot be committed after verification changed it.
    /// </summary>
    Task<bool> TryInvalidateReviewApprovalAsync(
        Guid executionId,
        CancellationToken cancellationToken = default);
}
