using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;

namespace DevPilot.Application.Executions.Queries.GetExecutionRevisionDiff;

public sealed record GetExecutionRevisionDiffQuery(Guid ExecutionId, Guid? RepositoryWorkspaceId = null);

public enum GetExecutionRevisionDiffStatus
{
    Success,
    NotFound,
    Conflict
}

public sealed class GetExecutionRevisionDiffResult
{
    public GetExecutionRevisionDiffStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    public ExecutionRevisionDiffDto? Diff { get; private set; }

    public static GetExecutionRevisionDiffResult Ok(ExecutionRevisionDiffDto diff) =>
        new() { Status = GetExecutionRevisionDiffStatus.Success, Diff = diff };

    public static GetExecutionRevisionDiffResult NotFound(string message) =>
        new() { Status = GetExecutionRevisionDiffStatus.NotFound, ErrorMessage = message };

    public static GetExecutionRevisionDiffResult Conflict(string message) =>
        new() { Status = GetExecutionRevisionDiffStatus.Conflict, ErrorMessage = message };
}

public interface IGetExecutionRevisionDiffQueryHandler
{
    Task<GetExecutionRevisionDiffResult> HandleAsync(
        GetExecutionRevisionDiffQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>The diff of just the latest requested fix: the difference between the worktree snapshots taken around it.</summary>
public sealed class GetExecutionRevisionDiffQueryHandler : IGetExecutionRevisionDiffQueryHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionGitDiffReader _gitDiffReader;

    public GetExecutionRevisionDiffQueryHandler(
        IExecutionRepository executionRepository,
        IExecutionGitDiffReader gitDiffReader)
    {
        _executionRepository = executionRepository;
        _gitDiffReader = gitDiffReader;
    }

    public async Task<GetExecutionRevisionDiffResult> HandleAsync(
        GetExecutionRevisionDiffQuery query,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(query.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null ||
            (query.RepositoryWorkspaceId.HasValue &&
             execution.DevelopmentTask?.RepositoryWorkspaceId != query.RepositoryWorkspaceId.Value))
        {
            return GetExecutionRevisionDiffResult.NotFound("Execution not found.");
        }

        if (!ExecutionRevisionScope.HasRevision(execution) || ExecutionRevisionScope.IsActive(execution))
        {
            return GetExecutionRevisionDiffResult.Conflict("There is no finished requested fix to show.");
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) ||
            string.IsNullOrWhiteSpace(execution.RevisionBaseSnapshotSha) ||
            string.IsNullOrWhiteSpace(execution.RevisionResultSnapshotSha))
        {
            return GetExecutionRevisionDiffResult.Conflict("The worktree snapshots of this fix were not recorded.");
        }

        var diff = await _gitDiffReader
            .ReadCommittedDiffAsync(
                execution.WorkspacePath,
                execution.RevisionBaseSnapshotSha,
                execution.RevisionResultSnapshotSha,
                cancellationToken)
            .ConfigureAwait(false);

        if (!diff.Success)
        {
            return GetExecutionRevisionDiffResult.Conflict($"Failed to read the fix diff: {diff.ErrorMessage}");
        }

        return GetExecutionRevisionDiffResult.Ok(new ExecutionRevisionDiffDto(
            diff.ChangedFiles ?? Array.Empty<ExecutionReviewFileDto>(),
            diff.DiffText,
            diff.DiffTruncated));
    }
}
