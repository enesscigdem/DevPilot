using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/repositoryworkspaces")]
[Produces("application/json")]
public class RepositoryWorkspacesController : ControllerBase
{
    private readonly DevPilotDbContext _dbContext;

    public RepositoryWorkspacesController(DevPilotDbContext dbContext)
    {
        _dbContext = dbContext;
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
