using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.Tasks.Ports;
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
    private readonly ILogger<StartExecutionCommandHandler> _logger;

    public StartExecutionCommandHandler(
        ITaskRepository taskRepository,
        IImpactAnalysisRepository analysisRepository,
        IExecutionRepository executionRepository,
        ILogger<StartExecutionCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _analysisRepository = analysisRepository;
        _executionRepository = executionRepository;
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
        };
}
