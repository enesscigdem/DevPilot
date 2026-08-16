using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Tasks.Commands.CreateTask;

public interface ICreateTaskCommandHandler
{
    Task<CreateTaskResult> HandleAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class CreateTaskCommandHandler : ICreateTaskCommandHandler
{
    private readonly ITaskRepository _taskRepository;
    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly ILogger<CreateTaskCommandHandler> _logger;

    public CreateTaskCommandHandler(
        ITaskRepository taskRepository,
        IRepositoryWorkspaceQuery workspaceQuery,
        ILogger<CreateTaskCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _workspaceQuery = workspaceQuery;
        _logger = logger;
    }

    public async Task<CreateTaskResult> HandleAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        var dto = command.Dto;

        var validationError = Validate(dto);
        if (!string.IsNullOrEmpty(validationError))
        {
            return new CreateTaskResult
            {
                Success = false,
                ErrorMessage = validationError,
            };
        }

        var workspace = await _workspaceQuery
            .GetByIdAsync(dto.RepositoryWorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        if (workspace is null)
        {
            return new CreateTaskResult
            {
                Success = false,
                ErrorMessage = "Repository workspace not found.",
            };
        }

        var now = DateTime.UtcNow;
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = dto.RepositoryWorkspaceId,
            Title = dto.Title.Trim(),
            Description = dto.Description.Trim(),
            AcceptanceCriteria = dto.AcceptanceCriteria?.Trim(),
            Priority = dto.Priority,
            Status = DevelopmentTaskStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _taskRepository.AddAsync(task, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Created development task {TaskId} in workspace {WorkspaceId}.",
            task.Id,
            task.RepositoryWorkspaceId);

        return new CreateTaskResult
        {
            Success = true,
            Task = MapToDto(task, workspace),
        };
    }

    private static string? Validate(CreateTaskDto dto)
    {
        if (dto.RepositoryWorkspaceId == Guid.Empty)
        {
            return "Repository workspace is required.";
        }

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

        if (!Enum.IsDefined(typeof(DevelopmentTaskPriority), dto.Priority))
        {
            return "Invalid priority value.";
        }

        return null;
    }

    private static TaskDto MapToDto(DevelopmentTask task, RepositoryWorkspace workspace)
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
