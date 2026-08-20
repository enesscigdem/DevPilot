using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevPilot.Application.GitProviders;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Infrastructure.GitProviders;

internal sealed class GitHubGitProvider : IGitProvider
{
    public const string HttpClientName = "GitHub";
    private const string ApiVersion = "2022-11-28";

    public string ProviderName => GitProviderNames.GitHub;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubAppTokenService _tokenService;
    private readonly IOptions<GitHubAppOptions> _options;
    private readonly DevPilotDbContext _dbContext;
    private readonly ILogger<GitHubGitProvider> _logger;

    public GitHubGitProvider(
        IHttpClientFactory httpClientFactory,
        IGitHubAppTokenService tokenService,
        IOptions<GitHubAppOptions> options,
        DevPilotDbContext dbContext,
        ILogger<GitHubGitProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
        _options = options;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<GitProviderResult<IReadOnlyList<GitRepository>>> GetRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var activeConnections = await _dbContext.GitHubInstallationConnections
            .Where(c => c.Status == GitHubInstallationStatus.Active)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeConnections.Count == 0)
        {
            if (_options.Value.EnableLegacyPatFallback && !string.IsNullOrWhiteSpace(_options.Value.FallbackToken))
            {
                return await GetRepositoriesWithTokenAsync(_options.Value.FallbackToken, "user/repos?per_page=100&sort=updated", cancellationToken).ConfigureAwait(false);
            }

            return GitProviderResult<IReadOnlyList<GitRepository>>.Success(Array.Empty<GitRepository>());
        }

        var allRepos = new List<GitRepository>();
        var seenFullNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var conn in activeConnections)
        {
            var tokenResult = await _tokenService.GetInstallationTokenAsync(conn.ExternalInstallationId, null, cancellationToken).ConfigureAwait(false);
            if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
            {
                _logger.LogWarning("Failed to resolve installation token for installation {InstallationId}: {Error}", conn.ExternalInstallationId, tokenResult.ErrorMessage);
                continue;
            }

            var page = 1;
            const int perPage = 100;
            while (page <= 10)
            {
                var uri = $"installation/repositories?per_page={perPage}&page={page}";
                var pageResult = await GetInstallationRepositoriesPageAsync(tokenResult.Token, uri, cancellationToken).ConfigureAwait(false);

                if (!pageResult.IsSuccess || pageResult.Data == null || pageResult.Data.Count == 0)
                {
                    break;
                }

                foreach (var repo in pageResult.Data)
                {
                    if (seenFullNames.Add(repo.FullName))
                    {
                        allRepos.Add(repo);
                    }
                }

                if (pageResult.Data.Count < perPage)
                {
                    break;
                }

                page++;
            }
        }

        return GitProviderResult<IReadOnlyList<GitRepository>>.Success(allRepos);
    }

    public async Task<GitProviderResult<GitRepository>> GetRepositoryAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return GitProviderResult<GitRepository>.Failure(tokenResult.ErrorMessage ?? "Repository authorization failed.");
        }

        var uri = $"repos/{Escape(owner)}/{Escape(repository)}";
        return await GetSingleWithTokenAsync<GitHubRepositoryDto, GitRepository>(
            tokenResult.Token,
            uri,
            dto => dto.ToRepository(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitProviderResult<IReadOnlyList<GitBranch>>> GetBranchesAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return GitProviderResult<IReadOnlyList<GitBranch>>.Failure(tokenResult.ErrorMessage ?? "Repository authorization failed.");
        }

        var uri = $"repos/{Escape(owner)}/{Escape(repository)}/branches?per_page=100";
        return await GetListWithTokenAsync<GitHubBranchDto, GitBranch>(
            tokenResult.Token,
            uri,
            dto => dto.ToBranch(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitProviderResult<string>> GetDefaultBranchAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        var result = await GetRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? GitProviderResult<string>.Success(result.Data?.DefaultBranch ?? string.Empty)
            : GitProviderResult<string>.Failure(result.ErrorMessage ?? "Repository details could not be retrieved.");
    }

    private async Task<GitProviderResult<IReadOnlyList<GitRepository>>> GetInstallationRepositoriesPageAsync(
        string token,
        string relativeUri,
        CancellationToken cancellationToken)
    {
        var response = await GetWithTokenAsync<InstallationRepositoriesWrapperDto>(token, relativeUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return GitProviderResult<IReadOnlyList<GitRepository>>.Failure(response.ErrorMessage ?? "Failed to retrieve installation repositories.");
        }

        var list = response.Data?.Repositories?.Select(r => r.ToRepository()).ToList() ?? new List<GitRepository>();
        return GitProviderResult<IReadOnlyList<GitRepository>>.Success(list);
    }

    private async Task<GitProviderResult<IReadOnlyList<GitRepository>>> GetRepositoriesWithTokenAsync(
        string token,
        string relativeUri,
        CancellationToken cancellationToken)
    {
        return await GetListWithTokenAsync<GitHubRepositoryDto, GitRepository>(
            token,
            relativeUri,
            dto => dto.ToRepository(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitProviderResult<IReadOnlyList<TModel>>> GetListWithTokenAsync<TDto, TModel>(
        string token,
        string relativeUri,
        Func<TDto, TModel> map,
        CancellationToken cancellationToken)
    {
        var result = await GetWithTokenAsync<List<TDto>>(token, relativeUri, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return GitProviderResult<IReadOnlyList<TModel>>.Failure(result.ErrorMessage ?? "Request failed.");
        }

        if (result.Data is null)
        {
            return GitProviderResult<IReadOnlyList<TModel>>.Success(Array.Empty<TModel>());
        }

        var list = result.Data.Select(map).ToList();
        return GitProviderResult<IReadOnlyList<TModel>>.Success(list);
    }

    private async Task<GitProviderResult<TModel>> GetSingleWithTokenAsync<TDto, TModel>(
        string token,
        string relativeUri,
        Func<TDto, TModel> map,
        CancellationToken cancellationToken)
    {
        var result = await GetWithTokenAsync<TDto>(token, relativeUri, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return GitProviderResult<TModel>.Failure(result.ErrorMessage ?? "Request failed.");
        }

        if (result.Data is null)
        {
            return GitProviderResult<TModel>.Failure("Response payload was empty.");
        }

        return GitProviderResult<TModel>.Success(map(result.Data));
    }

    private async Task<GitProviderResult<T>> GetWithTokenAsync<T>(
        string token,
        string relativeUri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            var uri = BuildUri(relativeUri);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                if (statusCode == 404)
                {
                    return GitProviderResult<T>.Failure("The requested GitHub resource was not found.");
                }
                if (statusCode is 403 or 429)
                {
                    return GitProviderResult<T>.Failure("GitHub API rate limit or permission constraint encountered.");
                }
                return GitProviderResult<T>.Failure($"GitHub API returned status HTTP {statusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web);

            if (dto is null)
            {
                return GitProviderResult<T>.Failure("GitHub API returned an empty response body.");
            }

            return GitProviderResult<T>.Success(dto);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return GitProviderResult<T>.Failure("The request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during GitHub API call to {Uri}.", relativeUri);
            return GitProviderResult<T>.Failure($"GitHub API communication error: {ex.Message}");
        }
    }

    private Uri BuildUri(string relativeUri)
    {
        var baseUrl = _options.Value.BaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/{relativeUri}", UriKind.Absolute);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed class InstallationRepositoriesWrapperDto
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("repositories")]
        public List<GitHubRepositoryDto>? Repositories { get; set; }
    }

    private sealed class GitHubRepositoryDto
    {
        public long Id { get; set; }

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
