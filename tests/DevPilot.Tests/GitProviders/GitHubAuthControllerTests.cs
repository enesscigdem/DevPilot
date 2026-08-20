using DevPilot.Api.Controllers;
using DevPilot.Application.GitProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.GitProviders;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevPilot.Tests.GitProviders;

public sealed class GitHubAuthControllerTests : IDisposable
{
    private readonly DevPilotDbContext _dbContext;
    private readonly GitHubOAuthStateService _stateService;
    private readonly FakeGitHubAppTokenService _tokenService;
    private readonly FakeGitProvider _gitProvider;
    private readonly IOptions<GitHubAppOptions> _options;

    private readonly IOptions<FrontendOptions> _frontendOptions;

    public GitHubAuthControllerTests()
    {
        var dbOptions = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DevPilotDbContext(dbOptions);

        _stateService = new GitHubOAuthStateService(NullLogger<GitHubOAuthStateService>.Instance);
        _tokenService = new FakeGitHubAppTokenService();
        _gitProvider = new FakeGitProvider();
        _options = Options.Create(new GitHubAppOptions
        {
            AppId = "123456",
            AppSlug = "devpilot-app",
            ClientId = "Iv1.test_client",
            ClientSecret = "test_secret",
            PrivateKeyPem = "-----BEGIN RSA PRIVATE KEY-----\ntest\n-----END RSA PRIVATE KEY-----"
        });
        _frontendOptions = Options.Create(new FrontendOptions
        {
            BaseUrl = "http://localhost:3000"
        });
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private GitHubAuthController CreateController() =>
        new(_stateService, _tokenService, _gitProvider, _options, _frontendOptions, _dbContext, NullLogger<GitHubAuthController>.Instance);

    [Fact]
    public async Task GetStatus_ReturnsCleanSummaryWithoutLeakingSecrets()
    {
        // Arrange
        var controller = CreateController();
        _dbContext.GitHubInstallationConnections.Add(new GitHubInstallationConnection
        {
            Id = Guid.NewGuid(),
            ExternalInstallationId = 12345678,
            AccountLogin = "enesscigdem",
            AccountType = "User",
            TargetAvatarUrl = "https://avatars.github.com/u/1",
            Status = GitHubInstallationStatus.Active,
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        // Act
        var actionResult = await controller.GetStatus(CancellationToken.None);

        // Assert
        var okResult = actionResult.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var dto = okResult!.Value as GitHubConnectionStatusDto;
        dto.Should().NotBeNull();
        dto!.IsConnected.Should().BeTrue();
        dto.Installations.Should().HaveCount(1);
        dto.Installations[0].AccountLogin.Should().Be("enesscigdem");

        // Verify no credentials or private keys in the DTO
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        json.Should().NotContain("PrivateKey");
        json.Should().NotContain("ClientSecret");
        json.Should().NotContain("token");
    }

    [Fact]
    public void GetConnectUrl_WithLocalReturnUrl_GeneratesValidSignedAppInstallationUrl()
    {
        // Arrange
        var controller = CreateController();

        // Act
        var result = controller.GetConnectUrl("/review/a1b2c3d4");

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var dto = okResult!.Value as GitHubConnectUrlResponseDto;
        dto.Should().NotBeNull();
        dto!.Url.Should().StartWith("https://github.com/apps/devpilot-app/installations/new?state=");
    }

    [Fact]
    public async Task HandleCallback_RootReturnUrl_RedirectsToFrontendRootWithSuccess()
    {
        // Arrange
        var controller = CreateController();
        var state = _stateService.GenerateState("/");
        var installationId = 12345678L;

        // Act
        var result = await controller.HandleCallback(
            code: "oauth_code_123",
            installationId: installationId,
            state: state,
            setupAction: "install",
            cancellationToken: CancellationToken.None);

        // Assert
        var redirectResult = result as RedirectResult;
        redirectResult.Should().NotBeNull();
        redirectResult!.Url.Should().Be("http://localhost:3000/?github_connected=true");
    }

    [Fact]
    public async Task HandleCallback_ValidCodeAndOwnership_RedirectsToFrontendReviewUrlWithSuccess()
    {
        // Arrange
        var controller = CreateController();
        var state = _stateService.GenerateState("/review/execution-123");
        var installationId = 12345678L;

        // Act
        var result = await controller.HandleCallback(
            code: "oauth_code_123",
            installationId: installationId,
            state: state,
            setupAction: "install",
            cancellationToken: CancellationToken.None);

        // Assert
        var redirectResult = result as RedirectResult;
        redirectResult.Should().NotBeNull();
        redirectResult!.Url.Should().Be("http://localhost:3000/review/execution-123?github_connected=true");

        var conn = await _dbContext.GitHubInstallationConnections
            .FirstOrDefaultAsync(c => c.ExternalInstallationId == installationId);
        conn.Should().NotBeNull();
        conn!.AccountLogin.Should().Be("enesscigdem");
        conn.Status.Should().Be(GitHubInstallationStatus.Active);
    }

    [Fact]
    public async Task HandleCallback_PreservesExistingQueryParamsOnReturnUrl()
    {
        // Arrange
        var controller = CreateController();
        var state = _stateService.GenerateState("/review/execution-123?tab=diff");
        var installationId = 12345678L;

        // Act
        var result = await controller.HandleCallback(
            code: "oauth_code_123",
            installationId: installationId,
            state: state,
            setupAction: "install",
            cancellationToken: CancellationToken.None);

        // Assert
        var redirectResult = result as RedirectResult;
        redirectResult.Should().NotBeNull();
        redirectResult!.Url.Should().Be("http://localhost:3000/review/execution-123?tab=diff&github_connected=true");
    }

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("javascript:alert(1)")]
    public async Task HandleCallback_MaliciousExternalReturnPaths_CannotEscapeTrustedFrontendOrigin(string maliciousUrl)
    {
        // Arrange
        var controller = CreateController();
        var state = _stateService.GenerateState(maliciousUrl);
        var installationId = 12345678L;

        // Act
        var result = await controller.HandleCallback(
            code: "oauth_code_123",
            installationId: installationId,
            state: state,
            setupAction: "install",
            cancellationToken: CancellationToken.None);

        // Assert
        var redirectResult = result as RedirectResult;
        redirectResult.Should().NotBeNull();
        redirectResult!.Url.Should().Be("http://localhost:3000/?github_connected=true");
    }

    [Fact]
    public async Task HandleCallback_InvalidState_RedirectsToFrontendRootWithErrorAndNeverSetsSuccess()
    {
        // Arrange
        var controller = CreateController();

        // Act
        var result = await controller.HandleCallback(
            code: "oauth_code",
            installationId: 12345678,
            state: "invalid_tampered_state",
            setupAction: "install",
            cancellationToken: CancellationToken.None);

        // Assert
        var redirectResult = result as RedirectResult;
        redirectResult.Should().NotBeNull();
        redirectResult!.Url.Should().Be("http://localhost:3000/?github_error=invalid_state");
        redirectResult.Url.Should().NotContain("github_connected=true");
    }

    [Fact]
    public async Task HandleCallback_UnverifiedOwnership_RedirectsToFrontendReturnUrlWithErrorAndNeverSetsSuccess()
    {
        // Arrange
        var controller = CreateController();
        var state = _stateService.GenerateState("/tasks");
        _tokenService.VerifyUserOwnershipSuccess = false;
        _tokenService.ConfiguredErrorMessage = "User does not own installation.";

        // Act
        var result = await controller.HandleCallback(
            code: "oauth_code",
            installationId: 88888888,
            state: state,
            setupAction: "install",
            cancellationToken: CancellationToken.None);

        // Assert
        var redirectResult = result as RedirectResult;
        redirectResult.Should().NotBeNull();
        redirectResult!.Url.Should().Be("http://localhost:3000/tasks?github_error=User%20does%20not%20own%20installation.");
        redirectResult.Url.Should().NotContain("github_connected=true");
    }

    [Fact]
    public async Task DisconnectInstallation_ExistingId_MarksStatusRevokedAndInvalidatesToken()
    {
        // Arrange
        var controller = CreateController();
        var connId = Guid.NewGuid();
        _dbContext.GitHubInstallationConnections.Add(new GitHubInstallationConnection
        {
            Id = connId,
            ExternalInstallationId = 12345678,
            AccountLogin = "enesscigdem",
            Status = GitHubInstallationStatus.Active,
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await controller.DisconnectInstallation(connId, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var conn = await _dbContext.GitHubInstallationConnections.FindAsync(connId);
        conn!.Status.Should().Be(GitHubInstallationStatus.Revoked);
        _tokenService.InvalidatedTokens.Should().Contain(12345678);
    }

    [Fact]
    public async Task GetRepositories_CorrelatesConnectedDevPilotWorkspaces()
    {
        // Arrange
        var controller = CreateController();
        var wsId = Guid.NewGuid();
        _dbContext.RepositoryWorkspaces.Add(new RepositoryWorkspace
        {
            Id = wsId,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed
        });
        await _dbContext.SaveChangesAsync();

        _gitProvider.ConfiguredRepositories = new List<GitRepository>
        {
            new() { FullName = "enesscigdem/DevPilot", Name = "DevPilot", Owner = "enesscigdem", DefaultBranch = "main", IsPrivate = false },
            new() { FullName = "enesscigdem/another-repo", Name = "another-repo", Owner = "enesscigdem", DefaultBranch = "main", IsPrivate = true }
        };

        // Act
        var actionResult = await controller.GetRepositories(CancellationToken.None);

        // Assert
        var okResult = actionResult.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var repos = okResult!.Value as IReadOnlyList<GitHubDiscoveredRepositoryDto>;
        repos.Should().HaveCount(2);

        var connected = repos!.First(r => r.FullName == "enesscigdem/DevPilot");
        connected.IsConnectedToDevPilot.Should().BeTrue();
        connected.DevPilotWorkspaceId.Should().Be(wsId);

        var unconnected = repos!.First(r => r.FullName == "enesscigdem/another-repo");
        unconnected.IsConnectedToDevPilot.Should().BeFalse();
        unconnected.DevPilotWorkspaceId.Should().BeNull();
    }

    private sealed class FakeGitProvider : IGitProvider
    {
        public string ProviderName => "GitHub";
        public List<GitRepository> ConfiguredRepositories { get; set; } = new();
        public List<GitBranch> ConfiguredBranches { get; set; } = new();

        public Task<GitProviderResult<IReadOnlyList<GitRepository>>> GetRepositoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GitProviderResult<IReadOnlyList<GitRepository>>.Success(ConfiguredRepositories));

        public Task<GitProviderResult<GitRepository>> GetRepositoryAsync(string owner, string repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(GitProviderResult<GitRepository>.Success(ConfiguredRepositories.FirstOrDefault(r => r.Owner == owner && r.Name == repository) ?? new GitRepository()));

        public Task<GitProviderResult<IReadOnlyList<GitBranch>>> GetBranchesAsync(string owner, string repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(GitProviderResult<IReadOnlyList<GitBranch>>.Success(ConfiguredBranches));

        public Task<GitProviderResult<string>> GetDefaultBranchAsync(string owner, string repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(GitProviderResult<string>.Success("main"));
    }
}
