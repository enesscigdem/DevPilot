namespace DevPilot.Application.Executions.Ports;

public interface IExecutionHeartbeatService
{
    /// <summary>
    /// Starts a background heartbeat timer that periodically renews the execution lease.
    /// Each renewal is performed using a fresh service scope and DbContext.
    /// Returns an IAsyncDisposable that stops the heartbeat timer cleanly when disposed.
    /// </summary>
    IAsyncDisposable StartHeartbeat(
        Guid executionId,
        Guid leaseToken,
        TimeSpan interval,
        TimeSpan leaseDuration,
        CancellationTokenSource linkedCts);
}
