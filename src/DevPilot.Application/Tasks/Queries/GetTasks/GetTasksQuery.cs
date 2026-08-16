using DevPilot.Application.Tasks.Dtos;

namespace DevPilot.Application.Tasks.Queries.GetTasks;

public sealed record GetTasksQuery(TaskQueryFilterDto Filter);

public sealed class GetTasksResult
{
    public IReadOnlyList<TaskListItemDto> Tasks { get; set; } = new List<TaskListItemDto>();
}
