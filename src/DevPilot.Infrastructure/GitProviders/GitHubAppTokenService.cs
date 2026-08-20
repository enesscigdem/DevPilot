using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Infrastructure.GitProviders;

public sealed class GitHubAppTokenService : IGitHubAppTokenService
{
    private static readonly TimeSpan CacheSafetyBuffer = TimeSpan.FromMinutes(5);
    private const string ApiVersion = "2022-11-28";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<GitHubAppOptions> _options;
    private readonly DevPilotDbContext _dbContext;
    private readonly ILogger<GitHubAppTokenService> _logger;

    private readonly ConcurrentDictionary<long, CachedToken> _tokenCache = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public GitHubAppTokenService(
        IHttpClientFactory httpClientFactory,
        IOptions<GitHubAppOptions> options,
        DevPilotDbContext dbContext,
        ILogger<GitHubAppTokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _dbContext = dbContext;
        _logger = logger;
    }

    public bool IsConfigured => _options.Value.IsAppConfigured;

    public async Task<GitHubTokenResult> GetInstallationTokenAsync(
        long externalInstallationId,
        string? repositoryName = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        if (_tokenCache.TryGetValue(externalInstallationId, out var cached) && cached.ValidUntilUtc > now)
        {
            return GitHubTokenResult.Success(cached.Token, cached.ExpiresAt, externalInstallationId);
        }

        if (!IsConfigured)
        {
            if (_options.Value.EnableLegacyPatFallback && !string.IsNullOrWhiteSpace(_options.Value.FallbackToken))
            {
                return GitHubTokenResult.Success(_options.Value.FallbackToken, now.AddHours(1), externalInstallationId);
            }

            return GitHubTokenResult.Failure(
                GitHubTokenFailureKind.ConfigurationError,
                "GitHub App credentials (AppId and PrivateKey) are not configured.");
        }

        var semaphore = _locks.GetOrAdd(externalInstallationId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokenCache.TryGetValue(externalInstallationId, out cached) && cached.ValidUntilUtc > DateTimeOffset.UtcNow)
            {
                return GitHubTokenResult.Success(cached.Token, cached.ExpiresAt, externalInstallationId);
            }

            string appJwt;
            try
            {
                appJwt = CreateAppJwt();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate GitHub App JWT.");
                return GitHubTokenResult.Failure(
                    GitHubTokenFailureKind.ConfigurationError,
                    $"Failed to generate GitHub App JWT: {ex.Message}");
            }

            var baseUrl = _options.Value.BaseUrl.TrimEnd('/');
            var tokenEndpoint = $"{baseUrl}/app/installations/{externalInstallationId}/access_tokens";

            using var client = _httpClientFactory.CreateClient("GitHub");
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);

            if (!string.IsNullOrWhiteSpace(repositoryName))
            {
                var payload = new { repositories = new[] { repositoryName } };
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;

            if (statusCode == 401)
            {
                _logger.LogWarning("GitHub App authentication failed (HTTP 401). Check AppId and PrivateKey configuration.");
                return GitHubTokenResult.Failure(
                    GitHubTokenFailureKind.ConfigurationError,
                    "GitHub App authentication failed (HTTP 401). Please verify your AppId and Private Key.",
                    externalInstallationId);
            }

            if (statusCode == 404)
            {
                _logger.LogWarning("GitHub installation {InstallationId} was not found or has been revoked (HTTP 404).", externalInstallationId);
                await MarkInstallationInvalidAsync(externalInstallationId, cancellationToken).ConfigureAwait(false);
                return GitHubTokenResult.Failure(
                    GitHubTokenFailureKind.InstallationInvalidOrRevoked,
                    $"GitHub installation {externalInstallationId} was not found or was uninstalled.",
                    externalInstallationId);
            }

            if (statusCode == 403)
            {
                _logger.LogWarning("GitHub installation {InstallationId} token creation forbidden (HTTP 403).", externalInstallationId);
                return GitHubTokenResult.Failure(
                    GitHubTokenFailureKind.PermissionDenied,
                    "GitHub denied token creation for this installation.",
                    externalInstallationId);
            }

            if (statusCode is 429 or 403 && response.Headers.Contains("X-RateLimit-Remaining") &&
                response.Headers.GetValues("X-RateLimit-Remaining").FirstOrDefault() == "0")
            {
                return GitHubTokenResult.Failure(
                    GitHubTokenFailureKind.RateLimited,
                    "GitHub API rate limit exceeded. Please try again later.",
                    externalInstallationId);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var sanitized = GitRemoteUrlNormalizer.SanitizeOutput(errContent);
                _logger.LogWarning("Failed to exchange installation token for {InstallationId} (HTTP {StatusCode}): {Error}", externalInstallationId, statusCode, sanitized);
                return GitHubTokenResult.Failure(
                    statusCode >= 500 ? GitHubTokenFailureKind.TransientError : GitHubTokenFailureKind.ConfigurationError,
                    $"GitHub API returned HTTP {statusCode}.",
                    externalInstallationId);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var tokenResponse = JsonSerializer.Deserialize<InstallationTokenResponse>(json, JsonSerializerOptions.Web);

            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.Token))
            {
                return GitHubTokenResult.Failure(
                    GitHubTokenFailureKind.TransientError,
                    "GitHub returned an empty installation token response.",
                    externalInstallationId);
            }

