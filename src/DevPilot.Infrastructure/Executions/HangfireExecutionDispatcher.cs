using DevPilot.Application.Executions.Ports;
using Hangfire;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Hangfire-backed implementation of <see cref="IExecutionDispatcher"/>.
/// Enqueues an <see cref="ExecutionWorkerJob"/> fire-and-forget job so that the
/// HTTP request returns immediately while the worker picks it up in the background.
/// </summary>
public sealed class HangfireExecutionDispatcher : IExecutionDispatcher
{
    private readonly IBackgroundJobClient _jobClient;

    public HangfireExecutionDispatcher(IBackgroundJobClient jobClient)
    {
        _jobClient = jobClient;
    }

    public void EnqueueProcessExecution(Guid executionId)
    {
        _jobClient.Enqueue<ExecutionWorkerJob>(job => job.ExecuteAsync(executionId));
    }
}
