namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Enqueues an execution for background processing.
/// Abstracts the concrete queuing mechanism (e.g. Hangfire) from the
/// Application layer so that the Application project stays free of
/// infrastructure dependencies.
/// </summary>
public interface IExecutionDispatcher
{
    /// <summary>
    /// Enqueues a background job that will process the given execution.
    /// Returns immediately; the job runs asynchronously on a worker.
    /// </summary>
    void EnqueueProcessExecution(Guid executionId);
}
