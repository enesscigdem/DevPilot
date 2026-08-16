using DevPilot.Application.Executions.Queries.GetExecutionById;
using DevPilot.Application.Executions.Queries.GetExecutions;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/executions")]
[Produces("application/json")]
public class ExecutionsController : ControllerBase
{
    private readonly IGetExecutionsQueryHandler _getExecutionsHandler;
    private readonly IGetExecutionByIdQueryHandler _getExecutionByIdHandler;

    public ExecutionsController(
        IGetExecutionsQueryHandler getExecutionsHandler,
        IGetExecutionByIdQueryHandler getExecutionByIdHandler)
    {
        _getExecutionsHandler = getExecutionsHandler;
        _getExecutionByIdHandler = getExecutionByIdHandler;
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
}
