using DevPilot.Application.AiProviders;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

/// <summary>Lets the user register their own models and decide which pipeline stage uses which.</summary>
[ApiController]
[Route("api/ai-models")]
[Produces("application/json")]
public class AiModelsController : ControllerBase
{
    private readonly IAiModelService _service;

    public AiModelsController(IAiModelService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await _service.ListAsync(cancellationToken));

    [HttpPost]
    public Task<IActionResult> Create([FromBody] SaveAiModelRequest request, CancellationToken cancellationToken) =>
        Run(async () =>
        {
            var created = await _service.CreateAsync(request, cancellationToken);
            return Created($"/api/ai-models/{created.Id}", created);
        });

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] SaveAiModelRequest request, CancellationToken cancellationToken) =>
        Run(async () => Ok(await _service.UpdateAsync(id, request, cancellationToken)));

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        Run(async () =>
        {
            await _service.DeleteAsync(id, cancellationToken);
            return NoContent();
        });

    [HttpPost("{id:guid}/test")]
    public Task<IActionResult> Test(Guid id, CancellationToken cancellationToken) =>
        Run(async () => Ok(await _service.TestAsync(id, cancellationToken)));

    [HttpGet("stages")]
    public async Task<IActionResult> GetStages(CancellationToken cancellationToken) =>
        Ok(await _service.GetStageAssignmentsAsync(cancellationToken));

    [HttpPut("stages")]
    public Task<IActionResult> SetStages(
        [FromBody] List<AiStageAssignmentDto> assignments,
        CancellationToken cancellationToken) =>
        Run(async () => Ok(await _service.SetStageAssignmentsAsync(assignments, cancellationToken)));

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
    }
}
