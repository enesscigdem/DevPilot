using DevPilot.Application.Executions.Commands.RunDeveloperAgent;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/dev/executions")]
public class DevExecutionsController : ControllerBase
{
    private readonly IRunDeveloperAgentCommandHandler _runDeveloperAgentHandler;
    private readonly IHostEnvironment _environment;

    public DevExecutionsController(
        IRunDeveloperAgentCommandHandler runDeveloperAgentHandler,
        IHostEnvironment environment)
    {
        _runDeveloperAgentHandler = runDeveloperAgentHandler;
        _environment = environment;
    }

    [HttpPost("{id:guid}/developer-agent")]
    public async Task<IActionResult> RunDeveloperAgent(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var result = await _runDeveloperAgentHandler
            .HandleAsync(new RunDeveloperAgentCommand(id), cancellationToken)
            .ConfigureAwait(false);

        if (result.NotFound)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Execution or task not found." });
        }

        if (result.Conflict)
        {
            return Conflict(new { error = result.ErrorMessage ?? "Execution state conflict." });
        }

        if (!result.Success)
        {
            return StatusCode(500, new { error = result.ErrorMessage ?? "Developer Agent execution failed." });
        }

        return Ok(new DevDeveloperAgentResponseDto
        {
            Success = true,
            ModifiedFiles = result.ModifiedFiles ?? Array.Empty<string>()
        });
    }

    public sealed class DevDeveloperAgentResponseDto
    {
        public bool Success { get; set; }

        public string? ErrorMessage { get; set; }

        public IReadOnlyList<string> ModifiedFiles { get; set; } = Array.Empty<string>();

        public string ResponseVersion { get; set; } = "1";
    }
}
