using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.ProjectBrain.Commands.AskBrain;
using DevPilot.Application.ProjectBrain.Commands.IndexWorkspace;
using DevPilot.Application.ProjectBrain.Queries.GetBrainStatus;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/repositoryworkspaces/{workspaceId:guid}/brain")]
public sealed class RepositoryBrainController : ControllerBase
{
    private readonly IGetBrainStatusQueryHandler _statusHandler;
    private readonly IIndexWorkspaceCommandHandler _indexHandler;
    private readonly IAskBrainCommandHandler _askHandler;
    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly IRepositoryAnalyzer _repositoryAnalyzer;
    private readonly ILogger<RepositoryBrainController> _logger;

    public RepositoryBrainController(
        IGetBrainStatusQueryHandler statusHandler,
        IIndexWorkspaceCommandHandler indexHandler,
        IAskBrainCommandHandler askHandler,
        IRepositoryWorkspaceQuery workspaceQuery,
        IRepositoryAnalyzer repositoryAnalyzer,
        ILogger<RepositoryBrainController> logger)
    {
        _statusHandler = statusHandler;
        _indexHandler = indexHandler;
        _askHandler = askHandler;
        _workspaceQuery = workspaceQuery;
        _repositoryAnalyzer = repositoryAnalyzer;
        _logger = logger;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        [FromRoute] Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _statusHandler
            .HandleAsync(new GetBrainStatusQuery(workspaceId), cancellationToken)
            .ConfigureAwait(false);

        if (result.NotFound)
        {
            return NotFound(new { error = result.ErrorMessage });
        }

        if (!result.Success)
        {
            return BadRequest(new { error = result.ErrorMessage });
        }

        return Ok(result.Status);
    }

    [HttpPost("index")]
    public async Task<IActionResult> Index(
        [FromRoute] Guid workspaceId,
        [FromBody] IndexBrainRequestDto? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var workspace = await _workspaceQuery
                .GetByIdAsync(workspaceId, cancellationToken)
                .ConfigureAwait(false);

            if (workspace is null)
            {
                return NotFound(new { error = $"Repository workspace {workspaceId} was not found." });
            }

            if (workspace.Status != RepositoryWorkspaceStatus.Completed)
            {
                return BadRequest(new { error = $"Workspace is not ready for indexing (current status: {workspace.Status})." });
            }

            if (string.IsNullOrWhiteSpace(workspace.LocalPath) || !Directory.Exists(workspace.LocalPath))
            {
                return BadRequest(new { error = "Workspace local directory does not exist." });
            }

            // Run Roslyn analysis for symbol extraction
            RepositoryAnalysisResult? analysisResult = null;
            try
            {
                analysisResult = await _repositoryAnalyzer
                    .AnalyzeAsync(new RepositoryAnalysisRequest { WorkspacePath = workspace.LocalPath }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Roslyn analysis failed for workspace {WorkspaceId}; continuing with file chunking", workspaceId);
            }

            var command = new IndexWorkspaceCommand(
                WorkspacePath: workspace.LocalPath,
                WorkspaceName: $"{workspace.Owner}/{workspace.Repository}",
                AnalysisResult: analysisResult,
                GenerateEmbeddings: request?.GenerateEmbeddings ?? true,
                RepositoryWorkspaceId: workspace.Id,
                CommitSha: workspace.CommitSha);

            var result = await _indexHandler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during brain indexing for workspace {WorkspaceId}", workspaceId);
            return StatusCode(500, new { error = "An unexpected error occurred during repository indexing." });
        }
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat(
        [FromRoute] Guid workspaceId,
        [FromBody] BrainChatRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { error = "Question is required." });
        }

        var result = await _askHandler
            .HandleAsync(new AskBrainCommand(workspaceId, request.Question), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsUnindexed)
        {
            return Conflict(new
            {
                error = result.ErrorMessage,
                isUnindexed = true,
            });
        }

        if (!result.Success)
        {
            return BadRequest(new { error = result.ErrorMessage });
        }

        return Ok(result);
    }

    public sealed class IndexBrainRequestDto
    {
        public bool GenerateEmbeddings { get; set; } = true;
    }

    public sealed class BrainChatRequestDto
    {
        public string Question { get; set; } = string.Empty;
    }
}
