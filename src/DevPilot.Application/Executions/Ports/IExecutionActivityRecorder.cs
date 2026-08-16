using DevPilot.Application.Executions.Models;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Best-effort recorder for execution-stage activity telemetry.
/// Implementations must be completely isolated and fail-safe so telemetry writes
/// never poison the execution pipeline or trigger AI/execution retries.
/// </summary>
public interface IExecutionActivityRecorder
{
    Task RecordActivityAsync(
        Guid executionId,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        ExecutionActivityMetadata? metadata = null,
        CancellationToken cancellationToken = default);
}
