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
/// Bitbucket Cloud pull requests behind the port the GitHub client implements. The owner is the workspace. Pipelines
/// publish their results as commit statuses. A token without a stored user name is a repository or workspace access
/// token (bearer); with a user name the token is an app password (basic auth).
/// </summary>
internal sealed class BitbucketPullRequestClient : IGitHubPullRequestClient
{
    public const string HttpClientName = "BitbucketPullRequest";
    internal const string ApiRoot = "https://api.bitbucket.org/2.0/repositories";

    private const int CompletionPolls = 5;
    private readonly TimeSpan _completionPollDelay;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitCredentialResolver _credentials;
    private readonly ILogger<BitbucketPullRequestClient> _logger;

    public BitbucketPullRequestClient(
        IHttpClientFactory httpClientFactory,
        IGitCredentialResolver credentials,
        ILogger<BitbucketPullRequestClient> logger,
        TimeSpan? completionPollDelay = null)
    {
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _logger = logger;
        _completionPollDelay = completionPollDelay ?? TimeSpan.FromSeconds(2);
    }

    public async Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> CreatePullRequestAsync(
        string owner, string repository, string head, string baseBranch, string title, string body,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null)
        {
            return GitHubPullRequestClientResult<GitHubPullRequestDto>.Failure(target.Failure, isConfigurationError: true);
        }

        var payload = JsonSerializer.Serialize(new
        {
            title,
            description = body,
            source = new { branch = new { name = head } },
            destination = new { branch = new { name = baseBranch } },
            close_source_branch = false,
        });

        using var response = await SendAsync(HttpMethod.Post, target.Value!, "pullrequests", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<GitHubPullRequestDto>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return GitHubPullRequestClientResult<GitHubPullRequestDto>.Success(await WithFullHeadAsync(target.Value!, ToPullRequest(doc.RootElement), cancellationToken).ConfigureAwait(false));
    }

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>> ListPullRequestsAsync(
        string owner, string repository, string head, string baseBranch,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null)
        {
            return GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>.Failure(target.Failure, isConfigurationError: true);
        }

        var filter = $"source.branch.name=\"{Quote(head)}\" AND destination.branch.name=\"{Quote(baseBranch)}\"";
        var query = "pullrequests?state=OPEN&state=MERGED&state=DECLINED&state=SUPERSEDED&pagelen=50" +
                    $"&q={Uri.EscapeDataString(filter)}";

        using var response = await SendAsync(HttpMethod.Get, target.Value!, query, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<IReadOnlyList<GitHubPullRequestDto>>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var results = new List<GitHubPullRequestDto>();
        if (doc.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in values.EnumerateArray())
            {
                results.Add(await WithFullHeadAsync(target.Value!, ToPullRequest(item), cancellationToken).ConfigureAwait(false));
            }
        }

