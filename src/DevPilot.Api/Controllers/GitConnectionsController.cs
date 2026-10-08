using DevPilot.Application.GitProviders;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.GitProviders;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

/// <summary>Access tokens for GitLab and generic git hosts. GitHub keeps its own App based connection.</summary>
[ApiController]
[Route("api/git-connections")]
[Produces("application/json")]
public sealed class GitConnectionsController : ControllerBase
{
    private readonly IGitConnectionStore _store;
    private readonly IGitTokenValidator _validator;

    public GitConnectionsController(IGitConnectionStore store, IGitTokenValidator validator)
    {
        _store = store;
        _validator = validator;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GitConnectionResponse>>> List(CancellationToken cancellationToken)
    {
        var connections = await _store.ListAsync(cancellationToken);
        return Ok(connections.Select(GitConnectionResponse.From).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<GitConnectionResponse>> Create(
        [FromBody] CreateGitConnectionRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<GitProviderKind>(request.Provider, ignoreCase: true, out var provider) ||
            provider == GitProviderKind.GitHub)
        {
            return BadRequest(new { error = "Provider must be GitLab, AzureDevOps, Bitbucket or Generic. GitHub uses the GitHub App." });
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { error = "Token is required." });
        }

        var host = NormalizeHost(request.Host);
        if (host is null)
        {
            return BadRequest(new { error = "Host must be a plain host name such as gitlab.com or git.company.local." });
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? host : request.DisplayName.Trim();
        if (displayName.Length > 200)
        {
            return BadRequest(new { error = "Display name must be at most 200 characters." });
        }

        var validation = await _validator.ValidateAsync(provider, host, request.Token.Trim(), cancellationToken);
        if (!validation.IsValid)
        {
            return BadRequest(new { error = validation.ErrorMessage });
        }

        var created = await _store.CreateAsync(provider, host, displayName, request.Username, request.Token, cancellationToken);
        return Created($"api/git-connections/{created.Id}", GitConnectionResponse.From(created));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        return await _store.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
    }

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var trimmed = host.Trim();
        if (trimmed.Contains("://"))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return null;
            }

            trimmed = uri.Authority;
        }

        trimmed = trimmed.TrimEnd('/').ToLowerInvariant();
        return Uri.CheckHostName(trimmed.Split(':')[0]) == UriHostNameType.Unknown ? null : trimmed;
    }
}

public sealed class CreateGitConnectionRequest
{
    public string Provider { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? Username { get; set; }

    public string Token { get; set; } = string.Empty;
}

public sealed record GitConnectionResponse(
    Guid Id,
    string Provider,
    string Host,
    string DisplayName,
    string? Username,
    DateTime CreatedAt)
{
    public static GitConnectionResponse From(GitConnectionDto dto) =>
        new(dto.Id, dto.Provider.ToString(), dto.Host, dto.DisplayName, dto.Username, dto.CreatedAt);
}
