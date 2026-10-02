namespace DevPilot.Application.Executions.Ports;

/// <summary>Enqueues the background job that applies reviewer feedback to an execution's existing worktree.</summary>
public interface IExecutionRevisionDispatcher
{
    /// <summary>The lease was already claimed by the request handler.</summary>
    void EnqueueReviseExecution(Guid executionId, Guid leaseToken);
}
