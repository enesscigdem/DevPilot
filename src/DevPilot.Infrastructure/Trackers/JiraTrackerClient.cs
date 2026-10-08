using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevPilot.Application.Trackers;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Trackers;

/// <summary>
/// Jira over REST API v2, which returns descriptions as plain text on Cloud and on Server / Data Center alike.
/// Jira Cloud authenticates with the account e-mail and an API token, Server / Data Center with a bearer personal access token.
/// </summary>
internal sealed class JiraTrackerClient : ITrackerClient
{
    public const string HttpClientName = "Tracker";
    private const string IssueFields = "summary,description,status,issuetype,priority,labels";
    private const int MaxResults = 50;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JiraTrackerClient> _logger;

    public JiraTrackerClient(IHttpClientFactory httpClientFactory, ILogger<JiraTrackerClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TrackerResult<string>> ValidateAsync(TrackerConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(connection, HttpMethod.Get, "/rest/api/2/myself", null, cancellationToken).ConfigureAwait(false);
        if (response.Failure is not null)
        {
            return TrackerResult<string>.Failure(response.Failure.Message, response.Failure.IsAuth, response.Failure.IsNotFound);
        }

        using var doc = response.Document!;
        var name = GetString(doc.RootElement, "displayName") ?? GetString(doc.RootElement, "name") ?? "Jira";
        return TrackerResult<string>.Success(name);
    }

    public async Task<TrackerResult<IReadOnlyList<TrackerIssue>>> SearchAsync(
        TrackerConnectionInfo connection,
        string? query,
        CancellationToken cancellationToken = default)
    {
        var jql = JiraQuery.ToJql(query);
        var path = IsCloud(connection.BaseUrl) ? "/rest/api/2/search/jql" : "/rest/api/2/search";
        var url = $"{path}?jql={Uri.EscapeDataString(jql)}&fields={IssueFields}&maxResults={MaxResults}";

        var response = await SendAsync(connection, HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        if (response.Failure is not null)
        {
            return TrackerResult<IReadOnlyList<TrackerIssue>>.Failure(response.Failure.Message, response.Failure.IsAuth, response.Failure.IsNotFound);
        }

        using var doc = response.Document!;
        var issues = new List<TrackerIssue>();
        if (doc.RootElement.TryGetProperty("issues", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var issue = ParseIssue(connection.BaseUrl, item);
                if (issue is not null)
                {
                    issues.Add(issue);
                }
            }
        }

        return TrackerResult<IReadOnlyList<TrackerIssue>>.Success(issues);
    }

    public async Task<TrackerResult<TrackerIssue>> GetIssueAsync(
        TrackerConnectionInfo connection,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (!JiraQuery.IsIssueKey(key))
        {
            return TrackerResult<TrackerIssue>.Failure($"'{key}' is not a Jira issue key.");
        }

        var response = await SendAsync(
            connection, HttpMethod.Get, $"/rest/api/2/issue/{Uri.EscapeDataString(key)}?fields={IssueFields}", null, cancellationToken).ConfigureAwait(false);
        if (response.Failure is not null)
        {
            var message = response.Failure.IsNotFound ? $"Issue {key} was not found, or the account cannot see it." : response.Failure.Message;
            return TrackerResult<TrackerIssue>.Failure(message, response.Failure.IsAuth, response.Failure.IsNotFound);
        }

        using var doc = response.Document!;
        var issue = ParseIssue(connection.BaseUrl, doc.RootElement);
        return issue is null
            ? TrackerResult<TrackerIssue>.Failure($"Jira returned an issue {key} that could not be read.")
            : TrackerResult<TrackerIssue>.Success(issue);
    }

    public async Task<TrackerResult<bool>> AddCommentAsync(
        TrackerConnectionInfo connection,
        string key,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!JiraQuery.IsIssueKey(key))
        {
            return TrackerResult<bool>.Failure($"'{key}' is not a Jira issue key.");
        }

        var body = JsonSerializer.Serialize(new { body = text });
        var response = await SendAsync(
            connection,
            HttpMethod.Post,
            $"/rest/api/2/issue/{Uri.EscapeDataString(key)}/comment",
            new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);

        if (response.Failure is not null)
        {
            return TrackerResult<bool>.Failure(response.Failure.Message, response.Failure.IsAuth, response.Failure.IsNotFound);
        }

        response.Document?.Dispose();
        return TrackerResult<bool>.Success(true);
    }

    internal static bool IsCloud(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase);

    internal static AuthenticationHeaderValue BuildAuthorization(TrackerConnectionInfo connection) =>
        string.IsNullOrWhiteSpace(connection.Email)
            ? new AuthenticationHeaderValue("Bearer", connection.Token)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Email}:{connection.Token}")));

    private static TrackerIssue? ParseIssue(string baseUrl, JsonElement element)
    {
        var key = GetString(element, "key");
        if (string.IsNullOrWhiteSpace(key) || !element.TryGetProperty("fields", out var fields))
        {
            return null;
        }

        var labels = new List<string>();
        if (fields.TryGetProperty("labels", out var labelArray) && labelArray.ValueKind == JsonValueKind.Array)
        {
            labels.AddRange(labelArray.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!));
        }

