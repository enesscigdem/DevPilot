using DevPilot.Application.GitProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.GitProviders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/github")]
public sealed class GitHubAuthController : ControllerBase
{
    private readonly IGitHubOAuthStateService _stateService;
    private readonly IGitHubAppTokenService _tokenService;
    private readonly IGitProvider _gitProvider;
    private readonly IOptions<GitHubAppOptions> _options;
    private readonly IOptions<FrontendOptions> _frontendOptions;
    private readonly DevPilotDbContext _dbContext;
    private readonly ILogger<GitHubAuthController> _logger;

    public GitHubAuthController(
        IGitHubOAuthStateService stateService,
        IGitHubAppTokenService tokenService,
        IGitProvider gitProvider,
        IOptions<GitHubAppOptions> options,
        IOptions<FrontendOptions> frontendOptions,
        DevPilotDbContext dbContext,
        ILogger<GitHubAuthController> logger)
    {
        _stateService = stateService;
        _tokenService = tokenService;
        _gitProvider = gitProvider;
        _options = options;
        _frontendOptions = frontendOptions;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("status")]
    public async Task<ActionResult<GitHubConnectionStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var isConfigured = _options.Value.IsAppConfigured;

        var connections = await _dbContext.GitHubInstallationConnections
            .Where(c => c.Status == GitHubInstallationStatus.Active)
            .OrderByDescending(c => c.ConnectedAt)
            .ToListAsync(cancellationToken);

        var dtos = connections.Select(c => new GitHubInstallationSummaryDto(
            Id: c.Id,
            ExternalInstallationId: c.ExternalInstallationId,
            AccountLogin: c.AccountLogin,
            AccountType: c.AccountType,
            TargetAvatarUrl: c.TargetAvatarUrl,
            Status: c.Status.ToString(),
            ConnectedAt: c.ConnectedAt,
            ManageUrl: $"https://github.com/settings/installations/{c.ExternalInstallationId}"
        )).ToList();

        var isConnected = dtos.Count > 0;

