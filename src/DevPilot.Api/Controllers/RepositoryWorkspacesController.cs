using DevPilot.Application.RepositoryWorkspaces.Commands.CreateRepositoryWorkspace;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceAnalysis;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceArchitecture;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceInsights;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceOverview;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/repositoryworkspaces")]
[Produces("application/json")]
public class RepositoryWorkspacesController : ControllerBase
{
    private readonly DevPilotDbContext _dbContext;
    private readonly ICreateRepositoryWorkspaceCommandHandler _createWorkspaceHandler;
    private readonly IGetRepositoryWorkspaceAnalysisQueryHandler _analysisQueryHandler;
    private readonly IGetRepositoryWorkspaceArchitectureQueryHandler _architectureQueryHandler;
    private readonly IGetWorkspaceOverviewQueryHandler _overviewQueryHandler;
    private readonly IGetWorkspaceInsightsQueryHandler _insightsQueryHandler;

    public RepositoryWorkspacesController(
        DevPilotDbContext dbContext,
        ICreateRepositoryWorkspaceCommandHandler createWorkspaceHandler,
        IGetRepositoryWorkspaceAnalysisQueryHandler analysisQueryHandler,
        IGetRepositoryWorkspaceArchitectureQueryHandler architectureQueryHandler,
        IGetWorkspaceOverviewQueryHandler overviewQueryHandler,
        IGetWorkspaceInsightsQueryHandler insightsQueryHandler)
    {
        _insightsQueryHandler = insightsQueryHandler;
        _dbContext = dbContext;
        _createWorkspaceHandler = createWorkspaceHandler;
        _analysisQueryHandler = analysisQueryHandler;
        _architectureQueryHandler = architectureQueryHandler;
        _overviewQueryHandler = overviewQueryHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var workspaces = await _dbContext.RepositoryWorkspaces
            .OrderByDescending(w => w.UpdatedAt)
            .Select(w => new RepositoryWorkspaceListDto
            {
                Id = w.Id,
                Provider = w.Provider.ToString(),
                Host = w.Host,
                Owner = w.Owner,
                Repository = w.Repository,
                Branch = w.Branch,
                Status = w.Status,
                DisplayName = $"{w.Owner}/{w.Repository} ({w.Branch})",
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(workspaces);
    }

    [HttpGet("{id:guid}", Name = nameof(GetById))]
    public async Task<IActionResult> GetById(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var workspace = await _dbContext.RepositoryWorkspaces
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (workspace is null)
        {
            return NotFound(new { error = "Repository workspace not found." });
        }

        return Ok(new RepositoryWorkspaceDto
        {
            Id = workspace.Id,
            Provider = workspace.Provider.ToString(),
            Host = workspace.Host,
            Owner = workspace.Owner,
            Repository = workspace.Repository,
            Branch = workspace.Branch,
            Status = workspace.Status,
            CommitSha = workspace.CommitSha,
            CreatedAt = workspace.CreatedAt,
            UpdatedAt = workspace.UpdatedAt,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateRepositoryWorkspaceDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _createWorkspaceHandler
            .HandleAsync(new CreateRepositoryWorkspaceCommand(dto), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.IsConflict)
            {
                return Conflict(new { error = result.ErrorMessage });
            }

            if (result.IsValidationError)
            {
                return BadRequest(new { error = result.ErrorMessage });
            }

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = result.ErrorMessage ?? "Failed to create repository workspace." });
        }

        return CreatedAtAction(
            nameof(GetById),
            new { id = result.Workspace!.Id },
            result.Workspace);
    }

    [HttpGet("{id:guid}/analysis")]
    public async Task<IActionResult> GetAnalysis(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _analysisQueryHandler
            .HandleAsync(new GetRepositoryWorkspaceAnalysisQuery(id), cancellationToken)
            .ConfigureAwait(false);

        if (result.NotFound)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Repository workspace not found." });
        }

        if (result.IsConflict)
        {
            return Conflict(new { error = result.ErrorMessage });
        }

        if (!result.Success)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = result.ErrorMessage ?? "Failed to analyze repository workspace." });
        }

        return Ok(result.Analysis);
    }

    [HttpGet("{id:guid}/architecture")]
    public async Task<IActionResult> GetArchitecture(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _architectureQueryHandler
            .HandleAsync(new GetRepositoryWorkspaceArchitectureQuery(id), cancellationToken)
            .ConfigureAwait(false);

        if (result.NotFound)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Repository workspace not found." });
        }

        if (result.IsConflict)
        {
            return Conflict(new { error = result.ErrorMessage });
        }

        if (!result.Success)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = result.ErrorMessage ?? "Failed to analyze repository workspace architecture." });
        }

        return Ok(result.Architecture);
    }

    [HttpGet("{id:guid}/overview")]
    public async Task<IActionResult> GetOverview(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _overviewQueryHandler
            .HandleAsync(new GetWorkspaceOverviewQuery(id), cancellationToken)
            .ConfigureAwait(false);

        if (result.NotFound)
        {
            return NotFound(new { error = result.ErrorMessage ?? "Repository workspace not found." });
        }

        if (result.IsConflict)
        {
            return Conflict(new { error = result.ErrorMessage });
        }

        if (!result.Success)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = result.ErrorMessage ?? "Failed to retrieve repository workspace overview." });
        }

        return Ok(result.Overview);
    }

    [HttpGet("{id:guid}/insights")]
    public async Task<IActionResult> GetInsights(
        [FromRoute] Guid id,
        [FromQuery] int? window,
        CancellationToken cancellationToken)
    {
        var result = await _insightsQueryHandler
            .HandleAsync(new GetWorkspaceInsightsQuery(id, window ?? 100), cancellationToken)
            .ConfigureAwait(false);

        if (result.NotFound)
        {
            return NotFound(new { error = result.ErrorMessage });
        }

        if (!result.Success)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = result.ErrorMessage ?? "Failed to retrieve repository insights." });
        }

        return Ok(result.Insights);
    }

    public sealed class RepositoryWorkspaceListDto
    {
        public Guid Id { get; set; }

        public string Provider { get; set; } = "GitHub";

        public string Host { get; set; } = "github.com";

        public string Owner { get; set; } = string.Empty;

        public string Repository { get; set; } = string.Empty;

        public string Branch { get; set; } = string.Empty;

        public RepositoryWorkspaceStatus Status { get; set; }

        public string DisplayName { get; set; } = string.Empty;
    }
}
