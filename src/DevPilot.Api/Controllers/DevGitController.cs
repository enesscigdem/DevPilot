using DevPilot.Application.GitProviders;
using DevPilot.Application.RepositoryClone;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/dev/git")]
public class DevGitController : ControllerBase
{
    private readonly IGitProvider _gitProvider;
    private readonly IRepositoryCloneService _cloneService;
    private readonly IHostEnvironment _environment;

    public DevGitController(
        IGitProvider gitProvider,
        IRepositoryCloneService cloneService,
        IHostEnvironment environment)
    {
        _gitProvider = gitProvider;
        _cloneService = cloneService;
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

    [HttpPost("clone")]
    public async Task<IActionResult> Clone(
        [FromBody] CloneRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var result = await _cloneService.CloneAsync(
            new CloneRequest
            {
                Owner = request.Owner,
                Repository = request.Repository,
                Branch = request.Branch,
            },
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(result);
    }

    public sealed class CloneRequestDto
    {
        public string Owner { get; set; } = string.Empty;

        public string Repository { get; set; } = string.Empty;

        public string Branch { get; set; } = string.Empty;
    }
}
