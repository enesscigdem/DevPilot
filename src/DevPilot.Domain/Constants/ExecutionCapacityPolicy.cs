namespace DevPilot.Domain.Constants;

/// <summary>
/// Authoritative shared capacity policy for Change Intelligence, task approval, execution dispatch, and Developer Agent generation.
/// </summary>
public static class ExecutionCapacityPolicy
{
    /// <summary>
    /// The maximum number of impacted files permitted in a single executable change plan (20).
    /// Supports standard 10–15 file feature changes across multi-project architectures.
    /// </summary>
    public const int MaxImpactedFiles = 20;

    /// <summary>
    /// The maximum number of generation calls allowed for an execution wave (40).
    /// Supports base generations, compact retries, and bounded repair rounds.
    /// </summary>
    public const int MaxGenerationCalls = 40;
}
