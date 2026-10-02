namespace DevPilot.Application.AiProviders;

/// <summary>
/// Scoped holder that tells the AI router which model the running execution is pinned to.
/// The execution processor sets it when a job starts; the router reads it on every call.
/// </summary>
public interface IAiExecutionContext
{
    /// <summary>When set, all AI calls in this scope use this model, ignoring stage assignments.</summary>
    Guid? PinnedModelId { get; set; }
}

public sealed class AiExecutionContext : IAiExecutionContext
{
    public Guid? PinnedModelId { get; set; }
}
