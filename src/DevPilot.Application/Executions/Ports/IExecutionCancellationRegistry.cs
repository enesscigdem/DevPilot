namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Manages in-memory cancellation tokens for active in-process executions.
/// The database is the source of truth; this registry provides low-latency in-process signal dispatch.
/// </summary>
public interface IExecutionCancellationRegistry
{
    CancellationToken Register(Guid executionId, CancellationToken parentToken = default);
    bool TryCancel(Guid executionId);
    void Unregister(Guid executionId);
}
