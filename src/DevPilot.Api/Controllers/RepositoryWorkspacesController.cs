using DevPilot.Application.RepositoryWorkspaces.Commands.CreateRepositoryWorkspace;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
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

    public RepositoryWorkspacesController(
        DevPilotDbContext dbContext,
        ICreateRepositoryWorkspaceCommandHandler createWorkspaceHandler)
    {
        _dbContext = dbContext;
        _createWorkspaceHandler = createWorkspaceHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var workspaces = await _dbContext.RepositoryWorkspaces
            .OrderByDescending(w => w.UpdatedAt)
            .Select(w => new RepositoryWorkspaceListDto
            {
                Id = w.Id,
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

    public sealed class RepositoryWorkspaceListDto
    {
        public Guid Id { get; set; }

        public string Owner { get; set; } = string.Empty;

        public string Repository { get; set; } = string.Empty;

        public string Branch { get; set; } = string.Empty;

        public RepositoryWorkspaceStatus Status { get; set; }

        public string DisplayName { get; set; } = string.Empty;
    }
}
