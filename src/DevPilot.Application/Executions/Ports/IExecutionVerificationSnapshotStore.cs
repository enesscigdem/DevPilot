namespace DevPilot.Application.Executions.Ports;

/// <summary>Persists the terminal verification snapshot on an execution (history, no log replay needed).</summary>
public interface IExecutionVerificationSnapshotStore
{
    Task SaveAsync(
        Guid executionId,
        string outcome,
        string snapshotJson,
        CancellationToken cancellationToken = default);
}

/// <summary>Computes and stores the terminal verification snapshot for a finished execution. Never throws.</summary>
public interface IExecutionVerificationSnapshotRecorder
{
    Task RecordAsync(Guid executionId, CancellationToken cancellationToken = default);
}
