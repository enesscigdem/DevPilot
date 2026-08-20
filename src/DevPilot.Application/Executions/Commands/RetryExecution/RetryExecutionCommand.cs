using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.RetryExecution;

public sealed record RetryExecutionCommand(
    Guid TaskId,
    Guid? RepositoryWorkspaceId = null);

public sealed class RetryExecutionResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>True when the task was not found or workspace ownership check failed.</summary>
    public bool NotFound { get; set; }

    /// <summary>True when the retry is ineligible (wrong status, missing analysis, duplicate active execution, etc.).</summary>
    public bool Conflict { get; set; }

    public ExecutionDto? Execution { get; set; }

    public static RetryExecutionResult Ok(ExecutionDto execution) =>
        new() { Success = true, Execution = execution };

    public static RetryExecutionResult TaskNotFound(string message = "Task not found.") =>
        new() { Success = false, NotFound = true, ErrorMessage = message };

    public static RetryExecutionResult ConflictResult(string message) =>
        new() { Success = false, Conflict = true, ErrorMessage = message };

    public static RetryExecutionResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}

public interface IRetryExecutionCommandHandler
{
    Task<RetryExecutionResult> HandleAsync(
        RetryExecutionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class RetryExecutionCommandHandler : IRetryExecutionCommandHandler
{
    private readonly ITaskRepository _taskRepository;
    private readonly IImpactAnalysisRepository _analysisRepository;
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionDispatcher _dispatcher;
    private readonly ILogger<RetryExecutionCommandHandler> _logger;

    public RetryExecutionCommandHandler(
        ITaskRepository taskRepository,
        IImpactAnalysisRepository analysisRepository,
        IExecutionRepository executionRepository,
        IExecutionDispatcher dispatcher,
        ILogger<RetryExecutionCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _analysisRepository = analysisRepository;
        _executionRepository = executionRepository;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<RetryExecutionResult> HandleAsync(
        RetryExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            return RetryExecutionResult.Fail("Command is required.");
        }

        // 1. Load task
        var task = await _taskRepository
            .GetByIdAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return RetryExecutionResult.TaskNotFound("Task not found.");
        }

        // 2. Verify repository workspace ownership
        if (command.RepositoryWorkspaceId.HasValue &&
            task.RepositoryWorkspaceId != command.RepositoryWorkspaceId.Value)
        {
            return RetryExecutionResult.TaskNotFound("Task not found.");
        }

        // 3. Status eligibility: reject non-eligible task statuses
        if (task.Status == DevelopmentTaskStatus.Draft ||
            task.Status == DevelopmentTaskStatus.ReadyForAnalysis ||
            task.Status == DevelopmentTaskStatus.Analyzing ||
            task.Status == DevelopmentTaskStatus.AwaitingApproval ||
            task.Status == DevelopmentTaskStatus.Rejected)
        {
            return RetryExecutionResult.ConflictResult(
                $"Cannot retry execution for a task in '{task.Status}' status. " +
                "Only tasks that previously passed approval and failed execution may be retried.");
        }

        if (task.Status == DevelopmentTaskStatus.Completed)
        {
            return RetryExecutionResult.ConflictResult(
                "Cannot retry execution for a completed task.");
        }

        if (task.Status != DevelopmentTaskStatus.Failed && task.Status != DevelopmentTaskStatus.Approved)
        {
            return RetryExecutionResult.ConflictResult(
                $"Cannot retry execution for a task in '{task.Status}' status.");
        }

        // 4. Require approved impact analysis / plan evidence
        var analysis = await _analysisRepository
            .GetLatestByTaskIdAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (analysis is null || analysis.Status != ImpactAnalysisStatus.Completed)
        {
            return RetryExecutionResult.ConflictResult(
                "A completed impact analysis is required before a task can be retried.");
        }

        // 5. Require a historical failed execution for this task
        var hasFailed = await _executionRepository
            .HasFailedExecutionForTaskAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (!hasFailed)
        {
            return RetryExecutionResult.ConflictResult(
                "No failed execution exists for this task to retry.");
        }

        // 6. Optimistic pre-check: ensure NO active (Pending or Running) execution exists
        var hasActive = await _executionRepository
            .HasActiveExecutionForTaskAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (hasActive)
        {
            return RetryExecutionResult.ConflictResult(
                "An active execution already exists for this task.");
        }

        // 7. Create NEW execution attempt (preserving old execution untouched)
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = command.TaskId,
            Status = TaskExecutionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        task.Status = DevelopmentTaskStatus.Executing;
        task.UpdatedAt = DateTime.UtcNow;

        // 8. Atomic persist: stages both the TaskExecution insert and DevelopmentTask status update
        var persisted = await _executionRepository
            .StartExecutionAtomicAsync(execution, task, cancellationToken)
            .ConfigureAwait(false);

        if (!persisted)
        {
            // Unique partial index caught a concurrent insert for the same task
            return RetryExecutionResult.ConflictResult(
                "An active execution already exists for this task.");
        }

        _logger.LogInformation(
            "Retrying execution: created new execution attempt {ExecutionId} for development task {TaskId}.",
            execution.Id,
            task.Id);

        // 9. Enqueue background execution
        try
        {
            _dispatcher.EnqueueProcessExecution(execution.Id);
        }
        catch (Exception ex)
        {
            const string enqueueError = "Failed to enqueue background processing job.";
            _logger.LogError(ex,
                "RetryExecution: dispatch failed for execution {ExecutionId}. Compensating.",
                execution.Id);

            await _executionRepository
                .FailAsync(execution.Id, enqueueError, cancellationToken)
                .ConfigureAwait(false);

            return RetryExecutionResult.Fail(enqueueError);
        }

        _logger.LogInformation(
            "Enqueued background processing for retried execution {ExecutionId}.",
            execution.Id);

        return RetryExecutionResult.Ok(MapToDto(execution, task));
    }

    private static ExecutionDto MapToDto(TaskExecution execution, DevelopmentTask task) =>
        new()
        {
            Id = execution.Id,
            DevelopmentTaskId = execution.DevelopmentTaskId,
            TaskTitle = task.Title,
            RepositoryWorkspaceId = task.RepositoryWorkspaceId,
            RepositoryOwner = task.RepositoryWorkspace?.Owner ?? string.Empty,
            RepositoryName = task.RepositoryWorkspace?.Repository ?? string.Empty,
            Status = execution.Status,
            CreatedAt = execution.CreatedAt,
            StartedAt = execution.StartedAt,
            CompletedAt = execution.CompletedAt,
            ErrorMessage = execution.ErrorMessage,
            ReviewStatus = execution.ReviewStatus.ToString(),
            CommitStatus = execution.CommitStatus.ToString(),
            CommitSha = execution.CommitSha,
            CommittedAt = execution.CommittedAt,
        };
}
