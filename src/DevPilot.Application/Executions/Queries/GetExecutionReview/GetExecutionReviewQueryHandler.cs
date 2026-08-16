using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Queries.GetExecutionReview;

public sealed class GetExecutionReviewQueryHandler : IGetExecutionReviewQueryHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IExecutionGitDiffReader _gitDiffReader;
    private readonly ILogger<GetExecutionReviewQueryHandler> _logger;

    public GetExecutionReviewQueryHandler(
        IExecutionRepository executionRepository,
        IExecutionWorkspaceManager workspaceManager,
        IExecutionGitDiffReader gitDiffReader,
        ILogger<GetExecutionReviewQueryHandler> logger)
    {
        _executionRepository = executionRepository;
        _workspaceManager = workspaceManager;
        _gitDiffReader = gitDiffReader;
        _logger = logger;
    }

    public async Task<GetExecutionReviewResult> HandleAsync(
        GetExecutionReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(query.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return GetExecutionReviewResult.NotFound("Execution not found.");
        }

        if (execution.Status == TaskExecutionStatus.Pending || execution.Status == TaskExecutionStatus.Running)
        {
            return GetExecutionReviewResult.Conflict(
                $"Execution is currently {execution.Status} and cannot be reviewed yet.");
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return GetExecutionReviewResult.Conflict(
                "Execution workspace path or branch name is not configured.");
        }

        var verificationResult = await _workspaceManager
            .VerifyWorkspaceStateAsync(
                execution.WorkspacePath,
                execution.BranchName,
                requireClean: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!verificationResult.IsValid)
        {
            return GetExecutionReviewResult.Conflict(
                $"Execution workspace verification failed: {verificationResult.ErrorMessage}");
        }

        var diffResult = await _gitDiffReader
            .ReadWorkspaceDiffAsync(
                execution.WorkspacePath,
                execution.BranchName,
                cancellationToken)
            .ConfigureAwait(false);

        if (!diffResult.Success)
        {
            return GetExecutionReviewResult.Conflict(
                $"Failed to read Git execution review diff: {diffResult.ErrorMessage}");
        }

        var (buildStatus, testStatus) = DetermineStageStatuses(execution);

        var review = new ExecutionReviewDto(
            ExecutionId: execution.Id,
            TaskId: execution.DevelopmentTaskId,
            TaskTitle: execution.DevelopmentTask?.Title ?? string.Empty,
            ExecutionStatus: execution.Status.ToString(),
            BranchName: execution.BranchName,
            ChangedFileCount: diffResult.ChangedFiles?.Count ?? 0,
            ChangedFiles: diffResult.ChangedFiles ?? Array.Empty<ExecutionReviewFileDto>(),
            Diff: diffResult.DiffText,
            DiffTruncated: diffResult.DiffTruncated,
            Build: new ExecutionReviewStageStatusDto(buildStatus),
            Test: new ExecutionReviewStageStatusDto(testStatus),
            ReviewStatus: execution.ReviewStatus.ToString(),
            DecidedAt: execution.ReviewDecidedAt,
            RejectionReason: execution.ReviewRejectionReason);

        return GetExecutionReviewResult.Ok(review);
    }

    private static (string BuildStatus, string TestStatus) DetermineStageStatuses(Domain.Entities.TaskExecution execution)
    {
        if (execution.Status == TaskExecutionStatus.Completed)
        {
            return ("Passed", "Passed");
        }

        if (execution.Status == TaskExecutionStatus.Failed)
        {
            var err = execution.ErrorMessage ?? string.Empty;
            if (err.Contains("Build validation failed", StringComparison.OrdinalIgnoreCase))
            {
                return ("Failed", "Unknown");
            }
            if (err.Contains("Test validation failed", StringComparison.OrdinalIgnoreCase))
            {
                return ("Passed", "Failed");
            }
            return ("Unknown", "Unknown");
        }

        // Cancelled or any other non-terminal / custom state
        return ("Unknown", "Unknown");
    }
}
