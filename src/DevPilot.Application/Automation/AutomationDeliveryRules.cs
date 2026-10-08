namespace DevPilot.Application.Automation;

/// <summary>
/// Delivery steps (commit, push) claim a lease before they act and release it when they finish. A process that dies
/// in between leaves the lease behind with the work already done, and the step's own crash recovery can only run
/// once the lease is stale. These rules let automation come back for such executions instead of leaving them stuck.
/// </summary>
public static class AutomationDeliveryRules
{
    /// <summary>How long a claimed lease is respected. Must match the lease the commit and push commands enforce.</summary>
    public static readonly TimeSpan StaleLeaseAfter = TimeSpan.FromMinutes(2);

    /// <summary>A lease with no claim time is anomalous (status and time are written together), so it is not treated as stale.</summary>
    public static bool IsLeaseStale(DateTime? claimedAt, DateTime nowUtc) =>
        claimedAt.HasValue && nowUtc - claimedAt.Value >= StaleLeaseAfter;
}
