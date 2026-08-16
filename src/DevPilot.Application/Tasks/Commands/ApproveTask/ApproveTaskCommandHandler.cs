using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Tasks.Commands.ApproveTask;

public interface IApproveTaskCommandHandler
{
    Task<ApproveTaskResult> HandleAsync(
        ApproveTaskCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class ApproveTaskCommandHandler : IApproveTaskCommandHandler
{
    private readonly ITaskRepository _taskRepository;
    private readonly IImpactAnalysisRepository _analysisRepository;
    private readonly ILogger<ApproveTaskCommandHandler> _logger;

    public ApproveTaskCommandHandler(
        ITaskRepository taskRepository,
        IImpactAnalysisRepository analysisRepository,
        ILogger<ApproveTaskCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _analysisRepository = analysisRepository;
        _logger = logger;
    }

    public async Task<ApproveTaskResult> HandleAsync(
        ApproveTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository
            .GetByIdAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return new ApproveTaskResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = "Task not found.",
            };
        }

        if (task.Status != DevelopmentTaskStatus.AwaitingApproval)
        {
            return new ApproveTaskResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage =
                    $"Cannot approve a task that is in '{task.Status}' status. " +
                    "Only tasks in 'AwaitingApproval' status may be approved.",
            };
        }

        // Verify a completed impact analysis exists for the task.
        var analysis = await _analysisRepository
            .GetLatestByTaskIdAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (analysis is null || analysis.Status != ImpactAnalysisStatus.Completed)
        {
            return new ApproveTaskResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage =
                    "A completed impact analysis is required before a task can be approved.",
            };
        }

        task.Status = DevelopmentTaskStatus.Approved;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Approved development task {TaskId}.",
            task.Id);

        return new ApproveTaskResult
        {
            Success = true,
            Task = new TaskDto
            {
                Id = task.Id,
                RepositoryWorkspaceId = task.RepositoryWorkspaceId,
                RepositoryWorkspaceName =
                    $"{task.RepositoryWorkspace.Owner}/{task.RepositoryWorkspace.Repository}",
                RepositoryOwner = task.RepositoryWorkspace.Owner,
                RepositoryName = task.RepositoryWorkspace.Repository,
                Title = task.Title,
                Description = task.Description,
                AcceptanceCriteria = task.AcceptanceCriteria,
                Priority = task.Priority,
                Status = task.Status,
                CreatedAt = task.CreatedAt,
                UpdatedAt = task.UpdatedAt,
            },
        };
    }
}
