using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Tasks.Commands.RejectTask;

public interface IRejectTaskCommandHandler
{
    Task<RejectTaskResult> HandleAsync(
        RejectTaskCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class RejectTaskCommandHandler : IRejectTaskCommandHandler
{
    private readonly ITaskRepository _taskRepository;
    private readonly ILogger<RejectTaskCommandHandler> _logger;

    public RejectTaskCommandHandler(
        ITaskRepository taskRepository,
        ILogger<RejectTaskCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _logger = logger;
    }

    public async Task<RejectTaskResult> HandleAsync(
        RejectTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository
            .GetByIdAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return new RejectTaskResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = "Task not found.",
            };
        }

        if (task.Status != DevelopmentTaskStatus.AwaitingApproval)
        {
            return new RejectTaskResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage =
                    $"Cannot reject a task that is in '{task.Status}' status. " +
                    "Only tasks in 'AwaitingApproval' status may be rejected.",
            };
        }

        task.Status = DevelopmentTaskStatus.Rejected;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Rejected development task {TaskId}.",
            task.Id);

        return new RejectTaskResult
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
