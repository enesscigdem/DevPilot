using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Constants;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.StartExecution;

public sealed record StartExecutionCommand(Guid TaskId);

public sealed class StartExecutionResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>True when the task was not found.</summary>
    public bool NotFound { get; set; }

    /// <summary>True when the transition is invalid (wrong status, missing analysis, duplicate active execution, etc.).</summary>
    public bool Conflict { get; set; }

    public ExecutionDto? Execution { get; set; }
}

public interface IStartExecutionCommandHandler
{
    Task<StartExecutionResult> HandleAsync(
        StartExecutionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class StartExecutionCommandHandler : IStartExecutionCommandHandler
{
    private readonly ITaskRepository _taskRepository;
    private readonly IImpactAnalysisRepository _analysisRepository;
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionDispatcher _dispatcher;
    private readonly ILogger<StartExecutionCommandHandler> _logger;

    public StartExecutionCommandHandler(
        ITaskRepository taskRepository,
        IImpactAnalysisRepository analysisRepository,
        IExecutionRepository executionRepository,
        IExecutionDispatcher dispatcher,
        ILogger<StartExecutionCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _analysisRepository = analysisRepository;
        _executionRepository = executionRepository;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<StartExecutionResult> HandleAsync(
        StartExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        // --- Load & validate task ---
        var task = await _taskRepository
            .GetByIdAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return new StartExecutionResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = "Task not found.",
            };
        }

        if (task.Status != DevelopmentTaskStatus.Approved)
        {
            return new StartExecutionResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage =
                    $"Cannot start an execution for a task in '{task.Status}' status. " +
                    "Only tasks in 'Approved' status may be executed.",
            };
        }

        // --- Require a completed impact analysis ---
        var analysis = await _analysisRepository
            .GetLatestByTaskIdAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (analysis is null || analysis.Status != ImpactAnalysisStatus.Completed)
        {
            return new StartExecutionResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = "A completed impact analysis is required before a task can be executed.",
            };
        }

        if (analysis.StructuredResult?.ImpactedFiles is not null &&
            analysis.StructuredResult.ImpactedFiles.Count > ExecutionCapacityPolicy.MaxImpactedFiles)
        {
            return new StartExecutionResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = $"Cannot execute task: approved plan contains {analysis.StructuredResult.ImpactedFiles.Count} files, exceeding maximum executable capacity of {ExecutionCapacityPolicy.MaxImpactedFiles} files.",
            };
        }

        // --- Optimistic pre-check (common path; DB index is the authoritative guard) ---
        var hasActive = await _executionRepository
            .HasActiveExecutionForTaskAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (hasActive)
        {
            return new StartExecutionResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = "An active execution already exists for this task.",
            };
        }

        // --- Build the new execution record and advance the task status ---
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = command.TaskId,
            Status = TaskExecutionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        // Mutate the already-tracked task entity; the repository will stage it
        // alongside the execution insert and flush both in one SaveChangesAsync.
        task.Status = DevelopmentTaskStatus.Executing;
        task.UpdatedAt = DateTime.UtcNow;

        // --- Atomic persist: one SaveChangesAsync = one implicit DB transaction ---
        var persisted = await _executionRepository
            .StartExecutionAtomicAsync(execution, task, cancellationToken)
            .ConfigureAwait(false);

        if (!persisted)
        {
            // Unique partial index caught a concurrent insert for the same task.
            return new StartExecutionResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = "An active execution already exists for this task.",
            };
        }

        _logger.LogInformation(
            "Started execution {ExecutionId} for development task {TaskId}.",
            execution.Id,
            task.Id);

        // Enqueue the background worker; the HTTP response returns immediately.
        // If the enqueue itself fails we compensate immediately: transition the
        // execution to Failed and the task back to Failed so the DB reflects truth.
        // This is a best-effort compensation — if FailAsync also throws the caller
        // will see a 500, which is still more honest than leaving the row Pending forever.
        try
        {
            _dispatcher.EnqueueProcessExecution(execution.Id);
        }
        catch (Exception ex)
        {
            const string enqueueError = "Failed to enqueue background processing job.";
            _logger.LogError(ex,
                "StartExecution: dispatch failed for execution {ExecutionId}. Compensating.",
                execution.Id);

            await _executionRepository
                .FailAsync(execution.Id, enqueueError, cancellationToken)
                .ConfigureAwait(false);

            return new StartExecutionResult
            {
                Success = false,
                ErrorMessage = enqueueError,
            };
        }

        _logger.LogInformation(
            "Enqueued background processing for execution {ExecutionId}.",
            execution.Id);

        return new StartExecutionResult
        {
            Success = true,
            Execution = MapToDto(execution, task),
        };
    }

    private static ExecutionDto MapToDto(TaskExecution execution, DevelopmentTask task) =>
        new()
        {
            Id = execution.Id,
            DevelopmentTaskId = execution.DevelopmentTaskId,
            TaskTitle = task.Title,
            RepositoryWorkspaceId = task.RepositoryWorkspaceId,
            RepositoryOwner = task.RepositoryWorkspace.Owner,
            RepositoryName = task.RepositoryWorkspace.Repository,
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
