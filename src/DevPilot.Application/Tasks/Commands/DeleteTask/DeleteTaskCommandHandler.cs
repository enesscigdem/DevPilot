using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Tasks.Commands.DeleteTask;

public interface IDeleteTaskCommandHandler
{
    Task<DeleteTaskResult> HandleAsync(
        DeleteTaskCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class DeleteTaskCommandHandler : IDeleteTaskCommandHandler
{
    private readonly Ports.ITaskRepository _taskRepository;
    private readonly ILogger<DeleteTaskCommandHandler> _logger;

    public DeleteTaskCommandHandler(
        Ports.ITaskRepository taskRepository,
        ILogger<DeleteTaskCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _logger = logger;
    }

    public async Task<DeleteTaskResult> HandleAsync(
        DeleteTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository
            .GetByIdAsync(command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return new DeleteTaskResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = "Task not found.",
            };
        }

        await _taskRepository.DeleteAsync(task, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Deleted development task {TaskId}.",
            task.Id);

        return new DeleteTaskResult
        {
            Success = true,
        };
    }
}
