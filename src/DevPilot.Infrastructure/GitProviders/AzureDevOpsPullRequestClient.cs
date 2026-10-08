using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.GitProviders;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.GitProviders;

/// <summary>
/// Azure DevOps pull requests behind the port the GitHub client implements. A repository is addressed as owner
/// "organization/project" plus repository. Completing a pull request is asynchronous on Azure, so the client waits briefly
/// for the final state. CI comes from commit statuses; a repository whose pipelines publish none reports no checks.
/// </summary>
internal sealed class AzureDevOpsPullRequestClient : IGitHubPullRequestClient
{
    public const string HttpClientName = "AzureDevOpsPullRequest";
    internal const string ApiVersion = "7.1";
    private const string BranchPrefix = "refs/heads/";

    private const int CompletionPolls = 5;
    private readonly TimeSpan _completionPollDelay;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitCredentialResolver _credentials;
    private readonly ILogger<AzureDevOpsPullRequestClient> _logger;

    public AzureDevOpsPullRequestClient(
        IHttpClientFactory httpClientFactory,
        IGitCredentialResolver credentials,
        ILogger<AzureDevOpsPullRequestClient> logger,
        TimeSpan? completionPollDelay = null)
    {
        _completionPollDelay = completionPollDelay ?? TimeSpan.FromSeconds(2);
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _logger = logger;
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
            sourceRefName = BranchPrefix + head,
            targetRefName = BranchPrefix + baseBranch,
            title,
            description = Truncate(body, 3900),
        });

        using var response = await SendAsync(HttpMethod.Post, target.Value!, "pullrequests", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<GitHubPullRequestDto>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return GitHubPullRequestClientResult<GitHubPullRequestDto>.Success(ToPullRequest(doc.RootElement, target.Value!));
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

        var query = "pullrequests?searchCriteria.status=all" +
                    $"&searchCriteria.sourceRefName={Uri.EscapeDataString(BranchPrefix + head)}" +
                    $"&searchCriteria.targetRefName={Uri.EscapeDataString(BranchPrefix + baseBranch)}&$top=100";

        using var response = await SendAsync(HttpMethod.Get, target.Value!, query, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<IReadOnlyList<GitHubPullRequestDto>>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var results = new List<GitHubPullRequestDto>();
        if (doc.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            results.AddRange(values.EnumerateArray().Select(v => ToPullRequest(v, target.Value!)));
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

        using var response = await SendAsync(
            HttpMethod.Get, target.Value!, $"refs?filter={Uri.EscapeDataString("heads/" + branch)}", null, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var err = await HandleErrorAsync<string>(response, cancellationToken).ConfigureAwait(false);
            return new GitHubBranchRefResult(false, false, null, err.ErrorMessage ?? "Failed to query remote branch.");
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var sha = FindBranchSha(doc.RootElement, branch);
        return sha is null
            ? new GitHubBranchRefResult(false, true, null, $"Remote branch '{branch}' was not found on Azure DevOps.")
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

    // Azure reports pipeline results as commit statuses, so check runs stay empty.
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
            HttpMethod.Get, target.Value!, $"commits/{Uri.EscapeDataString(refSha)}/statuses?latestOnly=true&$top=100", null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<IReadOnlyList<GitHubCommitStatusDto>>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var statuses = new List<GitHubCommitStatusDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in values.EnumerateArray())
            {
                var context = item.TryGetProperty("context", out var ctx) ? ctx : default;
                var genre = GetString(context, "genre");
                var name = GetString(context, "name") ?? "default";
                var label = string.IsNullOrWhiteSpace(genre) ? name : $"{genre}/{name}";
                if (!seen.Add(label))
                {
                    continue;
                }

                statuses.Add(new GitHubCommitStatusDto(
                    Id: item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
                    Context: label,
                    State: MapStatus(GetString(item, "state")),
                    Description: GetString(item, "description"),
                    CreatedAt: GetDate(item, "creationDate") ?? DateTime.UtcNow,
                    UpdatedAt: GetDate(item, "updatedDate")));
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
            status = "completed",
            // The approved head: Azure refuses to complete if the source branch moved on.
            lastMergeSourceCommit = new { commitId = expectedHeadSha },
            completionOptions = new
            {
                mergeStrategy = "noFastForward",
                deleteSourceBranch = false,
                mergeCommitMessage = string.IsNullOrWhiteSpace(commitTitle) ? commitMessage : commitTitle,
            },
        });

        using var response = await SendAsync(
            HttpMethod.Patch, target.Value!, $"pullrequests/{pullNumber}", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<GitHubMergeResultDto>(response, cancellationToken).ConfigureAwait(false);
        }

        // Completion is queued; wait for the outcome instead of reporting a merge that has not happened yet.
        for (var attempt = 0; attempt < CompletionPolls; attempt++)
        {
            var current = await GetPullRequestAsync(target.Value!, pullNumber, cancellationToken).ConfigureAwait(false);
            if (!current.IsSuccess || current.Data is null)
            {
                return GitHubPullRequestClientResult<GitHubMergeResultDto>.Failure(current.ErrorMessage ?? "Could not confirm the completion.");
            }

            if (current.Data.Merged)
            {
                return GitHubPullRequestClientResult<GitHubMergeResultDto>.Success(new GitHubMergeResultDto(await ReadMergeCommitAsync(target.Value!, pullNumber, cancellationToken).ConfigureAwait(false), true));
            }

            if (string.Equals(current.Data.MergeableState, "dirty", StringComparison.OrdinalIgnoreCase))
            {
                return GitHubPullRequestClientResult<GitHubMergeResultDto>.Failure(
                    "Azure DevOps could not complete the pull request: it has merge conflicts with the target branch.",
                    isConflict: true,
                    isNotMergeable: true,
                    isBaseConflict: true);
            }

            if (!string.Equals(current.Data.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await Task.Delay(_completionPollDelay, cancellationToken).ConfigureAwait(false);
        }

        // Still queued: the merge command re-reads the pull request and recovers once it completes.
        return GitHubPullRequestClientResult<GitHubMergeResultDto>.Success(new GitHubMergeResultDto(null, false));
    }

    internal static string MapStatus(string? azureState) =>
        azureState?.ToLowerInvariant() switch
        {
            "succeeded" => "success",
            "failed" => "failure",
            "error" => "error",
            "notapplicable" => "success",
            "pending" or "notset" => "pending",
            _ => "pending",
        };

    internal static string MapPullRequestState(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "active" => "open",
            "completed" or "abandoned" => "closed",
            _ => status ?? string.Empty,
        };

    internal static string? FindBranchSha(JsonElement root, string branch)
    {
        if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // The filter matches by prefix, so heads/feature also returns heads/feature-2.
        var expected = BranchPrefix + branch;
        foreach (var item in values.EnumerateArray())
        {
            if (string.Equals(GetString(item, "name"), expected, StringComparison.Ordinal))
            {
                var sha = GetString(item, "objectId");
                return string.IsNullOrWhiteSpace(sha) ? null : sha;
            }
        }

        return null;
    }

    internal static GitHubPullRequestDto ToPullRequest(JsonElement pr, AzureRepo repo)
    {
        var status = GetString(pr, "status");
        var merged = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
        var closedAt = GetDate(pr, "closedDate");
        var id = pr.TryGetProperty("pullRequestId", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32() : 0;
        var head = pr.TryGetProperty("lastMergeSourceCommit", out var source) ? GetString(source, "commitId") : null;

        return new GitHubPullRequestDto(
            Number: id,
            HtmlUrl: repo.PullRequestUrl(id),
            State: MapPullRequestState(status),
            Merged: merged,
            ClosedAt: closedAt,
            MergedAt: merged ? closedAt : null,
            HeadRef: StripBranchPrefix(GetString(pr, "sourceRefName")),
            HeadSha: head ?? string.Empty,
            HeadRepoOwner: string.Empty,
            HeadRepoName: string.Empty,
            BaseRef: StripBranchPrefix(GetString(pr, "targetRefName")),
            BaseRepoOwner: string.Empty,
            BaseRepoName: string.Empty,
            Body: GetString(pr, "description") ?? string.Empty,
            MergeableState: string.Equals(GetString(pr, "mergeStatus"), "conflicts", StringComparison.OrdinalIgnoreCase) ? "dirty" : null);
    }

    private static string StripBranchPrefix(string? refName) =>
        refName is not null && refName.StartsWith(BranchPrefix, StringComparison.Ordinal) ? refName[BranchPrefix.Length..] : refName ?? string.Empty;

    private async Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> GetPullRequestAsync(
        AzureRepo repo, int pullNumber, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, repo, $"pullrequests/{pullNumber}", null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorAsync<GitHubPullRequestDto>(response, cancellationToken).ConfigureAwait(false);
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return GitHubPullRequestClientResult<GitHubPullRequestDto>.Success(ToPullRequest(doc.RootElement, repo));
    }

    private async Task<string?> ReadMergeCommitAsync(AzureRepo repo, int pullNumber, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, repo, $"pullrequests/{pullNumber}", null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("lastMergeCommit", out var commit) ? GetString(commit, "commitId") : null;
    }

    private async Task<(AzureRepo? Value, string? Failure)> ResolveAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        var split = GitRemoteUrl.SplitAzureOwner(owner);
        if (split is null)
        {
            return (null, $"'{owner}' is not an Azure DevOps organization/project.");
        }

        var result = await _credentials.ResolveForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Credential is null)
        {
            return (null, result.ErrorMessage ?? "Azure DevOps authorization failed.");
        }

        return (new AzureRepo(split.Value.Organization, split.Value.Project, repository, result.Credential.Token), null);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, AzureRepo repo, string relative, HttpContent? content, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(HttpClientName);
        var separator = relative.Contains('?') ? '&' : '?';
        var uri = $"{repo.ApiBase}/{relative}{separator}api-version={ApiVersion}";
        using var request = new HttpRequestMessage(method, uri) { Content = content };

        // A personal access token is sent as the password of basic auth with an empty user name.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{repo.Token}")));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // A rejected token does not get a 401 from Azure DevOps but HTTP 203 with the HTML sign-in page. Treat it as the
        // refusal it is, so it is never parsed as a result.
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
        {
            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) };
        }

        return response;
    }

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
        _logger.LogWarning("Azure DevOps call failed with HTTP {Status}: {Message}", status, message);

        // A browser sign-in page (HTTP 203 or a redirect) means the token was not accepted at all.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation ||
            status is 302 or 203)
        {
            return GitHubPullRequestClientResult<T>.Failure(
                $"Azure DevOps denied the request (HTTP {status}). The token needs the Code (Read & Write) scope and access to the project.",
                isConfigurationError: true);
        }

        return status switch
        {
            429 => GitHubPullRequestClientResult<T>.Failure("Azure DevOps rate limit exceeded. Try again later.", isRateLimit: true),
            // 400 and 409 carry policy and duplicate-pull-request refusals (TF401179, TF401027 and similar).
            400 or 409 or 422 => GitHubPullRequestClientResult<T>.Failure(
                message,
                isConflict: true,
                isNotMergeable: true,
                isBaseConflict: message.Contains("conflict", StringComparison.OrdinalIgnoreCase)),
            _ => GitHubPullRequestClientResult<T>.Failure($"Azure DevOps API error (HTTP {status}): {message}"),
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

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    internal sealed record AzureRepo(string Organization, string Project, string Repository, string Token)
    {
        public string ApiBase =>
            $"https://{GitRemoteUrl.AzureHost}/{Uri.EscapeDataString(Organization)}/{Uri.EscapeDataString(Project)}/_apis/git/repositories/{Uri.EscapeDataString(Repository)}";

        public string PullRequestUrl(int id) =>
            $"https://{GitRemoteUrl.AzureHost}/{Uri.EscapeDataString(Organization)}/{Uri.EscapeDataString(Project)}/_git/{Uri.EscapeDataString(Repository)}/pullrequest/{id}";
    }
}
