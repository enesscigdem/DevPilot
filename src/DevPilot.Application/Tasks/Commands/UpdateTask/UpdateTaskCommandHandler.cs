using DevPilot.Domain.Enums;
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
                NotFound = true,
                ErrorMessage = "Task not found.",
            };
        }

        var title = dto.Title.Trim();
        var description = dto.Description.Trim();
        var criteria = dto.AcceptanceCriteria?.Trim();
        var contentChanged = title != task.Title || description != task.Description || criteria != task.AcceptanceCriteria;

        // The plan and the approval were made for the text that existed then. Text that is being analysed, run or already
        // delivered stays as it is; text that waits for approval or a run changes only by sending the task back for a new analysis.
        var approvalReset = false;
        if (contentChanged)
        {
            if (task.Status is DevelopmentTaskStatus.Analyzing or DevelopmentTaskStatus.Executing or DevelopmentTaskStatus.Completed)
            {
                return new UpdateTaskResult
                {
                    Success = false,
                    Conflict = true,
                    ErrorMessage = $"The text of a task in '{task.Status}' status cannot be changed.",
                };
            }

            if (task.Status is DevelopmentTaskStatus.AwaitingApproval or DevelopmentTaskStatus.Approved or DevelopmentTaskStatus.Failed)
            {
                task.Status = DevelopmentTaskStatus.ReadyForAnalysis;
                approvalReset = true;
            }
        }

        task.Title = title;
        task.Description = description;
        task.AcceptanceCriteria = criteria;
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
ApprovalReset = approvalReset,
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

        if (!Enum.IsDefined(typeof(DevelopmentTaskPriority), dto.Priority))
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
                ExternalSource = task.ExternalSource,
                ExternalKey = task.ExternalKey,
                ExternalUrl = task.ExternalUrl,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
        };
    }
}