        return GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>.Success(results);
    }

    public async Task<GitHubBranchRefResult> GetBranchHeadShaAsync(
        string owner, string repository, string branch,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null)
        {
            return new GitHubBranchRefResult(false, false, null, target.Failure);
        }

        // A query by exact name works for branch names with slashes, where a path segment would need escaping tricks.
        var query = $"refs/branches?pagelen=5&q={Uri.EscapeDataString($"name=\"{Quote(branch)}\"")}";
        using var response = await SendAsync(HttpMethod.Get, target.Value!, query, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await HandleErrorAsync<string>(response, cancellationToken).ConfigureAwait(false);
            return new GitHubBranchRefResult(false, false, null, err.ErrorMessage ?? "Failed to query remote branch.");
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var sha = FindBranchSha(doc.RootElement, branch);
        return sha is null
            ? new GitHubBranchRefResult(false, true, null, $"Remote branch '{branch}' was not found on Bitbucket.")
            : new GitHubBranchRefResult(true, false, sha, null);
    }

    public async Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> GetPullRequestAsync(
        string owner, string repository, int pullNumber,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null)
        {
            return GitHubPullRequestClientResult<GitHubPullRequestDto>.Failure(target.Failure, isConfigurationError: true);
        }

        return await GetPullRequestAsync(target.Value!, pullNumber, cancellationToken).ConfigureAwait(false);
    }

    // Bitbucket reports pipeline results as commit statuses, so check runs stay empty.
    public Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>> ListCheckRunsForRefAsync(
        string owner, string repository, string refSha,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>.Success(Array.Empty<GitHubCheckRunDto>()));

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>> ListCommitStatusesForRefAsync(
        string owner, string repository, string refSha,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null)
        {
            return GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>.Failure(target.Failure, isConfigurationError: true);
        }

        using var response = await SendAsync(
            HttpMethod.Get, target.Value!, $"commit/{Uri.EscapeDataString(refSha)}/statuses?pagelen=100", null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<IReadOnlyList<GitHubCommitStatusDto>>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var statuses = new List<GitHubCommitStatusDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            var index = 0L;
            foreach (var item in values.EnumerateArray())
            {
                var name = GetString(item, "name") ?? GetString(item, "key") ?? "default";
                if (!seen.Add(name))
                {
                    continue;
                }

                statuses.Add(new GitHubCommitStatusDto(
                    Id: ++index,
                    Context: name,
                    State: MapStatus(GetString(item, "state")),
                    Description: GetString(item, "description"),
                    CreatedAt: GetDate(item, "created_on") ?? DateTime.UtcNow,
                    UpdatedAt: GetDate(item, "updated_on")));
            }
        }

        return GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>.Success(statuses);
    }

    public async Task<GitHubPullRequestClientResult<GitHubMergeResultDto>> MergePullRequestAsync(
        string owner, string repository, int pullNumber, string expectedHeadSha,
        string? commitTitle = null, string? commitMessage = null,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null)
        {
            return GitHubPullRequestClientResult<GitHubMergeResultDto>.Failure(target.Failure, isConfigurationError: true);
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "pullrequest",
            merge_strategy = "merge_commit",
            close_source_branch = false,
            message = string.IsNullOrWhiteSpace(commitTitle) ? commitMessage : commitTitle,
        });

        using var response = await SendAsync(
            HttpMethod.Post, target.Value!, $"pullrequests/{pullNumber}/merge", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<GitHubMergeResultDto>(response, cancellationToken).ConfigureAwait(false);
        }

        // 200 carries the merged pull request; 202 means the merge runs in the background, so wait for the outcome.
        string? mergeCommit = null;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (string.Equals(GetString(doc.RootElement, "state"), "MERGED", StringComparison.OrdinalIgnoreCase))
            {
                mergeCommit = doc.RootElement.TryGetProperty("merge_commit", out var commit) ? GetString(commit, "hash") : null;
                return GitHubPullRequestClientResult<GitHubMergeResultDto>.Success(new GitHubMergeResultDto(mergeCommit, true));
            }
        }

        for (var attempt = 0; attempt < CompletionPolls; attempt++)
        {
            await Task.Delay(_completionPollDelay, cancellationToken).ConfigureAwait(false);
            var current = await GetPullRequestAsync(target.Value!, pullNumber, cancellationToken).ConfigureAwait(false);
            if (!current.IsSuccess || current.Data is null)
            {
                break;
            }

            if (current.Data.Merged)
            {
                return GitHubPullRequestClientResult<GitHubMergeResultDto>.Success(new GitHubMergeResultDto(mergeCommit, true));
            }

            if (!string.Equals(current.Data.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        // Still running: the merge command re-reads the pull request and recovers once it finishes.
        return GitHubPullRequestClientResult<GitHubMergeResultDto>.Success(new GitHubMergeResultDto(null, false));
    }

    internal static string MapStatus(string? state) =>
        state?.ToUpperInvariant() switch
        {
            "SUCCESSFUL" => "success",
            "FAILED" => "failure",
            "STOPPED" => "error",
            "INPROGRESS" => "pending",
            _ => "pending",
        };

    internal static string? FindBranchSha(JsonElement root, string branch)
    {
        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in values.EnumerateArray())
        {
            if (string.Equals(GetString(item, "name"), branch, StringComparison.Ordinal) &&
                item.TryGetProperty("target", out var tip))
            {
                var sha = GetString(tip, "hash");
                return string.IsNullOrWhiteSpace(sha) ? null : sha;
            }
        }

        return null;
    }

    internal static GitHubPullRequestDto ToPullRequest(JsonElement pr)
    {
        var state = GetString(pr, "state");
        var merged = string.Equals(state, "MERGED", StringComparison.OrdinalIgnoreCase);
        var open = string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase);
        var updated = GetDate(pr, "updated_on");
        var id = pr.TryGetProperty("id", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32() : 0;

        var html = pr.TryGetProperty("links", out var links) && links.TryGetProperty("html", out var htmlLink)
            ? GetString(htmlLink, "href")
            : null;

        return new GitHubPullRequestDto(
            Number: id,
            HtmlUrl: html ?? string.Empty,
            State: open ? "open" : "closed",
            Merged: merged,
            ClosedAt: open ? null : updated,
            MergedAt: merged ? updated : null,
            HeadRef: BranchName(pr, "source"),
            HeadSha: pr.TryGetProperty("source", out var source) && source.TryGetProperty("commit", out var commit) ? GetString(commit, "hash") ?? string.Empty : string.Empty,
            HeadRepoOwner: string.Empty,
            HeadRepoName: string.Empty,
            BaseRef: BranchName(pr, "destination"),
            BaseRepoOwner: string.Empty,
            BaseRepoName: string.Empty,
            Body: GetString(pr, "description") ?? string.Empty,
            MergeableState: null);
    }

    private static string BranchName(JsonElement pr, string side) =>
        pr.TryGetProperty(side, out var node) && node.TryGetProperty("branch", out var branch) ? GetString(branch, "name") ?? string.Empty : string.Empty;

    /// <summary>The pull request payload carries a 12 character commit id; the rest of DevPilot compares full ids.</summary>
    private async Task<GitHubPullRequestDto> WithFullHeadAsync(Target repo, GitHubPullRequestDto pr, CancellationToken cancellationToken)
    {
        if (pr.HeadSha.Length is 0 or >= 40)
        {
            return pr;
        }

        using var response = await SendAsync(HttpMethod.Get, repo, $"commit/{Uri.EscapeDataString(pr.HeadSha)}", null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return pr;
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var full = GetString(doc.RootElement, "hash");
        return string.IsNullOrWhiteSpace(full) ? pr : pr with { HeadSha = full };
    }

    private async Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> GetPullRequestAsync(
        Target repo, int pullNumber, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, repo, $"pullrequests/{pullNumber}", null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<GitHubPullRequestDto>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return GitHubPullRequestClientResult<GitHubPullRequestDto>.Success(await WithFullHeadAsync(repo, ToPullRequest(doc.RootElement), cancellationToken).ConfigureAwait(false));
    }

    private async Task<(Target? Value, string? Failure)> ResolveAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        var result = await _credentials.ResolveForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Credential is null)
        {
            return (null, result.ErrorMessage ?? "Bitbucket authorization failed.");
        }

        return (new Target(owner, repository, result.Credential.Username, result.Credential.Token), null);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Target repo, string relative, HttpContent? content, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, $"{repo.ApiBase}/{relative}") { Content = content };
        request.Headers.Authorization = BuildAuthorization(repo.Username, repo.Token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static AuthenticationHeaderValue BuildAuthorization(string username, string token) =>
        string.Equals(username, GitCredentialResolver.DefaultUsername(GitProviderKind.Bitbucket), StringComparison.Ordinal)
            ? new AuthenticationHeaderValue("Bearer", token)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}")));

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    }

    private async Task<GitHubPullRequestClientResult<T>> HandleErrorAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = GitProviderErrorText.Extract(raw);
        _logger.LogWarning("Bitbucket call failed with HTTP {Status}: {Message}", status, message);

        return status switch
        {
            401 or 403 => GitHubPullRequestClientResult<T>.Failure(
                $"Bitbucket denied the request (HTTP {status}). Use an app password with Pull requests and Repositories write access (with your user name), or a repository access token.",
                isConfigurationError: true),
            429 => GitHubPullRequestClientResult<T>.Failure("Bitbucket rate limit exceeded. Try again later.", isRateLimit: true),
            // Merge refusals and a duplicate pull request arrive as 400 or 409; a conflict says so in its message.
            400 or 409 or 422 => GitHubPullRequestClientResult<T>.Failure(
                message,
                isConflict: true,
                isNotMergeable: true,
                isBaseConflict: message.Contains("conflict", StringComparison.OrdinalIgnoreCase)),
            _ => GitHubPullRequestClientResult<T>.Failure($"Bitbucket API error (HTTP {status}): {message}"),
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? GetDate(JsonElement element, string name)
    {
        var raw = GetString(element, name);
        return raw is not null &&
               DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string Quote(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    internal sealed record Target(string Workspace, string Repository, string Username, string Token)
    {
        public string ApiBase => $"{ApiRoot}/{Uri.EscapeDataString(Workspace)}/{Uri.EscapeDataString(Repository)}";
    }
}
