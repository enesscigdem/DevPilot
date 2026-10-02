using DevPilot.Application.Executions.Dtos;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.RepositoryWorkspaces.Ports;

public sealed record InsightExecutionInput(
    Guid ExecutionId,
    Guid TaskId,
    string TaskTitle,
    TaskExecutionStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    ExecutionVerificationSnapshot Snapshot);

public interface IWorkspaceInsightsReader
{
    /// <summary>Returns the newest terminal executions with their verdict/usage snapshots, or null when the workspace does not exist.</summary>
    Task<IReadOnlyList<InsightExecutionInput>?> ReadAsync(
        Guid workspaceId,
        int maxExecutions,
        CancellationToken cancellationToken = default);
}
