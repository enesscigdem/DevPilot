using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.RejectExecutionReview;
using DevPilot.Application.Executions.Queries.GetExecutionActivity;
using DevPilot.Application.Executions.Queries.GetExecutionById;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Application.Executions.Queries.GetExecutions;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

public sealed record RejectExecutionReviewRequest(string? Reason);

[ApiController]
[Route("api/executions")]
[Produces("application/json")]
public class ExecutionsController : ControllerBase
{
    private readonly IGetExecutionsQueryHandler _getExecutionsHandler;
    private readonly IGetExecutionByIdQueryHandler _getExecutionByIdHandler;
    private readonly IGetExecutionReviewQueryHandler _getExecutionReviewHandler;
    private readonly IGetExecutionActivityQueryHandler _getExecutionActivityHandler;
    private readonly IApproveExecutionReviewCommandHandler _approveReviewHandler;
    private readonly IRejectExecutionReviewCommandHandler _rejectReviewHandler;

    public ExecutionsController(
        IGetExecutionsQueryHandler getExecutionsHandler,
        IGetExecutionByIdQueryHandler getExecutionByIdHandler,
        IGetExecutionReviewQueryHandler getExecutionReviewHandler,
        IGetExecutionActivityQueryHandler getExecutionActivityHandler,
        IApproveExecutionReviewCommandHandler approveReviewHandler,
        IRejectExecutionReviewCommandHandler rejectReviewHandler)
    {
        _getExecutionsHandler = getExecutionsHandler;
        _getExecutionByIdHandler = getExecutionByIdHandler;
        _getExecutionReviewHandler = getExecutionReviewHandler;
        _getExecutionActivityHandler = getExecutionActivityHandler;
        _approveReviewHandler = approveReviewHandler;
        _rejectReviewHandler = rejectReviewHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetExecutions(CancellationToken cancellationToken)
    {
        var result = await _getExecutionsHandler
            .HandleAsync(new GetExecutionsQuery(), cancellationToken)
            .ConfigureAwait(false);

        return Ok(result.Executions);
    }

    [HttpGet("{id:guid}", Name = nameof(GetExecutionById))]
    public async Task<IActionResult> GetExecutionById(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _getExecutionByIdHandler
            .HandleAsync(new GetExecutionByIdQuery(id), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Found || result.Execution is null)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Execution not found." });
        }

        return Ok(result.Execution);
    }

    [HttpGet("{id:guid}/activity", Name = nameof(GetExecutionActivity))]
    public async Task<IActionResult> GetExecutionActivity(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _getExecutionActivityHandler
            .HandleAsync(new GetExecutionActivityQuery(id), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Found)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Execution not found." });
        }

        return Ok(result.Activities);
    }

    [HttpGet("{id:guid}/review", Name = nameof(GetExecutionReview))]
    public async Task<IActionResult> GetExecutionReview(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _getExecutionReviewHandler
            .HandleAsync(new GetExecutionReviewQuery(id), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            ExecutionReviewResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            ExecutionReviewResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be reviewed." }),
            ExecutionReviewResultStatus.Success => Ok(result.Review),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/review/approve", Name = nameof(ApproveExecutionReview))]
    public async Task<IActionResult> ApproveExecutionReview(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _approveReviewHandler
            .HandleAsync(new ApproveExecutionReviewCommand(id), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            ApproveExecutionReviewResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            ApproveExecutionReviewResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be approved." }),
            ApproveExecutionReviewResultStatus.Success => Ok(result.Decision),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }

    [HttpPost("{id:guid}/review/reject", Name = nameof(RejectExecutionReview))]
    public async Task<IActionResult> RejectExecutionReview(
        [FromRoute] Guid id,
        [FromBody] RejectExecutionReviewRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _rejectReviewHandler
            .HandleAsync(new RejectExecutionReviewCommand(id, request?.Reason), cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            RejectExecutionReviewResultStatus.BadRequest => BadRequest(new { error = result.ErrorMessage ?? "Invalid rejection request." }),
            RejectExecutionReviewResultStatus.NotFound => NotFound(new { error = result.ErrorMessage ?? "Execution not found." }),
            RejectExecutionReviewResultStatus.Conflict => Conflict(new { error = result.ErrorMessage ?? "Execution cannot be rejected." }),
            RejectExecutionReviewResultStatus.Success => Ok(result.Decision),
            _ => StatusCode(500, new { error = "An unexpected error occurred." })
        };
    }
}