        return Ok(new GitHubConnectionStatusDto(isConfigured, isConnected, dtos));
    }

    [HttpGet("connect-url")]
    public ActionResult<GitHubConnectUrlResponseDto> GetConnectUrl([FromQuery] string? returnUrl = null)
    {
        var state = _stateService.GenerateState(returnUrl);
        var appSlug = !string.IsNullOrWhiteSpace(_options.Value.AppSlug)
            ? _options.Value.AppSlug.Trim()
            : "devpilot-app";

        var url = $"https://github.com/apps/{appSlug}/installations/new?state={Uri.EscapeDataString(state)}";
        return Ok(new GitHubConnectUrlResponseDto(url));
    }

    [HttpGet("callback")]
    public async Task<IActionResult> HandleCallback(
        [FromQuery] string? code,
        [FromQuery(Name = "installation_id")] long? installationId,
        [FromQuery] string? state,
        [FromQuery(Name = "setup_action")] string? setupAction,
        CancellationToken cancellationToken)
    {
        var (isValid, safeReturnUrl) = _stateService.ValidateAndConsumeState(state);
        if (!isValid)
        {
            _logger.LogWarning("GitHub callback rejected: Invalid or expired state parameter.");
            return Redirect(BuildFrontendRedirectUrl("/", "github_error=invalid_state"));
        }

        if (string.IsNullOrWhiteSpace(code) || !installationId.HasValue || installationId.Value <= 0)
        {
            _logger.LogWarning("GitHub callback missing code or installation_id.");
            return Redirect(BuildFrontendRedirectUrl(safeReturnUrl, "github_error=missing_parameters"));
        }

        // Verify user ownership of the installation
        var verification = await _tokenService.VerifyUserInstallationOwnershipAsync(code, installationId.Value, cancellationToken);
        if (!verification.IsSuccess || verification.Installation == null)
        {
            _logger.LogWarning("GitHub installation ownership verification failed: {Error}", verification.ErrorMessage);
            var errorParam = $"github_error={Uri.EscapeDataString(verification.ErrorMessage ?? "ownership_verification_failed")}";
            return Redirect(BuildFrontendRedirectUrl(safeReturnUrl, errorParam));
        }

        var instInfo = verification.Installation;

        // Upsert installation connection
        var existing = await _dbContext.GitHubInstallationConnections
            .FirstOrDefaultAsync(c => c.ExternalInstallationId == instInfo.ExternalInstallationId, cancellationToken);

        var now = DateTime.UtcNow;
        if (existing != null)
        {
            existing.AccountLogin = instInfo.AccountLogin;
            existing.AccountType = instInfo.AccountType;
            existing.TargetId = instInfo.TargetId;
            existing.TargetAvatarUrl = instInfo.TargetAvatarUrl;
            existing.Status = GitHubInstallationStatus.Active;
            existing.UpdatedAt = now;
            existing.LastVerifiedAt = now;
        }
        else
        {
            var newConnection = new GitHubInstallationConnection
            {
                Id = Guid.NewGuid(),
                ExternalInstallationId = instInfo.ExternalInstallationId,
                AccountLogin = instInfo.AccountLogin,
                AccountType = instInfo.AccountType,
                TargetId = instInfo.TargetId,
                TargetAvatarUrl = instInfo.TargetAvatarUrl,
                Status = GitHubInstallationStatus.Active,
                ConnectedAt = now,
                UpdatedAt = now,
                LastVerifiedAt = now
            };
            _dbContext.GitHubInstallationConnections.Add(newConnection);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _tokenService.InvalidateToken(instInfo.ExternalInstallationId);

        return Redirect(BuildFrontendRedirectUrl(safeReturnUrl, "github_connected=true"));
    }

    private string BuildFrontendRedirectUrl(string? returnPath, string queryParam)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(_frontendOptions.Value.BaseUrl)
            ? _frontendOptions.Value.BaseUrl.Trim().TrimEnd('/')
            : "http://localhost:3000";

        var sanitizedPath = GitHubOAuthStateService.SanitizeReturnUrl(returnPath);
        if (!sanitizedPath.StartsWith('/'))
        {
            sanitizedPath = "/" + sanitizedPath;
        }

        var sep = sanitizedPath.Contains('?') ? "&" : "?";
        return $"{baseUrl}{sanitizedPath}{sep}{queryParam}";
    }

    [HttpDelete("installations/{id:guid}")]
    public async Task<IActionResult> DisconnectInstallation(Guid id, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.GitHubInstallationConnections
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (connection == null)
        {
            return NotFound();
        }

        connection.Status = GitHubInstallationStatus.Revoked;
        connection.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _tokenService.InvalidateToken(connection.ExternalInstallationId);

        return NoContent();
    }

    [HttpGet("repositories")]
    public async Task<ActionResult<IReadOnlyList<GitHubDiscoveredRepositoryDto>>> GetRepositories(CancellationToken cancellationToken)
    {
        var repoResult = await _gitProvider.GetRepositoriesAsync(cancellationToken);
        if (!repoResult.IsSuccess)
        {
            return StatusCode(502, new { error = repoResult.ErrorMessage });
        }

        var workspaces = await _dbContext.RepositoryWorkspaces
            .ToListAsync(cancellationToken);

        var activeConnections = await _dbContext.GitHubInstallationConnections
            .Where(c => c.Status == GitHubInstallationStatus.Active)
            .ToListAsync(cancellationToken);

        var connectionByLogin = activeConnections
            .GroupBy(c => c.AccountLogin, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ExternalInstallationId, StringComparer.OrdinalIgnoreCase);

        var dtos = new List<GitHubDiscoveredRepositoryDto>();
        long autoId = 1;

        foreach (var repo in repoResult.Data ?? Array.Empty<GitRepository>())
        {
            var matchingWorkspace = workspaces.FirstOrDefault(w =>
                string.Equals(w.Owner, repo.Owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(w.Repository, repo.Name, StringComparison.OrdinalIgnoreCase));

            var extId = connectionByLogin.TryGetValue(repo.Owner, out var id) ? id : 0;

            dtos.Add(new GitHubDiscoveredRepositoryDto(
                Id: autoId++,
                FullName: repo.FullName,
                Name: repo.Name,
                Owner: repo.Owner,
                IsPrivate: repo.IsPrivate,
                DefaultBranch: string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch,
                Url: repo.Url ?? $"https://github.com/{repo.FullName}",
                Description: repo.Description,
                ExternalInstallationId: extId,
                IsConnectedToDevPilot: matchingWorkspace != null,
                DevPilotWorkspaceId: matchingWorkspace?.Id
            ));
        }

        return Ok(dtos);
    }

    [HttpGet("repositories/{owner}/{repo}/branches")]
    public async Task<ActionResult<IReadOnlyList<GitBranch>>> GetBranches(
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        var branchesResult = await _gitProvider.GetBranchesAsync(owner, repo, cancellationToken);
        if (!branchesResult.IsSuccess)
        {
            return StatusCode(502, new { error = branchesResult.ErrorMessage });
        }

        return Ok(branchesResult.Data);
    }
}
