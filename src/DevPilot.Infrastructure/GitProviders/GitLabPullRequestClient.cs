using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.GitProviders;

/// <summary>
/// GitLab merge requests behind the same port the GitHub client implements. A merge request is mapped to the
/// pull request DTO: opened becomes open, merged becomes closed with the merged flag, CI jobs become commit statuses.
/// </summary>
internal sealed class GitLabPullRequestClient : IGitHubPullRequestClient
{
    public const string HttpClientName = "GitLabMergeRequest";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitCredentialResolver _credentials;
    private readonly ILogger<GitLabPullRequestClient> _logger;

    public GitLabPullRequestClient(
        IHttpClientFactory httpClientFactory,
        IGitCredentialResolver credentials,
        ILogger<GitLabPullRequestClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> CreatePullRequestAsync(
        string owner,
        string repository,
        string head,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        var cred = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (cred.Failure is not null)
        {
            return MapFailure<GitHubPullRequestDto>(cred.Failure);
        }

        var payload = JsonSerializer.Serialize(new
        {
            source_branch = head,
            target_branch = baseBranch,
            title,
            description = body,
            remove_source_branch = false,
        });

        using var response = await SendAsync(
            HttpMethod.Post,
            $"{ProjectPath(owner, repository)}/merge_requests",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            cred.Credential!,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<GitHubPullRequestDto>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return GitHubPullRequestClientResult<GitHubPullRequestDto>.Success(ToPullRequest(doc.RootElement));
    }

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>> ListPullRequestsAsync(
        string owner,
        string repository,
        string head,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        var cred = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (cred.Failure is not null)
        {
            return MapFailure<IReadOnlyList<GitHubPullRequestDto>>(cred.Failure);
        }

        var results = new List<GitHubPullRequestDto>();
        for (var page = 1; page <= 5; page++)
        {
            var uri = $"{ProjectPath(owner, repository)}/merge_requests?state=all&source_branch={Uri.EscapeDataString(head)}" +
                      $"&target_branch={Uri.EscapeDataString(baseBranch)}&per_page=100&page={page}";

            using var response = await SendAsync(HttpMethod.Get, uri, null, cred.Credential!, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await HandleErrorAsync<IReadOnlyList<GitHubPullRequestDto>>(response, cancellationToken).ConfigureAwait(false);
            }

            using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var count = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                results.Add(ToPullRequest(item));
                count++;
            }

            if (count < 100)
            {
                break;
            }
        }

        return GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>.Success(results);
    }

    public async Task<GitHubBranchRefResult> GetBranchHeadShaAsync(
        string owner,
        string repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var cred = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (cred.Failure is not null)
        {
            return new GitHubBranchRefResult(false, false, null, cred.Failure);
        }

        using var response = await SendAsync(
            HttpMethod.Get,
            $"{ProjectPath(owner, repository)}/repository/branches/{Uri.EscapeDataString(branch)}",
            null,
            cred.Credential!,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new GitHubBranchRefResult(false, true, null, $"Remote branch '{branch}' was not found on GitLab.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await HandleErrorAsync<string>(response, cancellationToken).ConfigureAwait(false);
            return new GitHubBranchRefResult(false, false, null, err.ErrorMessage ?? "Failed to query remote branch.");
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var sha = doc.RootElement.TryGetProperty("commit", out var commit) && commit.TryGetProperty("id", out var id)
            ? id.GetString()
            : null;

        return string.IsNullOrWhiteSpace(sha)
            ? new GitHubBranchRefResult(false, false, null, "GitLab branch response contained no commit SHA.")
            : new GitHubBranchRefResult(true, false, sha, null);
    }

    public async Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> GetPullRequestAsync(
        string owner,
        string repository,
        int pullNumber,
        CancellationToken cancellationToken = default)
    {
        var cred = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (cred.Failure is not null)
        {
            return MapFailure<GitHubPullRequestDto>(cred.Failure);
        }

        using var response = await SendAsync(
            HttpMethod.Get,
            $"{ProjectPath(owner, repository)}/merge_requests/{pullNumber}",
            null,
            cred.Credential!,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<GitHubPullRequestDto>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return GitHubPullRequestClientResult<GitHubPullRequestDto>.Success(ToPullRequest(doc.RootElement));
    }

    // GitLab reports CI jobs as commit statuses, so check runs stay empty.
    public Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>> ListCheckRunsForRefAsync(
        string owner,
        string repository,
        string refSha,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>.Success(Array.Empty<GitHubCheckRunDto>()));

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>> ListCommitStatusesForRefAsync(
        string owner,
        string repository,
        string refSha,
        CancellationToken cancellationToken = default)
    {
        var cred = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (cred.Failure is not null)
        {
            return MapFailure<IReadOnlyList<GitHubCommitStatusDto>>(cred.Failure);
        }

        // all=false keeps the latest status per job, which is what the aggregate needs.
        var statuses = new List<GitHubCommitStatusDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= 5; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                $"{ProjectPath(owner, repository)}/repository/commits/{Uri.EscapeDataString(refSha)}/statuses?per_page=100&page={page}",
                null,
                cred.Credential!,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await HandleErrorAsync<IReadOnlyList<GitHubCommitStatusDto>>(response, cancellationToken).ConfigureAwait(false);
            }

            using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var count = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                count++;
                var name = GetString(item, "name") ?? "default";
                if (!seen.Add(name))
                {
                    continue;
                }

                statuses.Add(new GitHubCommitStatusDto(
                    Id: item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
                    Context: name,
                    State: MapStatus(GetString(item, "status")),
                    Description: GetString(item, "description"),
                    CreatedAt: GetDate(item, "created_at") ?? DateTime.UtcNow,
                    UpdatedAt: GetDate(item, "finished_at") ?? GetDate(item, "started_at")));
            }

            if (count < 100)
            {
                break;
            }
        }

        return GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>.Success(statuses);
    }

    public async Task<GitHubPullRequestClientResult<GitHubMergeResultDto>> MergePullRequestAsync(
        string owner,
        string repository,
        int pullNumber,
        string expectedHeadSha,
        string? commitTitle = null,
        string? commitMessage = null,
        CancellationToken cancellationToken = default)
    {
        var cred = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (cred.Failure is not null)
        {
            return MapFailure<GitHubMergeResultDto>(cred.Failure);
        }

        var payload = JsonSerializer.Serialize(new
        {
            sha = expectedHeadSha,
            merge_commit_message = string.IsNullOrWhiteSpace(commitTitle) ? commitMessage : $"{commitTitle}\n\n{commitMessage}".TrimEnd(),
        });

        using var response = await SendAsync(
            HttpMethod.Put,
            $"{ProjectPath(owner, repository)}/merge_requests/{pullNumber}/merge",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            cred.Credential!,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<GitHubMergeResultDto>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var merged = string.Equals(GetString(doc.RootElement, "state"), "merged", StringComparison.OrdinalIgnoreCase);
        var sha = GetString(doc.RootElement, "merge_commit_sha") ?? GetString(doc.RootElement, "squash_commit_sha");
        return GitHubPullRequestClientResult<GitHubMergeResultDto>.Success(new GitHubMergeResultDto(sha, merged));
    }

    internal static string MapStatus(string? gitlabStatus) =>
        gitlabStatus?.ToLowerInvariant() switch
        {
            "success" => "success",
            "failed" => "failure",
            "canceled" or "cancelled" => "error",
            "created" or "pending" or "running" or "waiting_for_resource" or "preparing" or "scheduled" or "manual" => "pending",
            "skipped" => "success",
            _ => "pending",
        };

    internal static string MapMergeRequestState(string? state) =>
        state?.ToLowerInvariant() switch
        {
            "opened" => "open",
            "merged" or "closed" or "locked" => "closed",
            _ => state ?? string.Empty,
        };

    internal static GitHubPullRequestDto ToPullRequest(JsonElement mr)
    {
        var state = GetString(mr, "state");
        var merged = string.Equals(state, "merged", StringComparison.OrdinalIgnoreCase);

        return new GitHubPullRequestDto(
            Number: mr.TryGetProperty("iid", out var iid) ? iid.GetInt32() : 0,
            HtmlUrl: GetString(mr, "web_url") ?? string.Empty,
            State: MapMergeRequestState(state),
            Merged: merged,
            ClosedAt: GetDate(mr, "closed_at"),
            MergedAt: GetDate(mr, "merged_at"),
            HeadRef: GetString(mr, "source_branch") ?? string.Empty,
            HeadSha: GetString(mr, "sha") ?? string.Empty,
            HeadRepoOwner: string.Empty,
            HeadRepoName: string.Empty,
            BaseRef: GetString(mr, "target_branch") ?? string.Empty,
            BaseRepoOwner: string.Empty,
            BaseRepoName: string.Empty,
            Body: GetString(mr, "description") ?? string.Empty,
            MergeableState: mr.TryGetProperty("has_conflicts", out var conflicts) && conflicts.ValueKind == JsonValueKind.True ? "dirty" : null);
    }

    private static string ProjectPath(string owner, string repository) =>
        $"projects/{Uri.EscapeDataString($"{owner}/{repository}")}";

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? GetDate(JsonElement element, string name)
    {
        var raw = GetString(element, name);
        return raw is not null &&
               DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    private async Task<(GitCredential? Credential, string? Failure)> ResolveAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken)
    {
        var result = await _credentials.ResolveForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Credential is null)
        {
            return (null, result.ErrorMessage ?? "GitLab authorization failed.");
        }

        if (string.IsNullOrWhiteSpace(result.Credential.Host))
        {
            return (null, "GitLab host is unknown for this repository.");
        }

        return (result.Credential, null);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUri,
        HttpContent? content,
        GitCredential credential,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(HttpClientName);
        var request = new HttpRequestMessage(method, $"https://{credential.Host}/api/v4/{relativeUri}") { Content = content };
        request.Headers.Add("PRIVATE-TOKEN", credential.Token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static GitHubPullRequestClientResult<T> MapFailure<T>(string message) =>
        GitHubPullRequestClientResult<T>.Failure(message, isConfigurationError: true);

    private async Task<GitHubPullRequestClientResult<T>> HandleErrorAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = GitProviderErrorText.Extract(raw);
        _logger.LogWarning("GitLab API call failed with HTTP {Status}: {Message}", status, message);

        return status switch
        {
            401 or 403 => GitHubPullRequestClientResult<T>.Failure(
                $"GitLab denied the request (HTTP {status}). The token needs the api scope and Developer role or higher.",
                isConfigurationError: true),
            429 => GitHubPullRequestClientResult<T>.Failure("GitLab rate limit exceeded. Try again later.", isRateLimit: true),
            // 405 means the MR cannot be merged right now, 406 is a branch conflict, 409 a stale sha or a duplicate MR.
            405 or 406 => GitHubPullRequestClientResult<T>.Failure(message, isConflict: true, isNotMergeable: true),
            409 or 422 => GitHubPullRequestClientResult<T>.Failure(message, isConflict: true),
            _ => GitHubPullRequestClientResult<T>.Failure($"GitLab API error (HTTP {status}): {message}"),
        };
    }
}

internal static class GitProviderErrorText
{
    public static string Extract(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "no details";
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var key in new[] { "message", "error" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var value))
                {
                    return Truncate(value.ValueKind == JsonValueKind.String ? value.GetString() ?? raw : value.ToString());
                }
            }
        }
        catch (JsonException)
        {
        }

        return Truncate(raw);
    }

    private static string Truncate(string text) =>
        Executions.GitRemoteUrlNormalizer.SanitizeOutput(text.Length > 300 ? text[..300] : text);
}
