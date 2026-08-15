using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevPilot.Application.GitProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.GitProviders;

internal sealed class GitHubGitProvider : IGitProvider
{
    public const string HttpClientName = "GitHub";

    public string ProviderName => GitProviderNames.GitHub;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubGitProvider> _logger;
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly bool _isConfigured;

    public GitHubGitProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GitHubGitProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _baseUrl = configuration["GitProvider:GitHub:BaseUrl"] ?? string.Empty;
        _token = configuration["GitProvider:GitHub:Token"]
            ?? configuration["GITHUB_TOKEN"]
            ?? string.Empty;

        _isConfigured =
            !string.IsNullOrWhiteSpace(_token) &&
            !string.IsNullOrWhiteSpace(_baseUrl);
    }

    public Task<GitProviderResult<IReadOnlyList<GitRepository>>> GetRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        return GetListAsync<GitHubRepositoryDto, GitRepository>(
            "user/repos?per_page=100&sort=updated",
            dto => dto.ToRepository(),
            cancellationToken);
    }

    public Task<GitProviderResult<GitRepository>> GetRepositoryAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        return GetSingleAsync<GitHubRepositoryDto, GitRepository>(
            $"repos/{Escape(owner)}/{Escape(repository)}",
            dto => dto.ToRepository(),
            cancellationToken);
    }

    public Task<GitProviderResult<IReadOnlyList<GitBranch>>> GetBranchesAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        return GetListAsync<GitHubBranchDto, GitBranch>(
            $"repos/{Escape(owner)}/{Escape(repository)}/branches?per_page=100",
            dto => dto.ToBranch(),
            cancellationToken);
    }

    public async Task<GitProviderResult<string>> GetDefaultBranchAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        var result = await GetRepositoryAsync(owner, repository, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? GitProviderResult<string>.Success(result.Data?.DefaultBranch ?? string.Empty)
            : GitProviderResult<string>.Failure(result.ErrorMessage ?? "Repository details could not be retrieved.");
    }

    private async Task<GitProviderResult<IReadOnlyList<TModel>>> GetListAsync<TDto, TModel>(
        string relativeUri,
        Func<TDto, TModel> map,
        CancellationToken cancellationToken)
    {
        var result = await GetAsync<List<TDto>>(
            relativeUri,
            cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return GitProviderResult<IReadOnlyList<TModel>>.Failure(
                result.ErrorMessage ?? "Request failed.");
        }

        if (result.Data is null)
        {
            return GitProviderResult<IReadOnlyList<TModel>>.Success(Array.Empty<TModel>());
        }

        var list = result.Data.Select(map).ToList();
        return GitProviderResult<IReadOnlyList<TModel>>.Success(list);
    }

    private async Task<GitProviderResult<TModel>> GetSingleAsync<TDto, TModel>(
        string relativeUri,
        Func<TDto, TModel> map,
        CancellationToken cancellationToken)
    {
        var result = await GetAsync<TDto>(
            relativeUri,
            cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return GitProviderResult<TModel>.Failure(
                result.ErrorMessage ?? "Request failed.");
        }

        if (result.Data is null)
        {
            return GitProviderResult<TModel>.Failure("Response payload was empty.");
        }

        return GitProviderResult<TModel>.Success(map(result.Data));
    }

    private async Task<GitProviderResult<T>> GetAsync<T>(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        if (!_isConfigured)
        {
            _logger.LogWarning(
                "GitHub provider is not configured. Ensure GitProvider:GitHub:BaseUrl " +
                "and the token (GitProvider:GitHub:Token or GITHUB_TOKEN environment variable) are set.");

            return GitProviderResult<T>.Failure(
                "GitHub provider is not configured. The base URL or token is missing.");
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(relativeUri));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await HandleErrorResponseAsync<T>(response)
                    .ConfigureAwait(false);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            var dto = JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web);

            if (dto is null)
            {
                return GitProviderResult<T>.Failure("GitHub API returned an empty response body.");
            }

            return GitProviderResult<T>.Success(dto);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("The GitHub API request was cancelled.");
            return GitProviderResult<T>.Failure("The request was cancelled.");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "The request to the GitHub API timed out.");
            return GitProviderResult<T>.Failure("The request to the GitHub API timed out.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "An HTTP error occurred while calling the GitHub API.");
            return GitProviderResult<T>.Failure("An HTTP error occurred while calling the GitHub API.");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Failed to parse the GitHub API response.");
            return GitProviderResult<T>.Failure("Failed to parse the GitHub API response.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "An unexpected error occurred while calling the GitHub API.");
            return GitProviderResult<T>.Failure("An unexpected error occurred while calling the GitHub API.");
        }
    }

    private async Task<GitProviderResult<T>> HandleErrorResponseAsync<T>(HttpResponseMessage response)
    {
        var statusCode = (int)response.StatusCode;
        var rateLimitRemaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            ? remaining?.FirstOrDefault()
            : null;

        if (statusCode is 403 or 429 && rateLimitRemaining == "0")
        {
            _logger.LogWarning(
                "GitHub API rate limit exceeded (HTTP {StatusCode}).",
                statusCode);

            return GitProviderResult<T>.Failure(
                "GitHub API rate limit exceeded. Please try again later.");
        }

        if (statusCode == 404)
        {
            _logger.LogWarning(
                "GitHub resource was not found (HTTP {StatusCode}).",
                statusCode);

            return GitProviderResult<T>.Failure(
                "The requested GitHub resource was not found.");
        }

        _logger.LogWarning(
            "GitHub API returned a non-success status code (HTTP {StatusCode}).",
            statusCode);

        // Drain the response body to avoid leaking connections.
        _ = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return GitProviderResult<T>.Failure(
            "GitHub API returned a non-success status code.");
    }

    private Uri BuildUri(string relativeUri)
    {
        var baseUrl = _baseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/{relativeUri}", UriKind.Absolute);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed class GitHubRepositoryDto
    {
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("full_name")]
        public string FullName { get; set; } = string.Empty;

        public GitHubOwnerDto Owner { get; set; } = new();

        public string? Description { get; set; }

        [JsonPropertyName("private")]
        public bool IsPrivate { get; set; }

        [JsonPropertyName("default_branch")]
        public string DefaultBranch { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        public GitRepository ToRepository()
        {
            return new GitRepository
            {
                Name = Name,
                FullName = FullName,
                Owner = Owner.Login,
                Description = Description,
                IsPrivate = IsPrivate,
                DefaultBranch = DefaultBranch,
                Url = HtmlUrl,
            };
        }
    }

    private sealed class GitHubOwnerDto
    {
        public string Login { get; set; } = string.Empty;
    }

    private sealed class GitHubBranchDto
    {
        public string Name { get; set; } = string.Empty;

        public GitHubCommitDto Commit { get; set; } = new();

        public bool Protected { get; set; }

        public GitBranch ToBranch()
        {
            return new GitBranch
            {
                Name = Name,
                CommitSha = Commit.Sha,
                IsProtected = Protected,
            };
        }
    }

    private sealed class GitHubCommitDto
    {
        public string Sha { get; set; } = string.Empty;
    }
}
