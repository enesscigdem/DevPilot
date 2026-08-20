using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.GitProviders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevPilot.Tests.GitProviders;

public sealed class GitHubAppTokenServiceTests : IDisposable
{
    private readonly DevPilotDbContext _dbContext;
    private readonly string _privateKeyPem;
    private readonly FakeHttpMessageHandler _httpHandler = new();
    private readonly HttpClient _httpClient;

    public GitHubAppTokenServiceTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DevPilotDbContext(options);

        using var rsa = RSA.Create(2048);
        _privateKeyPem = rsa.ExportRSAPrivateKeyPem();

        _httpClient = new HttpClient(_httpHandler);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _httpClient.Dispose();
    }

    private GitHubAppTokenService CreateService(GitHubAppOptions? customOptions = null)
    {
        var opts = customOptions ?? new GitHubAppOptions
        {
            BaseUrl = "https://api.github.com",
            AppId = "123456",
            AppSlug = "devpilot-app",
            ClientId = "Iv1.test_client_id",
            ClientSecret = "test_client_secret",
            PrivateKeyPem = _privateKeyPem,
        };

        var factory = new StubHttpClientFactory(_httpClient);
        return new GitHubAppTokenService(factory, Options.Create(opts), _dbContext, NullLogger<GitHubAppTokenService>.Instance);
    }

    [Fact]
    public async Task GetInstallationToken_ValidResponse_CachesTokenUsingActualExpiresAt()
    {
        // Arrange
        var service = CreateService();
        var installationId = 987654L;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(45);

        _httpHandler.ResponseFactory = req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains($"/app/installations/{installationId}/access_tokens"))
            {
                var payload = new { token = "ghs_valid_installation_token_xyz", expires_at = expiresAt };
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        // Act - First call: fetches from GitHub
        var result1 = await service.GetInstallationTokenAsync(installationId);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result1.Token.Should().Be("ghs_valid_installation_token_xyz");
        _httpHandler.CallCount.Should().Be(1);

        // Act - Second call: cached in-memory
        var result2 = await service.GetInstallationTokenAsync(installationId);
        result2.IsSuccess.Should().BeTrue();
        result2.Token.Should().Be("ghs_valid_installation_token_xyz");
        _httpHandler.CallCount.Should().Be(1, "Should reuse cached token before expiry");
    }

    [Fact]
    public async Task GetInstallationToken_LongOpaqueTokenFormat_AcceptedAndCached()
    {
        // Arrange
        var service = CreateService();
        var installationId = 777888L;
        // Generate a 256-character variable-length token string
        var longToken = "ghs_" + new string('A', 252);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(50);

        _httpHandler.ResponseFactory = _ =>
        {
            var payload = new { token = longToken, expires_at = expiresAt };
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload))
            };
        };

        // Act
        var result = await service.GetInstallationTokenAsync(installationId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Token.Should().Be(longToken);
        result.Token!.Length.Should().Be(256);
    }

    [Fact]
    public async Task GetInstallationToken_ConcurrentCalls_AreSingleFlight()
    {
        // Arrange
        var service = CreateService();
        var installationId = 112233L;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(50);

        _httpHandler.AsyncResponseFactory = async _ =>
        {
            await Task.Delay(50);
            var payload = new { token = "ghs_single_flight_token", expires_at = expiresAt };
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload))
            };
        };

        // Act: Fire 10 concurrent requests
        var tasks = Enumerable.Range(0, 10).Select(_ => service.GetInstallationTokenAsync(installationId));
        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().AllSatisfy(r =>
        {
            r.IsSuccess.Should().BeTrue();
            r.Token.Should().Be("ghs_single_flight_token");
        });
        _httpHandler.CallCount.Should().Be(1, "Concurrent requests should coalesce via single-flight lock");
    }

    [Fact]
    public async Task GetInstallationToken_AppAuth401_ClassifiedAsConfigurationErrorAndDoesNotInvalidateInstallation()
    {
        // Arrange
        var service = CreateService();
        var installationId = 555444L;

        _dbContext.GitHubInstallationConnections.Add(new GitHubInstallationConnection
        {
            Id = Guid.NewGuid(),
            ExternalInstallationId = installationId,
            AccountLogin = "testorg",
            Status = GitHubInstallationStatus.Active,
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        _httpHandler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        // Act
        var result = await service.GetInstallationTokenAsync(installationId);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(GitHubTokenFailureKind.ConfigurationError);

        var conn = await _dbContext.GitHubInstallationConnections.FirstAsync(c => c.ExternalInstallationId == installationId);
        conn.Status.Should().Be(GitHubInstallationStatus.Active, "App 401 is an App config error, not a user installation revocation");
    }

    [Fact]
    public async Task GetInstallationToken_Installation404_ClassifiedAsInvalidOrRevokedAndMarksDatabaseStatus()
    {
        // Arrange
        var service = CreateService();
        var installationId = 999111L;

        _dbContext.GitHubInstallationConnections.Add(new GitHubInstallationConnection
        {
            Id = Guid.NewGuid(),
            ExternalInstallationId = installationId,
            AccountLogin = "revokedorg",
            Status = GitHubInstallationStatus.Active,
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        _httpHandler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        // Act
        var result = await service.GetInstallationTokenAsync(installationId);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(GitHubTokenFailureKind.InstallationInvalidOrRevoked);

        var conn = await _dbContext.GitHubInstallationConnections.FirstAsync(c => c.ExternalInstallationId == installationId);
        conn.Status.Should().Be(GitHubInstallationStatus.Invalid, "Installation 404 marks installation invalid in DB");
    }

    [Fact]
    public async Task GetTokenForRepository_RepoNotFound404_ClassifiedAsRepositoryUnauthorizedWithoutInvalidatingInstallation()
    {
        // Arrange
        var service = CreateService();
        var installationId = 444333L;

        _dbContext.GitHubInstallationConnections.Add(new GitHubInstallationConnection
        {
            Id = Guid.NewGuid(),
            ExternalInstallationId = installationId,
            AccountLogin = "myorg",
            Status = GitHubInstallationStatus.Active,
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        _httpHandler.ResponseFactory = req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/access_tokens"))
            {
                var payload = new { token = "ghs_temp_token", expires_at = DateTimeOffset.UtcNow.AddHours(1) };
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload))
                };
            }
            if (req.RequestUri.AbsolutePath.Contains("/repos/myorg/unauthorized-repo"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        // Act
        var result = await service.GetTokenForRepositoryAsync("myorg", "unauthorized-repo");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(GitHubTokenFailureKind.RepositoryUnauthorized);

        var conn = await _dbContext.GitHubInstallationConnections.FirstAsync(c => c.ExternalInstallationId == installationId);
        conn.Status.Should().Be(GitHubInstallationStatus.Active, "Repo 404 must NOT invalidate the entire installation");
    }

    [Fact]
    public async Task VerifyUserInstallationOwnership_MatchingInstallation_Succeeds()
    {
        // Arrange
        var service = CreateService();
        var expectedId = 12345678L;

        _httpHandler.ResponseFactory = req =>
        {
            if (req.RequestUri!.Host == "github.com" && req.RequestUri.AbsolutePath.Contains("/access_token"))
            {
                var tokenPayload = new { access_token = "gho_user_oauth_token_abc" };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(tokenPayload))
                };
            }
            if (req.RequestUri.AbsolutePath.Contains("/user/installations"))
            {
                var instPayload = new
                {
                    total_count = 1,
                    installations = new[]
                    {
                        new
                        {
                            id = expectedId,
                            target_id = 98765L,
                            account = new { login = "enesscigdem", type = "User", avatar_url = "https://avatar.test" }
                        }
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(instPayload))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        // Act
        var result = await service.VerifyUserInstallationOwnershipAsync("valid_oauth_code", expectedId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Installation.Should().NotBeNull();
        result.Installation!.ExternalInstallationId.Should().Be(expectedId);
        result.Installation.AccountLogin.Should().Be("enesscigdem");
    }

    [Fact]
    public async Task VerifyUserInstallationOwnership_UnownedInstallation_FailsOwnershipCheck()
    {
        // Arrange
        var service = CreateService();
        var unownedId = 99999999L;

        _httpHandler.ResponseFactory = req =>
        {
            if (req.RequestUri!.Host == "github.com" && req.RequestUri.AbsolutePath.Contains("/access_token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { access_token = "gho_user_token" }))
                };
            }
            if (req.RequestUri.AbsolutePath.Contains("/user/installations"))
            {
                var instPayload = new
                {
                    total_count = 1,
                    installations = new[]
                    {
                        new { id = 12345678L, target_id = 1L, account = new { login = "other_user", type = "User" } }
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(instPayload))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        // Act
        var result = await service.VerifyUserInstallationOwnershipAsync("valid_code", unownedId);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("administrative access");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StubHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? AsyncResponseFactory { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (AsyncResponseFactory != null)
            {
                return await AsyncResponseFactory(request);
            }
            return ResponseFactory?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
