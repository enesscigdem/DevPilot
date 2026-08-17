using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Tasks.Commands.UpdateTask;

public interface IUpdateTaskCommandHandler
{
    Task<UpdateTaskResult> HandleAsync(
        UpdateTaskCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class UpdateTaskCommandHandler : IUpdateTaskCommandHandler
{
    private readonly ITaskRepository _taskRepository;
    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly ILogger<UpdateTaskCommandHandler> _logger;

    public UpdateTaskCommandHandler(
        ITaskRepository taskRepository,
        IRepositoryWorkspaceQuery workspaceQuery,
        ILogger<UpdateTaskCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _workspaceQuery = workspaceQuery;
        _logger = logger;
    }

    public async Task<UpdateTaskResult> HandleAsync(
        UpdateTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        var dto = command.Dto;

        var validationError = Validate(dto);
        if (!string.IsNullOrEmpty(validationError))
        {
            return new UpdateTaskResult
            {
                Success = false,
                ErrorMessage = validationError,
            };
        }

        var task = await _taskRepository
            .GetByIdAsync(command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return new UpdateTaskResult
            {
                Success = false,
                ErrorMessage = "Task not found.",
            };
        }

        task.Title = dto.Title.Trim();
        task.Description = dto.Description.Trim();
        task.AcceptanceCriteria = dto.AcceptanceCriteria?.Trim();
        task.Priority = dto.Priority;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);

        var workspace = await _workspaceQuery
            .GetByIdAsync(task.RepositoryWorkspaceId, cancellationToken)
            .ConfigureAwait(false)
            ?? task.RepositoryWorkspace;

        _logger.LogInformation(
            "Updated development task {TaskId}.",
            task.Id);

        return new UpdateTaskResult
        {
            Success = true,
            Task = MapToDto(task, workspace),
        };
    }

    private static string? Validate(UpdateTaskDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            return "Title is required.";
        }

        if (string.IsNullOrWhiteSpace(dto.Description))
        {
            return "Description is required.";
        }

        if (dto.Title.Length > 200)
        {
            return "Title must be at most 200 characters.";
        }

        if (dto.Description.Length > 10000)
        {
            return "Description must be at most 10,000 characters.";
        }

        if (!Enum.IsDefined(typeof(DevPilot.Domain.Enums.DevelopmentTaskPriority), dto.Priority))
        {
            return "Invalid priority value.";
        }

        return null;
    }

    private static TaskDto MapToDto(Domain.Entities.DevelopmentTask task, Domain.Entities.RepositoryWorkspace workspace)
    {
        return new TaskDto
        {
            Id = task.Id,
            RepositoryWorkspaceId = task.RepositoryWorkspaceId,
            RepositoryWorkspaceName = $"{workspace.Owner}/{workspace.Repository}",
            RepositoryOwner = workspace.Owner,
            RepositoryName = workspace.Repository,
            Title = task.Title,
            Description = task.Description,
            AcceptanceCriteria = task.AcceptanceCriteria,
            Priority = task.Priority,
            Status = task.Status,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
        };
    }
}
