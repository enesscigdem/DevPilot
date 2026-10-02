using DevPilot.Application.ModelComparisons;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

/// <summary>Runs one approved task with several models, one after another, to compare the results.</summary>
[ApiController]
[Route("api/model-comparisons")]
[Produces("application/json")]
public class ModelComparisonsController : ControllerBase
{
    private readonly IModelComparisonService _service;

    public ModelComparisonsController(IModelComparisonService service)
    {
        _service = service;
    }

    [HttpPost]
    public Task<IActionResult> Start([FromBody] StartModelComparisonRequest request, CancellationToken cancellationToken) =>
        Run(async () =>
        {
            var comparison = await _service.StartAsync(request, cancellationToken);
            return Created($"/api/model-comparisons/{comparison.Id}", comparison);
        });

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        Run(async () => Ok(await _service.GetAsync(id, cancellationToken)));

    [HttpGet]
    public Task<IActionResult> ListForTask([FromQuery] Guid taskId, CancellationToken cancellationToken) =>
        Run(async () => Ok(await _service.ListForTaskAsync(taskId, cancellationToken)));

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        Run(async () => Ok(await _service.CancelAsync(id, cancellationToken)));

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
