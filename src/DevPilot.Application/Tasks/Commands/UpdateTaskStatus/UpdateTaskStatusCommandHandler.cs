using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Tasks.Commands.UpdateTaskStatus;

public interface IUpdateTaskStatusCommandHandler
{
    Task<UpdateTaskStatusResult> HandleAsync(
        UpdateTaskStatusCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class UpdateTaskStatusCommandHandler : IUpdateTaskStatusCommandHandler
{
    private readonly Ports.ITaskRepository _taskRepository;
    private readonly ILogger<UpdateTaskStatusCommandHandler> _logger;

    public UpdateTaskStatusCommandHandler(
        Ports.ITaskRepository taskRepository,
        ILogger<UpdateTaskStatusCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _logger = logger;
    }

    public async Task<UpdateTaskStatusResult> HandleAsync(
        UpdateTaskStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(typeof(DevelopmentTaskStatus), command.Status))
        {
            return new UpdateTaskStatusResult
            {
                Success = false,
                ErrorMessage = "Invalid status value.",
            };
        }

        var task = await _taskRepository
            .GetByIdAsync(command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (task is null)
        {
            return new UpdateTaskStatusResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = "Task not found.",
            };
        }

        task.Status = command.Status;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Updated development task {TaskId} status to {Status}.",
            task.Id,
            task.Status);

        return new UpdateTaskStatusResult
        {
            Success = true,
        };
    }
}
