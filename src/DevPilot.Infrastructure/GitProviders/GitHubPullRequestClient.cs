using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevPilot.Application.Executions.Ports;
using DevPilot.Infrastructure.Executions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Infrastructure.GitProviders;

internal sealed class GitHubPullRequestClient : IGitHubPullRequestClient
{
    public const string HttpClientName = "GitHubPullRequest";
    private const string ApiVersion = "2022-11-28";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubAppTokenService _tokenService;
    private readonly IOptions<GitHubAppOptions> _options;
    private readonly ILogger<GitHubPullRequestClient> _logger;

    public GitHubPullRequestClient(
        IHttpClientFactory httpClientFactory,
        IGitHubAppTokenService tokenService,
        IOptions<GitHubAppOptions> options,
        ILogger<GitHubPullRequestClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
        _options = options;
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
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return MapTokenFailure<GitHubPullRequestDto>(tokenResult);
        }

        var relativeUri = $"repos/{Escape(owner)}/{Escape(repository)}/pulls";
        var payload = new CreatePrRequestPayload
        {
            Title = title,
            Head = head,
            Base = baseBranch,
            Body = body
        };

        var jsonPayload = JsonSerializer.Serialize(payload);
        using var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        using var response = await SendAsync(HttpMethod.Post, relativeUri, content, tokenResult.Token, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorResponseAsync<GitHubPullRequestDto>(response, cancellationToken).ConfigureAwait(false);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var apiDto = JsonSerializer.Deserialize<GitHubApiPullRequestDto>(json, JsonSerializerOptions.Web);

        if (apiDto is null)
        {
            return GitHubPullRequestClientResult<GitHubPullRequestDto>.Failure("GitHub API returned empty PR response.");
        }

        return GitHubPullRequestClientResult<GitHubPullRequestDto>.Success(apiDto.ToDto());
    }

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>> ListPullRequestsAsync(
        string owner,
        string repository,
        string head,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return MapTokenFailure<IReadOnlyList<GitHubPullRequestDto>>(tokenResult);
        }

        var allPrs = new List<GitHubPullRequestDto>();
        var page = 1;
        const int perPage = 100;

        var headQuery = $"{owner}:{head}";

        while (page <= 5)
        {
            var relativeUri = $"repos/{Escape(owner)}/{Escape(repository)}/pulls?state=all&head={Escape(headQuery)}&base={Escape(baseBranch)}&per_page={perPage}&page={page}";

            using var response = await SendAsync(HttpMethod.Get, relativeUri, null, tokenResult.Token, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await HandleErrorResponseAsync<IReadOnlyList<GitHubPullRequestDto>>(response, cancellationToken).ConfigureAwait(false);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var pageDtos = JsonSerializer.Deserialize<List<GitHubApiPullRequestDto>>(json, JsonSerializerOptions.Web);

            if (pageDtos is null || pageDtos.Count == 0)
            {
                break;
            }

            allPrs.AddRange(pageDtos.Select(dto => dto.ToDto()));

            if (pageDtos.Count < perPage)
            {
                break;
            }

            page++;
        }

        return GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>.Success(allPrs);
    }

    public async Task<GitHubBranchRefResult> GetBranchHeadShaAsync(
        string owner,
        string repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return new GitHubBranchRefResult(false, false, null, tokenResult.ErrorMessage ?? "Repository authorization failed.");
        }

        var relativeUri = $"repos/{Escape(owner)}/{Escape(repository)}/branches/{Escape(branch)}";

