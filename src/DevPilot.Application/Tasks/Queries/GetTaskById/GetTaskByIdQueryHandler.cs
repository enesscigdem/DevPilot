using DevPilot.Application.Tasks.Dtos;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Tasks.Queries.GetTaskById;

public interface IGetTaskByIdQueryHandler
{
    Task<GetTaskByIdResult> HandleAsync(
        GetTaskByIdQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GetTaskByIdQueryHandler : IGetTaskByIdQueryHandler
{
    private readonly Ports.ITaskRepository _taskRepository;
    private readonly ILogger<GetTaskByIdQueryHandler> _logger;

    public GetTaskByIdQueryHandler(
        Ports.ITaskRepository taskRepository,
        ILogger<GetTaskByIdQueryHandler> logger)
    {
        _taskRepository = taskRepository;
        _logger = logger;
    }

    public async Task<GetTaskByIdResult> HandleAsync(
        GetTaskByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository
            .GetByIdAsync(query.Id, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return new GetTaskByIdResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = "Task not found.",
            };
        }

        return new GetTaskByIdResult
        {
            Success = true,
            Task = MapToDto(task),
        };
    }

    private static TaskDto MapToDto(Domain.Entities.DevelopmentTask task)
    {
        var workspace = task.RepositoryWorkspace;

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
