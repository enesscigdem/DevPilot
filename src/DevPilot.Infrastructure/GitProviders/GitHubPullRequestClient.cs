using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevPilot.Infrastructure.Executions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.GitProviders;

internal sealed class GitHubPullRequestClient : IGitHubPullRequestClient
{
    public const string HttpClientName = "GitHubPullRequest";
    private const string ApiVersion = "2022-11-28";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubPullRequestClient> _logger;
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly bool _isConfigured;

    public GitHubPullRequestClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GitHubPullRequestClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _baseUrl = configuration["GitProvider:GitHub:BaseUrl"] ?? string.Empty;
        _token = configuration["GitProvider:GitHub:Token"]
            ?? configuration["GITHUB_TOKEN"]
            ?? string.Empty;

        _isConfigured = !string.IsNullOrWhiteSpace(_baseUrl) && !string.IsNullOrWhiteSpace(_token);
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
        if (!_isConfigured)
        {
            return GitHubPullRequestClientResult<GitHubPullRequestDto>.Failure(
                "GitHub API credentials or base URL are not configured.",
                isConfigurationError: true);
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

        var response = await SendAsync(HttpMethod.Post, relativeUri, content, cancellationToken).ConfigureAwait(false);

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
        if (!_isConfigured)
        {
            return GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>.Failure(
                "GitHub API credentials or base URL are not configured.",
                isConfigurationError: true);
        }

        var allPrs = new List<GitHubPullRequestDto>();
        var page = 1;
        const int perPage = 100;

        // Head filter in GitHub REST API for same repo is "head=owner:branch" or "head=branch"
        var headQuery = $"{owner}:{head}";

        while (true)
        {
            var relativeUri = $"repos/{Escape(owner)}/{Escape(repository)}/pulls?state=all&head={Escape(headQuery)}&base={Escape(baseBranch)}&per_page={perPage}&page={page}";

            using var response = await SendAsync(HttpMethod.Get, relativeUri, null, cancellationToken).ConfigureAwait(false);

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
        if (!_isConfigured)
        {
            return new GitHubBranchRefResult(false, false, null, "GitHub API credentials or base URL are not configured.");
        }

        var relativeUri = $"repos/{Escape(owner)}/{Escape(repository)}/branches/{Escape(branch)}";

        using var response = await SendAsync(HttpMethod.Get, relativeUri, null, cancellationToken).ConfigureAwait(false);

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

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var uri = BuildUri(relativeUri);

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

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
            return GitHubPullRequestClientResult<T>.Failure("GitHub API authentication or permission failed. Check configured token.", isConfigurationError: true);
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
        var baseUrl = _baseUrl.TrimEnd('/');
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

        public GitHubApiRefDto Head { get; set; } = new();

        public GitHubApiRefDto Base { get; set; } = new();

        public string Body { get; set; } = string.Empty;

        public GitHubPullRequestDto ToDto() =>
            new(
                Number: Number,
                HtmlUrl: HtmlUrl,
                State: State,
                HeadRef: Head.Ref,
                HeadSha: Head.Sha,
                HeadRepoOwner: Head.Repo?.Owner?.Login ?? string.Empty,
                HeadRepoName: Head.Repo?.Name ?? string.Empty,
                BaseRef: Base.Ref,
                BaseRepoOwner: Base.Repo?.Owner?.Login ?? string.Empty,
                BaseRepoName: Base.Repo?.Name ?? string.Empty,
                Body: Body ?? string.Empty);
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
}
