using DevPilot.Application.Tasks.Dtos;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Tasks.Queries.GetTasks;

public interface IGetTasksQueryHandler
{
    Task<GetTasksResult> HandleAsync(
        GetTasksQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GetTasksQueryHandler : IGetTasksQueryHandler
{
    private readonly Ports.ITaskRepository _taskRepository;

    public GetTasksQueryHandler(Ports.ITaskRepository taskRepository)
    {
        _taskRepository = taskRepository;
    }

    public async Task<GetTasksResult> HandleAsync(
        GetTasksQuery query,
        CancellationToken cancellationToken = default)
    {
        var filter = new Ports.DevelopmentTaskQueryFilter
        {
            Status = query.Filter.Status,
            Priority = query.Filter.Priority,
            RepositoryWorkspaceId = query.Filter.RepositoryWorkspaceId,
        };

        var tasks = await _taskRepository
            .GetAllAsync(filter, cancellationToken)
            .ConfigureAwait(false);

        return new GetTasksResult
        {
            Tasks = tasks.Select(MapToListItem).ToList(),
        };
    }

    private static TaskListItemDto MapToListItem(Domain.Entities.DevelopmentTask task)
    {
        return new TaskListItemDto
        {
            Id = task.Id,
            Title = task.Title,
            RepositoryName = $"{task.RepositoryWorkspace.Owner}/{task.RepositoryWorkspace.Repository}",
            Status = task.Status,
            Priority = task.Priority,
            UpdatedAt = task.UpdatedAt,
        };
    }
}
