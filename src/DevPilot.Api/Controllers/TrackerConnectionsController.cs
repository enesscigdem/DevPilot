using DevPilot.Application.Tasks.Dtos;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Application.Trackers;
using DevPilot.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

/// <summary>Jira connections, searching their issues and importing them as tasks.</summary>
[ApiController]
[Route("api/tracker-connections")]
[Produces("application/json")]
public sealed class TrackerConnectionsController : ControllerBase
{
    private readonly ITrackerConnectionStore _store;
    private readonly ITrackerClient _client;
    private readonly ITaskRepository _tasks;
    private readonly IImportTrackerIssuesCommandHandler _import;

    public TrackerConnectionsController(
        ITrackerConnectionStore store,
        ITrackerClient client,
        ITaskRepository tasks,
        IImportTrackerIssuesCommandHandler import)
    {
        _store = store;
        _client = client;
        _tasks = tasks;
        _import = import;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TrackerConnectionResponse>>> List(CancellationToken cancellationToken)
    {
        var connections = await _store.ListAsync(cancellationToken);
        return Ok(connections.Select(TrackerConnectionResponse.From).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<TrackerConnectionResponse>> Create(
        [FromBody] CreateTrackerConnectionRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<TrackerProviderKind>(request.Provider, ignoreCase: true, out var provider))
        {
            return BadRequest(new { error = "Provider must be Jira." });
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { error = "Token is required." });
        }

        var baseUrl = NormalizeBaseUrl(request.BaseUrl);
        if (baseUrl is null)
        {
            return BadRequest(new { error = "Site address must look like https://your-team.atlassian.net." });
        }

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        var probe = new TrackerConnectionInfo(Guid.Empty, provider, baseUrl, email, request.Token.Trim());
        var validation = await _client.ValidateAsync(probe, cancellationToken);
        if (!validation.IsSuccess)
        {
            return BadRequest(new { error = validation.ErrorMessage });
        }

        var host = new Uri(baseUrl).Host;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? $"{validation.Data} · {host}" : request.DisplayName.Trim();
        if (displayName.Length > 200)
        {
            return BadRequest(new { error = "Display name must be at most 200 characters." });
        }

        var created = await _store.CreateAsync(provider, baseUrl, displayName, email, request.Token, cancellationToken);
        return Created($"api/tracker-connections/{created.Id}", TrackerConnectionResponse.From(created));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await _store.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpGet("{id:guid}/issues")]
    public async Task<ActionResult<IReadOnlyList<TrackerIssueResponse>>> Issues(
        Guid id,
        [FromQuery] string? q,
        [FromQuery] Guid? workspaceId,
        CancellationToken cancellationToken)
    {
        var connection = await _store.GetInfoAsync(id, cancellationToken);
        if (connection is null)
        {
            return NotFound(new { error = "The connection was not found or its token can no longer be read. Add it again." });
        }

        var result = await _client.SearchAsync(connection, q, cancellationToken);
        if (!result.IsSuccess)
        {
            return StatusCode(result.IsAuthError ? StatusCodes.Status401Unauthorized : StatusCodes.Status502BadGateway, new { error = result.ErrorMessage });
        }

        var issues = result.Data ?? Array.Empty<TrackerIssue>();
        var imported = workspaceId.HasValue && issues.Count > 0
            ? await _tasks.FindByExternalKeysAsync(workspaceId.Value, ImportTrackerIssuesCommandHandler.JiraSource, TrackerOrigin.Normalize(connection.BaseUrl), issues.Select(i => i.Key.ToUpperInvariant()).ToList(), cancellationToken)
            : new HashSet<string>();

        return Ok(issues.Select(i => TrackerIssueResponse.From(i, imported.Contains(i.Key))).ToList());
    }

    [HttpPost("{id:guid}/import")]
    public async Task<ActionResult<ImportResponse>> Import(
        Guid id,
        [FromBody] ImportIssuesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _import.HandleAsync(
            new ImportTrackerIssuesCommand(id, request.RepositoryWorkspaceId, request.IssueKeys ?? new List<string>()),
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new { error = result.ErrorMessage });
        }

        return Ok(new ImportResponse(
            result.Items.Select(i => new ImportItemResponse(i.Key, i.Outcome.ToString(), i.Task, i.Message)).ToList(),
            result.Items.Count(i => i.Outcome == ImportOutcome.Imported),
            result.Items.Count(i => i.Outcome == ImportOutcome.AlreadyImported),
            result.Items.Count(i => i.Outcome == ImportOutcome.Failed)));
    }

    public static string? NormalizeBaseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (!value.Contains("://"))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
    }
}

public sealed class CreateTrackerConnectionRequest
{
    public string Provider { get; set; } = "Jira";

    public string BaseUrl { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? Email { get; set; }

    public string Token { get; set; } = string.Empty;
}

public sealed class ImportIssuesRequest
{
    public Guid RepositoryWorkspaceId { get; set; }

    public List<string>? IssueKeys { get; set; }
}

public sealed record TrackerConnectionResponse(Guid Id, string Provider, string BaseUrl, string DisplayName, string? Email, DateTime CreatedAt)
{
    public static TrackerConnectionResponse From(TrackerConnectionDto dto) =>
        new(dto.Id, dto.Provider.ToString(), dto.BaseUrl, dto.DisplayName, dto.Email, dto.CreatedAt);
}

public sealed record TrackerIssueResponse(
    string Key,
    string Summary,
    string? Description,
    string? Status,
    string? IssueType,
    string? Priority,
    IReadOnlyList<string> Labels,
    string Url,
    bool AlreadyImported)
{
    public static TrackerIssueResponse From(TrackerIssue issue, bool alreadyImported) =>
        new(issue.Key, issue.Summary, issue.Description, issue.Status, issue.IssueType, issue.Priority, issue.Labels, issue.Url, alreadyImported);
}

public sealed record ImportItemResponse(string Key, string Outcome, TaskDto? Task, string? Message);

public sealed record ImportResponse(IReadOnlyList<ImportItemResponse> Items, int Imported, int AlreadyImported, int Failed);
