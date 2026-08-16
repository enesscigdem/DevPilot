using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.ProjectBrain.Commands.IndexWorkspace;
using DevPilot.Infrastructure.RepositoryClone;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/dev/project-brain")]
public class DevProjectBrainController : ControllerBase
{
    private readonly IIndexWorkspaceCommandHandler _indexHandler;
    private readonly IOptions<RepositoryCloneOptions> _options;
    private readonly IHostEnvironment _environment;

    public DevProjectBrainController(
        IIndexWorkspaceCommandHandler indexHandler,
        IOptions<RepositoryCloneOptions> options,
        IHostEnvironment environment)
    {
        _indexHandler = indexHandler;
        _options = options;
        _environment = environment;
    }

    [HttpPost("index")]
    public async Task<IActionResult> Index(
        [FromBody] IndexWorkspaceRequestDto request,
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

        if (!Directory.Exists(requestedPath))
        {
            return BadRequest(new { error = $"Workspace path does not exist: {requestedPath}" });
        }

        var command = new IndexWorkspaceCommand(
            requestedPath,
            request.WorkspaceName,
            request.AnalysisResult,
            GenerateEmbeddings: true);

        var result = await _indexHandler.HandleAsync(command, cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new
            {
                error = result.ErrorMessage,
                jobId = result.JobId,
                duration = result.Duration,
            });
        }

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

    public sealed class IndexWorkspaceRequestDto
    {
        public string WorkspacePath { get; set; } = string.Empty;

        public string? WorkspaceName { get; set; }

        public RepositoryAnalysisResult? AnalysisResult { get; set; }
    }
}
