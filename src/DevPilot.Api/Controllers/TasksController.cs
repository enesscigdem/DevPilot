using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Commands.DeleteTask;
using DevPilot.Application.Tasks.Commands.UpdateTask;
using DevPilot.Application.Tasks.Commands.UpdateTaskStatus;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Queries.GetTaskById;
using DevPilot.Application.Tasks.Queries.GetTasks;
using DevPilot.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/tasks")]
[Produces("application/json")]
public class TasksController : ControllerBase
{
    private readonly ICreateTaskCommandHandler _createHandler;
    private readonly IUpdateTaskCommandHandler _updateHandler;
    private readonly IUpdateTaskStatusCommandHandler _updateStatusHandler;
    private readonly IDeleteTaskCommandHandler _deleteHandler;
    private readonly IGetTaskByIdQueryHandler _getByIdHandler;
    private readonly IGetTasksQueryHandler _getTasksHandler;

    public TasksController(
        ICreateTaskCommandHandler createHandler,
        IUpdateTaskCommandHandler updateHandler,
        IUpdateTaskStatusCommandHandler updateStatusHandler,
        IDeleteTaskCommandHandler deleteHandler,
        IGetTaskByIdQueryHandler getByIdHandler,
        IGetTasksQueryHandler getTasksHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _updateStatusHandler = updateStatusHandler;
        _deleteHandler = deleteHandler;
        _getByIdHandler = getByIdHandler;
        _getTasksHandler = getTasksHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetTasks(
        [FromQuery] DevelopmentTaskStatus? status,
        [FromQuery] DevelopmentTaskPriority? priority,
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var filter = new TaskQueryFilterDto
        {
            Status = status,
            Priority = priority,
            RepositoryWorkspaceId = repositoryWorkspaceId,
        };

        var result = await _getTasksHandler
            .HandleAsync(new GetTasksQuery(filter), cancellationToken)
            .ConfigureAwait(false);

        return Ok(result.Tasks);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTaskById(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _getByIdHandler
            .HandleAsync(new GetTaskByIdQuery(id), cancellationToken)
            .ConfigureAwait(false);

        if (result.NotFound || result.Task is null)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Task not found." });
        }

        return Ok(result.Task);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTask(
        [FromBody] CreateTaskDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _createHandler
            .HandleAsync(new CreateTaskCommand(dto), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return BadRequest(new { error = result.ErrorMessage });
        }

        return CreatedAtAction(
            nameof(GetTaskById),
            new { id = result.Task!.Id },
            result.Task);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTask(
        [FromRoute] Guid id,
        [FromBody] UpdateTaskDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _updateHandler
            .HandleAsync(new UpdateTaskCommand(id, dto), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.Task is null)
            {
                return NotFound(new { error = result.ErrorMessage ?? "Task not found." });
            }

            return BadRequest(new { error = result.ErrorMessage });
        }

        return Ok(result.Task);
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> UpdateTaskStatus(
        [FromRoute] Guid id,
        [FromBody] UpdateTaskStatusDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _updateStatusHandler
            .HandleAsync(new UpdateTaskStatusCommand(id, dto.Status), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.NotFound)
            {
                return NotFound(new { error = result.ErrorMessage ?? "Task not found." });
            }

            return BadRequest(new { error = result.ErrorMessage });
        }

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTask(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _deleteHandler
            .HandleAsync(new DeleteTaskCommand(id), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.NotFound)
            {
                return NotFound(new { error = result.ErrorMessage ?? "Task not found." });
            }

            return BadRequest(new { error = result.ErrorMessage });
        }

        return NoContent();
    }
}
