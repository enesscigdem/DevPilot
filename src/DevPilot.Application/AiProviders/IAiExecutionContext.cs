namespace DevPilot.Application.AiProviders;

/// <summary>
/// Scoped holder that tells the AI router which model the running execution is pinned to.
/// The execution processor sets it when a job starts; the router reads it on every call.
/// </summary>
public interface IAiExecutionContext
{
    /// <summary>When set, all AI calls in this scope use this model, ignoring stage assignments.</summary>
    Guid? PinnedModelId { get; set; }

    /// <summary>The execution these calls belong to, so the router can record which model really answered.</summary>
    Guid? ExecutionId { get; set; }

    /// <summary>Receives timeouts, retries and cancellations of provider calls made in this scope.</summary>
    Func<AiAttemptEvent, Task>? AttemptObserver { get; set; }
}

public sealed class AiExecutionContext : IAiExecutionContext
{
    public Guid? PinnedModelId { get; set; }

    public Guid? ExecutionId { get; set; }

    public Func<AiAttemptEvent, Task>? AttemptObserver { get; set; }
}
