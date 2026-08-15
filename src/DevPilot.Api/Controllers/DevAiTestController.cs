using DevPilot.Application.AiProviders;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/dev/ai")]
public class DevAiTestController : ControllerBase
{
    private readonly IAiProvider _aiProvider;
    private readonly IHostEnvironment _environment;

    public DevAiTestController(IAiProvider aiProvider, IHostEnvironment environment)
    {
        _aiProvider = aiProvider;
        _environment = environment;
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] DevAiTestRequest request, CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return BadRequest(new { error = "Prompt is required." });
        }

        var response = await _aiProvider.SendAsync(
            new AiRequest { UserPrompt = request.Prompt },
            cancellationToken);

        if (!response.IsSuccess)
        {
            return BadRequest(response);
        }

        return Ok(response);
    }
}

public sealed class DevAiTestRequest
{
    public string Prompt { get; set; } = string.Empty;
}