            var expiresAt = tokenResponse.ExpiresAt > DateTimeOffset.UtcNow
                ? tokenResponse.ExpiresAt
                : DateTimeOffset.UtcNow.AddHours(1);

            var validUntil = expiresAt > DateTimeOffset.UtcNow.Add(CacheSafetyBuffer)
                ? expiresAt.Subtract(CacheSafetyBuffer)
                : expiresAt;

            _tokenCache[externalInstallationId] = new CachedToken(tokenResponse.Token, expiresAt, validUntil);

            // Record verification timestamp on DB connection if present
            await RecordInstallationVerifiedAsync(externalInstallationId, cancellationToken).ConfigureAwait(false);

            return GitHubTokenResult.Success(tokenResponse.Token, expiresAt, externalInstallationId);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<GitHubTokenResult> GetTokenForRepositoryAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            return GitHubTokenResult.Failure(
                GitHubTokenFailureKind.RepositoryUnauthorized,
                "Owner and repository name are required.");
        }

        // 1. Locate matching installation in DB
        var connection = await _dbContext.GitHubInstallationConnections
            .Where(c => c.Status == GitHubInstallationStatus.Active &&
                        (c.AccountLogin.ToLower() == owner.ToLower() ||
                         c.Workspaces.Any(w => w.Owner.ToLower() == owner.ToLower() && w.Repository.ToLower() == repository.ToLower())))
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (connection == null)
        {
            // If legacy fallback enabled
            if (_options.Value.EnableLegacyPatFallback && !string.IsNullOrWhiteSpace(_options.Value.FallbackToken))
            {
                return GitHubTokenResult.Success(_options.Value.FallbackToken, DateTimeOffset.UtcNow.AddHours(1), 0);
            }

            return GitHubTokenResult.Failure(
                GitHubTokenFailureKind.Disconnected,
                $"No active GitHub App connection found for repository '{owner}/{repository}'.");
        }

        // 2. Obtain installation token
        var tokenResult = await GetInstallationTokenAsync(connection.ExternalInstallationId, null, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess)
        {
            return tokenResult;
        }

        // 3. Verify repository accessibility
        var isAccessible = await VerifyRepositoryAccessAsync(
            owner,
            repository,
            tokenResult.Token!,
            cancellationToken).ConfigureAwait(false);

        if (!isAccessible)
        {
            _logger.LogWarning("Repository '{Owner}/{Repo}' is not accessible under installation {InstallationId}.", owner, repository, connection.ExternalInstallationId);
            return GitHubTokenResult.Failure(
                GitHubTokenFailureKind.RepositoryUnauthorized,
                $"DevPilot does not have access to repository '{owner}/{repository}'. Please update repository permissions in GitHub App settings.",
                connection.ExternalInstallationId);
        }

        return tokenResult;
    }

    public async Task<GitHubTokenResult> GetTokenForWorkspaceAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        if (workspace == null)
        {
            return GitHubTokenResult.Failure(GitHubTokenFailureKind.Disconnected, "Workspace is null.");
        }

        if (workspace.GitHubInstallationConnectionId.HasValue)
        {
            var connection = await _dbContext.GitHubInstallationConnections
                .FirstOrDefaultAsync(c => c.Id == workspace.GitHubInstallationConnectionId.Value, cancellationToken)
                .ConfigureAwait(false);

            if (connection != null && connection.Status == GitHubInstallationStatus.Active)
            {
                var tokenResult = await GetInstallationTokenAsync(connection.ExternalInstallationId, null, cancellationToken).ConfigureAwait(false);
                if (tokenResult.IsSuccess)
                {
                    var isAccessible = await VerifyRepositoryAccessAsync(workspace.Owner, workspace.Repository, tokenResult.Token!, cancellationToken).ConfigureAwait(false);
                    if (isAccessible)
                    {
                        return tokenResult;
                    }

                    return GitHubTokenResult.Failure(
                        GitHubTokenFailureKind.RepositoryUnauthorized,
                        $"DevPilot does not have access to repository '{workspace.Owner}/{workspace.Repository}'.",
                        connection.ExternalInstallationId);
                }

                if (tokenResult.FailureKind is GitHubTokenFailureKind.InstallationInvalidOrRevoked)
                {
                    return tokenResult;
                }
            }
        }

        return await GetTokenForRepositoryAsync(workspace.Owner, workspace.Repository, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubUserInstallationVerificationResult> VerifyUserInstallationOwnershipAsync(
        string oauthCode,
        long expectedInstallationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(oauthCode))
        {
            return new GitHubUserInstallationVerificationResult(false, null, "OAuth code is required.");
        }

        if (!_options.Value.IsOAuthConfigured)
        {
            return new GitHubUserInstallationVerificationResult(false, null, "GitHub OAuth ClientId or ClientSecret is not configured.");
        }

        // 1. Exchange OAuth code for User Access Token
        using var client = _httpClientFactory.CreateClient("GitHub");
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        tokenReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.Value.ClientId!,
            ["client_secret"] = _options.Value.ClientSecret!,
            ["code"] = oauthCode
        });

        using var tokenRes = await client.SendAsync(tokenReq, cancellationToken).ConfigureAwait(false);
        if (!tokenRes.IsSuccessStatusCode)
        {
            return new GitHubUserInstallationVerificationResult(false, null, "Failed to exchange OAuth code for user access token.");
        }

        var tokenJson = await tokenRes.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var userTokenDto = JsonSerializer.Deserialize<OAuthTokenResponse>(tokenJson, JsonSerializerOptions.Web);

        if (userTokenDto == null || string.IsNullOrWhiteSpace(userTokenDto.AccessToken))
        {
            return new GitHubUserInstallationVerificationResult(false, null, userTokenDto?.ErrorDescription ?? "GitHub OAuth token response was empty.");
        }

        var userAccessToken = userTokenDto.AccessToken;

        try
        {
            // 2. Query authenticated user's installations with pagination
            var page = 1;
            const int perPage = 100;
            const int maxPages = 10;
            GitHubUserInstallationInfo? matchedInstallation = null;

            while (page <= maxPages)
            {
                var baseUrl = _options.Value.BaseUrl.TrimEnd('/');
                var instUrl = $"{baseUrl}/user/installations?per_page={perPage}&page={page}";

                using var instReq = new HttpRequestMessage(HttpMethod.Get, instUrl);
                instReq.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
                instReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                instReq.Headers.Add("X-GitHub-Api-Version", ApiVersion);
                instReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userAccessToken);

                using var instRes = await client.SendAsync(instReq, cancellationToken).ConfigureAwait(false);
                if (!instRes.IsSuccessStatusCode)
                {
                    var err = await instRes.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning("Failed to query user installations (HTTP {StatusCode}): {Error}", instRes.StatusCode, GitRemoteUrlNormalizer.SanitizeOutput(err));
                    return new GitHubUserInstallationVerificationResult(false, null, "Failed to verify user installations against GitHub API.");
                }

                var instJson = await instRes.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var instWrapper = JsonSerializer.Deserialize<UserInstallationsListResponse>(instJson, JsonSerializerOptions.Web);

                if (instWrapper?.Installations != null)
                {
                    foreach (var inst in instWrapper.Installations)
                    {
                        if (inst.Id == expectedInstallationId)
                        {
                            matchedInstallation = new GitHubUserInstallationInfo(
                                ExternalInstallationId: inst.Id,
                                AccountLogin: inst.Account?.Login ?? "unknown",
                                AccountType: inst.Account?.Type ?? "User",
                                TargetId: inst.TargetId,
                                TargetAvatarUrl: inst.Account?.AvatarUrl);
                            break;
                        }
                    }
                }

                if (matchedInstallation != null || instWrapper?.Installations == null || instWrapper.Installations.Count < perPage)
                {
                    break;
                }

                page++;
            }

            if (matchedInstallation == null)
            {
                _logger.LogWarning("Ownership verification failed: Installation {InstallationId} was not found in authenticated user's installations.", expectedInstallationId);
                return new GitHubUserInstallationVerificationResult(false, null, "The authenticated GitHub user does not have administrative access to the specified installation.");
            }

            return new GitHubUserInstallationVerificationResult(true, matchedInstallation, null);
        }
        finally
        {
            // User token is discarded immediately from local stack frame
        }
    }

    public void InvalidateToken(long externalInstallationId)
    {
        _tokenCache.TryRemove(externalInstallationId, out _);
    }

    private string CreateAppJwt()
    {
        var pem = _options.Value.PrivateKeyPem;
        if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(_options.Value.PrivateKeyPath) && File.Exists(_options.Value.PrivateKeyPath))
        {
            pem = File.ReadAllText(_options.Value.PrivateKeyPath);
        }

        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new InvalidOperationException("GitHub App Private Key is missing.");
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);

        var now = DateTimeOffset.UtcNow;
        var header = new { alg = "RS256", typ = "JWT" };
        var payload = new
        {
            iss = _options.Value.AppId,
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(10).ToUnixTimeSeconds()
        };

        var headerBase64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadBase64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));

        var unsignedToken = $"{headerBase64}.{payloadBase64}";
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(unsignedToken), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureBase64 = Base64UrlEncode(signature);

        return $"{unsignedToken}.{signatureBase64}";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private async Task<bool> VerifyRepositoryAccessAsync(
        string owner,
        string repository,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = _options.Value.BaseUrl.TrimEnd('/');
            var repoUri = $"{baseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";

            using var client = _httpClientFactory.CreateClient("GitHub");
            using var req = new HttpRequestMessage(HttpMethod.Get, repoUri);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevPilot", "1.0"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.Add("X-GitHub-Api-Version", ApiVersion);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var res = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while verifying repository access for {Owner}/{Repo}.", owner, repository);
            return false;
        }
    }

    private async Task MarkInstallationInvalidAsync(long externalInstallationId, CancellationToken cancellationToken)
    {
        try
        {
            var conn = await _dbContext.GitHubInstallationConnections
                .FirstOrDefaultAsync(c => c.ExternalInstallationId == externalInstallationId, cancellationToken)
                .ConfigureAwait(false);

            if (conn != null)
            {
                conn.Status = GitHubInstallationStatus.Invalid;
                conn.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark installation {InstallationId} invalid in database.", externalInstallationId);
        }
    }

    private async Task RecordInstallationVerifiedAsync(long externalInstallationId, CancellationToken cancellationToken)
    {
        try
        {
            var conn = await _dbContext.GitHubInstallationConnections
                .FirstOrDefaultAsync(c => c.ExternalInstallationId == externalInstallationId, cancellationToken)
                .ConfigureAwait(false);

            if (conn != null)
            {
                conn.LastVerifiedAt = DateTime.UtcNow;
                conn.UpdatedAt = DateTime.UtcNow;
                if (conn.Status == GitHubInstallationStatus.Invalid || conn.Status == GitHubInstallationStatus.Suspended)
                {
                    conn.Status = GitHubInstallationStatus.Active;
                }
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Non-critical background timestamp update
        }
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt, DateTimeOffset ValidUntilUtc);

    private sealed class InstallationTokenResponse
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }

    private sealed class UserInstallationsListResponse
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("installations")]
        public List<GitHubApiInstallationDto>? Installations { get; set; }
    }

    private sealed class GitHubApiInstallationDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("target_id")]
        public long TargetId { get; set; }

        [JsonPropertyName("account")]
        public GitHubApiAccountDto? Account { get; set; }
    }

    private sealed class GitHubApiAccountDto
    {
        [JsonPropertyName("login")]
        public string Login { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "User";

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; set; }
    }
}