        return new TrackerIssue(
            Key: key,
            Summary: GetString(fields, "summary") ?? key,
            Description: ReadDescription(fields),
            Status: GetNestedName(fields, "status"),
            IssueType: GetNestedName(fields, "issuetype"),
            Priority: GetNestedName(fields, "priority"),
            Labels: labels,
            Url: $"{baseUrl.TrimEnd('/')}/browse/{key}");
    }

    /// <summary>v2 gives text; a few instances still send Atlassian Document Format, which is flattened to its text.</summary>
    private static string? ReadDescription(JsonElement fields)
    {
        if (!fields.TryGetProperty("description", out var description))
        {
            return null;
        }

        return description.ValueKind switch
        {
            JsonValueKind.String => description.GetString(),
            JsonValueKind.Object => FlattenDocument(description),
            _ => null,
        };
    }

    private static string FlattenDocument(JsonElement node)
    {
        var builder = new StringBuilder();
        Append(node);
        return builder.ToString().Trim();

        void Append(JsonElement current)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    builder.Append(text.GetString());
                }

                if (current.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in content.EnumerateArray())
                    {
                        Append(child);
                    }

                    var type = GetString(current, "type");
                    if (type is "paragraph" or "heading" or "listItem" or "codeBlock" or "blockquote")
                    {
                        builder.AppendLine();
                    }
                }
            }
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetNestedName(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var nested) ? GetString(nested, "name") : null;

    private async Task<JiraResponse> SendAsync(
        TrackerConnectionInfo connection,
        HttpMethod method,
        string relativeUrl,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out _))
        {
            return JiraResponse.Fail($"'{connection.BaseUrl}' is not a valid Jira address.");
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(method, $"{connection.BaseUrl.TrimEnd('/')}{relativeUrl}") { Content = content };
            request.Headers.Authorization = BuildAuthorization(connection);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return JiraResponse.Ok(string.IsNullOrWhiteSpace(raw) ? JsonDocument.Parse("{}") : JsonDocument.Parse(raw));
            }

            _logger.LogWarning("Jira answered HTTP {Status} for {Method} {Path}.", (int)response.StatusCode, method, relativeUrl.Split('?')[0]);
            return JiraResponse.Fail(DescribeError(response.StatusCode, raw), IsAuth(response.StatusCode), response.StatusCode == HttpStatusCode.NotFound);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return JiraResponse.Fail($"Could not reach Jira at '{connection.BaseUrl}': {ex.Message}");
        }
    }

    private static bool IsAuth(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static string DescribeError(HttpStatusCode status, string raw) => status switch
    {
        HttpStatusCode.Unauthorized => "Jira rejected the credentials (HTTP 401). Check the e-mail and API token; Server and Data Center use only a personal access token.",
        HttpStatusCode.Forbidden => "The account is not allowed to do this in Jira (HTTP 403).",
        HttpStatusCode.NotFound => "Jira could not find that (HTTP 404). Check the site address.",
        HttpStatusCode.TooManyRequests => "Jira is rate limiting requests (HTTP 429). Try again in a minute.",
        HttpStatusCode.BadRequest => $"Jira could not run this: {ReadMessages(raw)}",
        _ => $"Jira answered HTTP {(int)status}.",
    };

    private static string ReadMessages(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("errorMessages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                var text = string.Join(" ", messages.EnumerateArray().Select(m => m.GetString()).Where(m => !string.IsNullOrWhiteSpace(m)));
                if (text.Length > 0)
                {
                    return text.Length > 300 ? text[..300] : text;
                }
            }
        }
        catch (JsonException)
        {
        }

        return "the request was rejected.";
    }

    private sealed record JiraFailure(string Message, bool IsAuth, bool IsNotFound);

    private sealed class JiraResponse
    {
        public JsonDocument? Document { get; private init; }

        public JiraFailure? Failure { get; private init; }

        public static JiraResponse Ok(JsonDocument document) => new() { Document = document };

        public static JiraResponse Fail(string message, bool isAuth = false, bool isNotFound = false) =>
            new() { Failure = new JiraFailure(message, isAuth, isNotFound) };
    }
}

/// <summary>Turns what a person typed into a JQL query.</summary>
public static class JiraQuery
{
    private static readonly Regex IssueKeyPattern = new(@"^[A-Za-z][A-Za-z0-9_]*-\d+$", RegexOptions.Compiled);

    // An operator, ORDER BY, IN (...) or IS EMPTY marks a JQL clause. Plain words, even ones like "is", are a text search.
    private static readonly Regex JqlMarkers = new(
        @"[=~<>!]|\border\s+by\b|\b(not\s+)?in\s*\(|\bis\s+(not\s+)?(empty|null)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const string DefaultJql = "assignee = currentUser() AND resolution = Unresolved ORDER BY updated DESC";

    public static bool IsIssueKey(string? value) => !string.IsNullOrWhiteSpace(value) && IssueKeyPattern.IsMatch(value.Trim());

    public static string ToJql(string? query)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DefaultJql;
        }

        if (IsIssueKey(trimmed))
        {
            return $"key = \"{trimmed.ToUpperInvariant()}\"";
        }

        if (JqlMarkers.IsMatch(trimmed))
        {
            return trimmed;
        }

        var escaped = trimmed.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"text ~ \"{escaped}\" ORDER BY updated DESC";
    }
}
