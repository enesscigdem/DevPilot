using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Tasks.Batch;

public sealed class BatchTaskItem
{
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? AcceptanceCriteria { get; set; }

    public DevelopmentTaskPriority Priority { get; set; } = DevelopmentTaskPriority.Medium;
}

public sealed record CreateTaskBatchCommand(Guid RepositoryWorkspaceId, IReadOnlyList<BatchTaskItem> Tasks);

public sealed record CreateTaskBatchItemResult(int Index, bool Success, TaskDto? Task, string? ErrorMessage);

public sealed class CreateTaskBatchResult
{
    public bool Success { get; set; }

    /// <summary>Set when the whole request was rejected; per-task failures are in <see cref="Items"/>.</summary>
    public string? ErrorMessage { get; set; }

    public IReadOnlyList<CreateTaskBatchItemResult> Items { get; set; } = Array.Empty<CreateTaskBatchItemResult>();
}

public interface ICreateTaskBatchCommandHandler
{
    Task<CreateTaskBatchResult> HandleAsync(CreateTaskBatchCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates several tasks through the same handler a single task uses, so every rule applies unchanged. A task that
/// fails does not stop the others: the person gets one result per task and can fix just the failed ones.
/// </summary>
public sealed class CreateTaskBatchCommandHandler : ICreateTaskBatchCommandHandler
{
    private readonly ICreateTaskCommandHandler _createTask;

    public CreateTaskBatchCommandHandler(ICreateTaskCommandHandler createTask)
    {
        _createTask = createTask;
    }

    public async Task<CreateTaskBatchResult> HandleAsync(
        CreateTaskBatchCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.Tasks.Count == 0)
        {
            return new CreateTaskBatchResult { Success = false, ErrorMessage = "There are no tasks to create." };
        }

        if (command.Tasks.Count > TaskBatchParser.MaxTasks)
        {
            return new CreateTaskBatchResult
            {
                Success = false,
                ErrorMessage = $"A batch can hold at most {TaskBatchParser.MaxTasks} tasks."
            };
        }

        var items = new List<CreateTaskBatchItemResult>(command.Tasks.Count);
        for (var i = 0; i < command.Tasks.Count; i++)
        {
            var item = command.Tasks[i];
            var result = await _createTask
                .HandleAsync(
                    new CreateTaskCommand(new CreateTaskDto
                    {
                        RepositoryWorkspaceId = command.RepositoryWorkspaceId,
                        Title = item.Title,
                        Description = item.Description,
                        AcceptanceCriteria = item.AcceptanceCriteria,
                        Priority = item.Priority
                    }),
                    cancellationToken)
                .ConfigureAwait(false);

            items.Add(new CreateTaskBatchItemResult(i, result.Success, result.Task, result.ErrorMessage));

            // The workspace does not exist: every remaining task would fail the same way.
            if (!result.Success && result.ErrorMessage == "Repository workspace not found.")
            {
                return new CreateTaskBatchResult { Success = false, ErrorMessage = result.ErrorMessage, Items = items };
            }
        }

        return new CreateTaskBatchResult { Success = items.All(r => r.Success), Items = items };
    }
}