        using var response = await SendAsync(HttpMethod.Get, relativeUri, null, tokenResult.Token, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new GitHubBranchRefResult(false, true, null, $"Remote branch '{branch}' was not found on GitHub.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await HandleErrorResponseAsync<string>(response, cancellationToken).ConfigureAwait(false);
            return new GitHubBranchRefResult(false, false, null, err.ErrorMessage ?? "Failed to query remote branch.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<GitHubApiBranchDto>(json, JsonSerializerOptions.Web);

        if (string.IsNullOrWhiteSpace(dto?.Commit?.Sha))
        {
            return new GitHubBranchRefResult(false, false, null, "GitHub branch response contained missing or empty commit SHA.");
        }

        return new GitHubBranchRefResult(true, false, dto.Commit.Sha, null);
    }

    public async Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> GetPullRequestAsync(
        string owner,
        string repository,
        int pullNumber,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return MapTokenFailure<GitHubPullRequestDto>(tokenResult);
        }

        var relativeUri = $"repos/{Escape(owner)}/{Escape(repository)}/pulls/{pullNumber}";

        using var response = await SendAsync(HttpMethod.Get, relativeUri, null, tokenResult.Token, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorResponseAsync<GitHubPullRequestDto>(response, cancellationToken).ConfigureAwait(false);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var apiDto = JsonSerializer.Deserialize<GitHubApiPullRequestDto>(json, JsonSerializerOptions.Web);

        if (apiDto is null)
        {
            return GitHubPullRequestClientResult<GitHubPullRequestDto>.Failure("GitHub API returned empty PR response.");
        }

        return GitHubPullRequestClientResult<GitHubPullRequestDto>.Success(apiDto.ToDto());
    }

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>> ListCheckRunsForRefAsync(
        string owner,
        string repository,
        string refSha,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return MapTokenFailure<IReadOnlyList<GitHubCheckRunDto>>(tokenResult);
        }

        var seenIds = new HashSet<long>();
        var checkRuns = new List<GitHubCheckRunDto>();
        var page = 1;
        const int perPage = 100;
        const int maxPages = 5;

        while (page <= maxPages)
        {
            var relativeUri = $"repos/{Escape(owner)}/{Escape(repository)}/commits/{Escape(refSha)}/check-runs?filter=latest&per_page={perPage}&page={page}";

            using var response = await SendAsync(HttpMethod.Get, relativeUri, null, tokenResult.Token, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await HandleErrorResponseAsync<IReadOnlyList<GitHubCheckRunDto>>(response, cancellationToken).ConfigureAwait(false);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var pageWrapper = JsonSerializer.Deserialize<GitHubApiCheckRunsListDto>(json, JsonSerializerOptions.Web);

            var pageItems = pageWrapper?.CheckRuns;
            if (pageItems is null || pageItems.Count == 0)
            {
                break;
            }

            foreach (var item in pageItems)
            {
                if (seenIds.Add(item.Id))
                {
                    checkRuns.Add(item.ToDto());
                }
            }

            if (pageItems.Count < perPage)
            {
                break;
            }

            page++;
        }

        return GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>.Success(checkRuns);
    }

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>> ListCommitStatusesForRefAsync(
        string owner,
        string repository,
        string refSha,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return MapTokenFailure<IReadOnlyList<GitHubCommitStatusDto>>(tokenResult);
        }

        var seenContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var statuses = new List<GitHubCommitStatusDto>();
        var page = 1;
        const int perPage = 100;
        const int maxPages = 5;

        while (page <= maxPages)
        {
            var relativeUri = $"repos/{Escape(owner)}/{Escape(repository)}/commits/{Escape(refSha)}/statuses?per_page={perPage}&page={page}";

            using var response = await SendAsync(HttpMethod.Get, relativeUri, null, tokenResult.Token, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await HandleErrorResponseAsync<IReadOnlyList<GitHubCommitStatusDto>>(response, cancellationToken).ConfigureAwait(false);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var pageItems = JsonSerializer.Deserialize<List<GitHubApiCommitStatusDto>>(json, JsonSerializerOptions.Web);

            if (pageItems is null || pageItems.Count == 0)
            {
                break;
            }

            foreach (var item in pageItems)
            {
                var contextName = string.IsNullOrWhiteSpace(item.Context) ? "default" : item.Context.Trim();
                if (seenContexts.Add(contextName))
                {
                    statuses.Add(item.ToDto());
                }
            }

            page++;
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
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return MapTokenFailure<GitHubMergeResultDto>(tokenResult);
        }

        var relativeUri = $"repos/{Escape(owner)}/{Escape(repository)}/pulls/{pullNumber}/merge";
        var payload = new MergePrRequestPayload
        {
            Sha = expectedHeadSha,
            MergeMethod = "merge",
            CommitTitle = commitTitle,
            CommitMessage = commitMessage
        };

        var jsonPayload = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        using var response = await SendAsync(HttpMethod.Put, relativeUri, content, tokenResult.Token, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return await HandleErrorResponseAsync<GitHubMergeResultDto>(response, cancellationToken).ConfigureAwait(false);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var apiDto = JsonSerializer.Deserialize<GitHubApiMergeResultDto>(json, JsonSerializerOptions.Web);

        if (apiDto is null)
        {
            return GitHubPullRequestClientResult<GitHubMergeResultDto>.Failure("GitHub API returned empty merge response.");
        }

        return GitHubPullRequestClientResult<GitHubMergeResultDto>.Success(new GitHubMergeResultDto(apiDto.Sha, apiDto.Merged));
    }

    private static GitHubPullRequestClientResult<T> MapTokenFailure<T>(GitHubTokenResult tokenResult)
    {
        var isConfigErr = tokenResult.FailureKind is GitHubTokenFailureKind.ConfigurationError
            or GitHubTokenFailureKind.Disconnected
            or GitHubTokenFailureKind.InstallationInvalidOrRevoked
            or GitHubTokenFailureKind.RepositoryUnauthorized
            or GitHubTokenFailureKind.PermissionDenied;

        var message = tokenResult.FailureKind switch
        {
            GitHubTokenFailureKind.Disconnected => "Connect GitHub to create pull requests.",
            GitHubTokenFailureKind.RepositoryUnauthorized => "DevPilot does not have access to this repository. Please update repository access in GitHub App settings.",
            GitHubTokenFailureKind.InstallationInvalidOrRevoked => "GitHub connection needs attention. Please reconnect GitHub.",
            GitHubTokenFailureKind.ConfigurationError => tokenResult.ErrorMessage ?? "GitHub App credentials are not configured.",
            GitHubTokenFailureKind.RateLimited => "GitHub API rate limit exceeded. Please try again later.",
            _ => tokenResult.ErrorMessage ?? "GitHub API authentication failed."
        };

        return GitHubPullRequestClientResult<T>.Failure(message, isConfigurationError: isConfigErr, isRateLimit: tokenResult.FailureKind == GitHubTokenFailureKind.RateLimited);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUri,
        HttpContent? content,
        string token,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var uri = BuildUri(relativeUri);

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (content != null)
        {
            request.Content = content;
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubPullRequestClientResult<T>> HandleErrorResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;
        var rateLimitRemaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            ? remaining?.FirstOrDefault()
            : null;

        var rawErr = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var sanitizedErr = GitRemoteUrlNormalizer.SanitizeOutput(rawErr);

        if ((statusCode is 403 or 429) && rateLimitRemaining == "0")
        {
            _logger.LogWarning("GitHub API rate limit exceeded (HTTP {StatusCode}).", statusCode);
            return GitHubPullRequestClientResult<T>.Failure("GitHub API rate limit exceeded. Please try again later.", isRateLimit: true);
        }

        if (statusCode is 401 or 403)
        {
            _logger.LogWarning("GitHub API authentication or permission failed (HTTP {StatusCode}).", statusCode);
            return GitHubPullRequestClientResult<T>.Failure("GitHub API authentication or permission failed.", isConfigurationError: true);
        }

        if (statusCode is 409 or 422)
        {
            _logger.LogWarning("GitHub API returned validation conflict (HTTP {StatusCode}): {Error}", statusCode, sanitizedErr);
            return GitHubPullRequestClientResult<T>.Failure($"GitHub pull request operation returned a conflict: {sanitizedErr}", isConflict: true);
        }

        _logger.LogWarning("GitHub API request failed (HTTP {StatusCode}): {Error}", statusCode, sanitizedErr);
        return GitHubPullRequestClientResult<T>.Failure($"GitHub API returned status code HTTP {statusCode}.");
    }

    private Uri BuildUri(string relativeUri)
    {
        var baseUrl = _options.Value.BaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/{relativeUri}", UriKind.Absolute);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed class CreatePrRequestPayload
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("head")]
        public string Head { get; set; } = string.Empty;

        [JsonPropertyName("base")]
        public string Base { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;
    }

    private sealed class GitHubApiPullRequestDto
    {
        public int Number { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public bool Merged { get; set; }

        [JsonPropertyName("closed_at")]
        public DateTime? ClosedAt { get; set; }

        [JsonPropertyName("merged_at")]
        public DateTime? MergedAt { get; set; }

        public GitHubApiRefDto Head { get; set; } = new();

        public GitHubApiRefDto Base { get; set; } = new();

        public string Body { get; set; } = string.Empty;

        public GitHubPullRequestDto ToDto() =>
            new(
                Number: Number,
                HtmlUrl: HtmlUrl,
                State: State,
                Merged: Merged || MergedAt.HasValue,
                ClosedAt: ClosedAt,
                MergedAt: MergedAt,
                HeadRef: Head.Ref,
                HeadSha: Head.Sha,
                HeadRepoOwner: Head.Repo?.Owner?.Login ?? string.Empty,
                HeadRepoName: Head.Repo?.Name ?? string.Empty,
                BaseRef: Base.Ref,
                BaseRepoOwner: Base.Repo?.Owner?.Login ?? string.Empty,
                BaseRepoName: Base.Repo?.Name ?? string.Empty,
                Body: Body ?? string.Empty);
    }

    private sealed class GitHubApiCheckRunsListDto
    {
        [JsonPropertyName("check_runs")]
        public List<GitHubApiCheckRunDto>? CheckRuns { get; set; }
    }

    private sealed class GitHubApiCheckRunDto
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? Conclusion { get; set; }

        [JsonPropertyName("started_at")]
        public DateTime? StartedAt { get; set; }

        [JsonPropertyName("completed_at")]
        public DateTime? CompletedAt { get; set; }

        public GitHubApiAppDto? App { get; set; }

        public GitHubCheckRunDto ToDto() =>
            new(
                Id: Id,
                Name: Name ?? string.Empty,
                Status: Status ?? string.Empty,
                Conclusion: Conclusion,
                StartedAt: StartedAt,
                CompletedAt: CompletedAt,
                AppName: App?.Name ?? "GitHub Actions");
    }

    private sealed class GitHubApiAppDto
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class GitHubApiCommitStatusDto
    {
        public long Id { get; set; }

        public string Context { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public string? Description { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime? UpdatedAt { get; set; }

        public GitHubCommitStatusDto ToDto() =>
            new(
                Id: Id,
                Context: string.IsNullOrWhiteSpace(Context) ? "default" : Context,
                State: State ?? string.Empty,
                Description: Description,
                CreatedAt: CreatedAt,
                UpdatedAt: UpdatedAt);
    }

    private sealed class GitHubApiRefDto
    {
        public string Ref { get; set; } = string.Empty;

        public string Sha { get; set; } = string.Empty;

        public GitHubApiRepoDto? Repo { get; set; }
    }

    private sealed class GitHubApiRepoDto
    {
        public string Name { get; set; } = string.Empty;

        public GitHubApiOwnerDto? Owner { get; set; }
    }

    private sealed class GitHubApiOwnerDto
    {
        public string Login { get; set; } = string.Empty;
    }

    private sealed class GitHubApiBranchDto
    {
        public GitHubApiCommitDto? Commit { get; set; }
    }

    private sealed class GitHubApiCommitDto
    {
        public string Sha { get; set; } = string.Empty;
    }

    private sealed class MergePrRequestPayload
    {
        [JsonPropertyName("sha")]
        public string Sha { get; set; } = string.Empty;

        [JsonPropertyName("merge_method")]
        public string MergeMethod { get; set; } = "merge";

        [JsonPropertyName("commit_title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CommitTitle { get; set; }

        [JsonPropertyName("commit_message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CommitMessage { get; set; }
    }

    private sealed class GitHubApiMergeResultDto
    {
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("merged")]
        public bool Merged { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
