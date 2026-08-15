using DevPilot.Application.GitProviders;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/dev/git")]
public class DevGitController : ControllerBase
{
    private readonly IGitProvider _gitProvider;
    private readonly IHostEnvironment _environment;

    public DevGitController(IGitProvider gitProvider, IHostEnvironment environment)
    {
        _gitProvider = gitProvider;
        _environment = environment;
    }

    [HttpGet("repositories")]
    public async Task<IActionResult> GetRepositories(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var result = await _gitProvider.GetRepositoriesAsync(cancellationToken);

        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.ErrorMessage });
        }

        return Ok(result.Data);
    }

    [HttpGet("repositories/{owner}/{repo}/branches")]
    public async Task<IActionResult> GetBranches(
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var result = await _gitProvider.GetBranchesAsync(owner, repo, cancellationToken);

        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.ErrorMessage });
        }

        return Ok(result.Data);
    }
}
