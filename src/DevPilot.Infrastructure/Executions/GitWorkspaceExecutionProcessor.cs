using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Execution processor that prepares an isolated Git worktree workspace and dedicated
/// branch for a task execution without modifying the original repository or running AI providers.
/// </summary>
public sealed class GitWorkspaceExecutionProcessor : IExecutionProcessor
{
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IExecutionRepository _executionRepository;
    private readonly ILogger<GitWorkspaceExecutionProcessor> _logger;

    public GitWorkspaceExecutionProcessor(
        IExecutionWorkspaceManager workspaceManager,
        IExecutionRepository executionRepository,
        ILogger<GitWorkspaceExecutionProcessor> logger)
    {
        _workspaceManager = workspaceManager;
        _executionRepository = executionRepository;
        _logger = logger;
    }

    public async Task ProcessAsync(
        ExecutionProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: starting workspace preparation for execution {ExecutionId} (Task '{TaskTitle}' - {TaskId}).",
            context.ExecutionId,
            context.TaskTitle,
            context.TaskId);

        // 1. Prepare isolated workspace & dedicated branch
        var result = await _workspaceManager.PrepareWorkspaceAsync(
            executionId: context.ExecutionId,
            taskId: context.TaskId,
            sourceRepositoryLocalPath: context.WorkspaceLocalPath,
            sourceBranch: null,
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            var errorMessage = $"Execution workspace preparation failed: {result.ErrorMessage}";
            _logger.LogError(
                "GitWorkspaceExecutionProcessor: preparation failed for execution {ExecutionId}. Error: {Error}",
                context.ExecutionId,
                errorMessage);

            throw new InvalidOperationException(errorMessage);
        }

        // 2. Persist workspace path and branch name to TaskExecution record
        await _executionRepository
            .UpdateWorkspaceDetailsAsync(
                context.ExecutionId,
                result.WorkspacePath,
                result.BranchName,
                cancellationToken)
            .ConfigureAwait(false);

        // 3. Log truthful preparation information (no files modified, no commits, no AI provider called)
        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: successfully prepared isolated workspace at '{WorkspacePath}' on branch '{BranchName}' for execution {ExecutionId}. " +
            "No source code files modified, no commits made, no AI provider called.",
            result.WorkspacePath,
            result.BranchName,
            context.ExecutionId);
    }
}
