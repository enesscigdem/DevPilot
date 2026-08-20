using DevPilot.Application.Executions.Commands.StartExecution;
using DevPilot.Application.Executions.Commands.RetryExecution;
using DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;
using DevPilot.Application.TaskImpactAnalysis.Queries.GetTaskImpactAnalysis;
using DevPilot.Application.Tasks.Commands.ApproveTask;
using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Commands.DeleteTask;
using DevPilot.Application.Tasks.Commands.RejectTask;
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
    private readonly IAnalyzeTaskImpactCommandHandler _analyzeImpactHandler;
    private readonly IGetTaskImpactAnalysisQueryHandler _getImpactAnalysisHandler;
    private readonly IApproveTaskCommandHandler _approveHandler;
    private readonly IRejectTaskCommandHandler _rejectHandler;
    private readonly IStartExecutionCommandHandler _startExecutionHandler;
    private readonly IRetryExecutionCommandHandler _retryExecutionHandler;

    public TasksController(
        ICreateTaskCommandHandler createHandler,
        IUpdateTaskCommandHandler updateHandler,
        IUpdateTaskStatusCommandHandler updateStatusHandler,
        IDeleteTaskCommandHandler deleteHandler,
        IGetTaskByIdQueryHandler getByIdHandler,
        IGetTasksQueryHandler getTasksHandler,
        IAnalyzeTaskImpactCommandHandler analyzeImpactHandler,
        IGetTaskImpactAnalysisQueryHandler getImpactAnalysisHandler,
        IApproveTaskCommandHandler approveHandler,
        IRejectTaskCommandHandler rejectHandler,
        IStartExecutionCommandHandler startExecutionHandler,
        IRetryExecutionCommandHandler retryExecutionHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _updateStatusHandler = updateStatusHandler;
        _deleteHandler = deleteHandler;
        _getByIdHandler = getByIdHandler;
        _getTasksHandler = getTasksHandler;
        _analyzeImpactHandler = analyzeImpactHandler;
        _getImpactAnalysisHandler = getImpactAnalysisHandler;
        _approveHandler = approveHandler;
        _rejectHandler = rejectHandler;
        _startExecutionHandler = startExecutionHandler;
        _retryExecutionHandler = retryExecutionHandler;
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

    [HttpPost("{id:guid}/impact-analysis")]
    public async Task<IActionResult> AnalyzeTaskImpact(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _analyzeImpactHandler
            .HandleAsync(new AnalyzeTaskImpactCommand(id), cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
        {
            return CreatedAtAction(
                nameof(GetTaskImpactAnalysis),
                new { id },
                result.Analysis);
        }

        if (result.NotFound)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Task not found." });
        }

        if (result.Conflict)
        {
            return Conflict(new { error = result.ErrorMessage });
        }

        if (result.AnalysisId is null)
        {
            return BadRequest(new { error = result.ErrorMessage });
        }

        return StatusCode(
            StatusCodes.Status502BadGateway,
            new { error = result.ErrorMessage, analysisId = result.AnalysisId });
    }

    [HttpGet("{id:guid}/impact-analysis")]
    public async Task<IActionResult> GetTaskImpactAnalysis(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _getImpactAnalysisHandler
            .HandleAsync(new GetTaskImpactAnalysisQuery(id), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Found || result.Analysis is null)
        {
            return NotFound(new { error = result.ErrorMessage ?? "No impact analysis found for this task." });
        }

        return Ok(result.Analysis);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> ApproveTask(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _approveHandler
            .HandleAsync(new ApproveTaskCommand(id), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.NotFound)
            {
                return NotFound(new { error = result.ErrorMessage ?? "Task not found." });
            }

            if (result.Conflict)
            {
                return Conflict(new { error = result.ErrorMessage });
            }

            return BadRequest(new { error = result.ErrorMessage });
        }

        return Ok(result.Task);
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> RejectTask(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _rejectHandler
            .HandleAsync(new RejectTaskCommand(id), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.NotFound)
            {
                return NotFound(new { error = result.ErrorMessage ?? "Task not found." });
            }

            if (result.Conflict)
            {
                return Conflict(new { error = result.ErrorMessage });
            }

            return BadRequest(new { error = result.ErrorMessage });
        }

        return Ok(result.Task);
    }

    [HttpPost("{id:guid}/executions")]
    public async Task<IActionResult> StartExecution(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _startExecutionHandler
            .HandleAsync(new StartExecutionCommand(id), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.NotFound)
            {
                return NotFound(new { error = result.ErrorMessage ?? "Task not found." });
            }

            if (result.Conflict)
            {
                return Conflict(new { error = result.ErrorMessage });
            }

            return BadRequest(new { error = result.ErrorMessage });
        }

        return CreatedAtRoute(
            nameof(ExecutionsController.GetExecutionById),
            new { id = result.Execution!.Id },
            result.Execution);
    }

    [HttpPost("{id:guid}/executions/retry")]
    public async Task<IActionResult> RetryExecution(
        [FromRoute] Guid id,
        [FromQuery] Guid? repositoryWorkspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _retryExecutionHandler
            .HandleAsync(new RetryExecutionCommand(id, repositoryWorkspaceId), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.NotFound)
            {
                return NotFound(new { error = result.ErrorMessage ?? "Task not found." });
            }

            if (result.Conflict)
            {
                return Conflict(new { error = result.ErrorMessage });
            }

            return BadRequest(new { error = result.ErrorMessage });
        }

        return CreatedAtRoute(
            nameof(ExecutionsController.GetExecutionById),
            new { id = result.Execution!.Id },
            result.Execution);
    }
}
