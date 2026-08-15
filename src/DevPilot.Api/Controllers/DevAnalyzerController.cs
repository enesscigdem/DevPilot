using DevPilot.Application.CodeAnalysis;
using DevPilot.Infrastructure.RepositoryClone;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/dev/analyzer")]
public class DevAnalyzerController : ControllerBase
{
    private readonly IRepositoryAnalyzer _analyzer;
    private readonly IOptions<RepositoryCloneOptions> _options;
    private readonly IHostEnvironment _environment;

    public DevAnalyzerController(
        IRepositoryAnalyzer analyzer,
        IOptions<RepositoryCloneOptions> options,
        IHostEnvironment environment)
    {
        _analyzer = analyzer;
        _options = options;
        _environment = environment;
    }

    [HttpPost("roslyn")]
    public async Task<IActionResult> Roslyn(
        [FromBody] AnalyzeRepositoryRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            return BadRequest(new { error = "WorkspacePath is required." });
        }

        var workspaceRoot = GetWorkspaceRoot();
        var requestedPath = Path.GetFullPath(request.WorkspacePath.Trim());

        if (!IsWithinWorkspaceRoot(requestedPath, workspaceRoot))
        {
            return BadRequest(new { error = "Requested path is outside the managed workspace root." });
        }

        var result = await _analyzer.AnalyzeAsync(
            new RepositoryAnalysisRequest { WorkspacePath = requestedPath },
            cancellationToken);

        return Ok(result);
    }

    private string GetWorkspaceRoot()
    {
        var configuredRoot = _options.Value?.WorkspaceRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "workspaces"));
    }

    private static bool IsWithinWorkspaceRoot(string targetPath, string workspaceRoot)
    {
        var normalizedTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedTarget.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            normalizedTarget.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class AnalyzeRepositoryRequestDto
    {
        public string WorkspacePath { get; set; } = string.Empty;
    }
}
