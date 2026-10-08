using System.Text.Json;
using DevPilot.Application.GitProviders;
using DevPilot.Application.RepositoryClone;
using DevPilot.Application.RepositoryWorkspaces.Commands.CreateRepositoryWorkspace;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Executions;
using DevPilot.Infrastructure.GitProviders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.GitProviders;

public class GitRemoteUrlTests
{
    [Theory]
    [InlineData("https://gitlab.com/acme/api.git", "gitlab.com", "acme", "api")]
    [InlineData("https://gitlab.com/acme/platform/api", "gitlab.com", "acme/platform", "api")]
    [InlineData("https://Git.Company.Local/team/sub/group/repo.git", "git.company.local", "team/sub/group", "repo")]
    [InlineData("git@gitlab.com:acme/api.git", "gitlab.com", "acme", "api")]
    [InlineData("ssh://git@host.example/acme/api.git", "host.example", "acme", "api")]
    public void Parse_ReadsHostOwnerAndRepository(string url, string host, string owner, string repo)
    {
        var parsed = GitRemoteUrl.Parse(url);

        parsed.Should().NotBeNull();
        parsed!.Host.Should().Be(host);
        parsed.Owner.Should().Be(owner);
        parsed.Repository.Should().Be(repo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("https://gitlab.com/onlyone")]
    [InlineData("https://gitlab.com/a/../b")]
    [InlineData(@"C:\repos\local.git")]
    [InlineData("file:///tmp/repo.git")]
    public void Parse_RejectsUnsupportedInput(string url)
    {
        GitRemoteUrl.Parse(url).Should().BeNull();
    }

    [Theory]
    [InlineData("github.com", GitProviderKind.GitHub)]
    [InlineData("gitlab.com", GitProviderKind.GitLab)]
    [InlineData("gitlab.company.io", GitProviderKind.GitLab)]
    [InlineData("git.company.local", GitProviderKind.Generic)]
    public void InferProvider_UsesHostName(string host, GitProviderKind expected)
    {
        GitRemoteUrl.InferProvider(host).Should().Be(expected);
    }

    [Fact]
    public void BuildCloneUrl_KeepsNestedNamespaces()
    {
        GitRemoteUrl.BuildCloneUrl("gitlab.com", "acme/platform", "api")
            .Should().Be("https://gitlab.com/acme/platform/api.git");
    }

    [Fact]
    public void BuildCloneUrl_ForGitHub_MatchesTheLegacyFormat()
    {
        GitRemoteUrl.BuildCloneUrl("github.com", "octo", "repo")
            .Should().Be("https://github.com/octo/repo.git");
    }

    [Fact]
    public void MatchesRepository_AcceptsNonGitHubHostsByNamespacePath()
    {
        GitRemoteUrlNormalizer.MatchesRepository("https://gitlab.com/acme/platform/api.git", "acme/platform", "api").Should().BeTrue();
        GitRemoteUrlNormalizer.MatchesRepository("https://gitlab.com/acme/platform/api.git", "acme", "api").Should().BeFalse();
        GitRemoteUrlNormalizer.MatchesRepository("https://gitlab.com/acme/api.git", "acme", "other").Should().BeFalse();
    }

    [Fact]
    public void SanitizeOutput_MasksCredentialsOnAnyHost()
    {
        GitRemoteUrlNormalizer.SanitizeOutput("fatal: https://oauth2:glpat-secret@gitlab.com/acme/api.git")
            .Should().NotContain("glpat-secret").And.Contain("https://***@gitlab.com");
        GitRemoteUrlNormalizer.SanitizeOutput("https://x-access-token:ghs_secret@github.com/o/r")
            .Should().NotContain("ghs_secret").And.Contain("https://***@github.com");
    }
}

public class GitLabMappingTests
{
    [Theory]
    [InlineData("success", "success")]
    [InlineData("failed", "failure")]
    [InlineData("canceled", "error")]
    [InlineData("running", "pending")]
    [InlineData("created", "pending")]
    [InlineData("manual", "pending")]
    [InlineData("skipped", "success")]
    [InlineData("something-new", "pending")]
    public void MapStatus_TranslatesJobStatusToCommitState(string gitlab, string expected)
    {
        GitLabPullRequestClient.MapStatus(gitlab).Should().Be(expected);
    }

    [Fact]
    public void ToPullRequest_MapsOpenMergeRequest()
    {
        using var doc = JsonDocument.Parse("""
            {"iid":7,"web_url":"https://gitlab.com/acme/api/-/merge_requests/7","state":"opened",
             "source_branch":"devpilot/x","target_branch":"main","sha":"abc123","description":"body <!-- devpilot-execution:1 -->",
             "closed_at":null,"merged_at":null}
            """);

        var pr = GitLabPullRequestClient.ToPullRequest(doc.RootElement);

        pr.Number.Should().Be(7);
        pr.State.Should().Be("open");
        pr.Merged.Should().BeFalse();
        pr.HeadRef.Should().Be("devpilot/x");
        pr.BaseRef.Should().Be("main");
        pr.HeadSha.Should().Be("abc123");
        pr.Body.Should().Contain("devpilot-execution");
        pr.HeadRepoOwner.Should().BeEmpty();
    }

    [Fact]
    public void ToPullRequest_MergedMergeRequest_IsClosedAndMerged()
    {
        using var doc = JsonDocument.Parse("""
            {"iid":8,"state":"merged","source_branch":"a","target_branch":"main","sha":"s",
             "merged_at":"2026-10-01T10:00:00.000Z","closed_at":null}
            """);

        var pr = GitLabPullRequestClient.ToPullRequest(doc.RootElement);

        pr.State.Should().Be("closed");
        pr.Merged.Should().BeTrue();
        pr.MergedAt.Should().NotBeNull();
        pr.MergedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }
}

public class GitCredentialResolverTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly FakeGitHubAppTokenService _github = new();
    private readonly PlainProtector _protector = new();
    private readonly GitCredentialResolver _resolver;

    public GitCredentialResolverTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new DevPilotDbContext(options);
        _resolver = new GitCredentialResolver(_github, _protector, _db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GitHub_UsesTheInstallationToken()
    {
        var result = await _resolver.ResolveAsync(GitProviderKind.GitHub, "github.com", "octo", "repo", null);

        result.IsSuccess.Should().BeTrue();
        result.Credential!.Username.Should().Be("x-access-token");
        result.Credential.Token.Should().Be(_github.DefaultToken);
    }

    [Fact]
    public async Task GitLab_UsesTheStoredTokenWithOauth2Username()
    {
        var connection = await AddConnectionAsync(GitProviderKind.GitLab, "gitlab.com", "glpat-1");

        var result = await _resolver.ResolveAsync(GitProviderKind.GitLab, "gitlab.com", "acme", "api", connection.Id);

        result.IsSuccess.Should().BeTrue();
        result.Credential!.Token.Should().Be("glpat-1");
        result.Credential.Username.Should().Be("oauth2");
        result.Credential.Host.Should().Be("gitlab.com");
    }

    [Fact]
    public async Task Generic_UsesConfiguredUsernameElseGit()
    {
        var withUser = await AddConnectionAsync(GitProviderKind.Generic, "git.local", "pat", "alice");
        var withoutUser = await AddConnectionAsync(GitProviderKind.Generic, "other.local", "pat2");

        (await _resolver.ResolveAsync(GitProviderKind.Generic, "git.local", "o", "r", withUser.Id)).Credential!.Username.Should().Be("alice");
        (await _resolver.ResolveAsync(GitProviderKind.Generic, "other.local", "o", "r", withoutUser.Id)).Credential!.Username.Should().Be("git");
    }

    [Fact]
    public async Task WithoutExplicitConnection_FallsBackToTheNewestConnectionForTheHost()
    {
        await AddConnectionAsync(GitProviderKind.GitLab, "gitlab.com", "old");
        await Task.Delay(5);
        await AddConnectionAsync(GitProviderKind.GitLab, "gitlab.com", "new");

        var result = await _resolver.ResolveAsync(GitProviderKind.GitLab, "GitLab.com", "acme", "api", null);

        result.Credential!.Token.Should().Be("new");
    }

    [Fact]
    public async Task MissingConnection_FailsAsDisconnected()
    {
        var result = await _resolver.ResolveAsync(GitProviderKind.GitLab, "gitlab.com", "acme", "api", null);

        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(GitHubTokenFailureKind.Disconnected);
    }

    [Fact]
    public async Task UnreadableToken_FailsWithoutLeakingAnything()
    {
        var connection = await AddConnectionAsync(GitProviderKind.GitLab, "gitlab.com", "glpat-1");
        _protector.CanDecrypt = false;

        var result = await _resolver.ResolveAsync(GitProviderKind.GitLab, "gitlab.com", "acme", "api", connection.Id);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotContain("glpat-1");
    }

    [Fact]
    public async Task ResolveForRepository_UsesTheWorkspaceProvider()
    {
        var connection = await AddConnectionAsync(GitProviderKind.GitLab, "gitlab.com", "glpat-9");
        _db.RepositoryWorkspaces.Add(new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Provider = GitProviderKind.GitLab,
            Host = "gitlab.com",
            GitConnectionId = connection.Id,
            Owner = "acme/platform",
            Repository = "api",
            Branch = "main",
            LocalPath = "x",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveForRepositoryAsync("acme/platform", "api");

        result.Credential!.Token.Should().Be("glpat-9");
    }

    [Fact]
    public async Task ResolveForRepository_UnknownRepository_DefaultsToGitHub()
    {
        var result = await _resolver.ResolveForRepositoryAsync("octo", "repo");

        result.Credential!.Username.Should().Be("x-access-token");
    }

    private async Task<GitConnection> AddConnectionAsync(GitProviderKind provider, string host, string token, string? username = null)
    {
        var connection = new GitConnection
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            Host = host,
            DisplayName = host,
            Username = username,
            EncryptedToken = _protector.Protect(token),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.GitConnections.Add(connection);
        await _db.SaveChangesAsync();
        return connection;
    }

    private sealed class PlainProtector : IGitSecretProtector
    {
        public bool CanDecrypt { get; set; } = true;

        public string Protect(string plainText) => "enc:" + plainText;

        public string? Unprotect(string protectedText) =>
            CanDecrypt && protectedText.StartsWith("enc:") ? protectedText[4..] : null;
    }
}

public class CreateWorkspaceFromRemoteUrlTests
{
    private readonly CapturingCloneService _clone = new();
    private readonly FakeConnectionStore _store = new();
    private readonly CreateRepositoryWorkspaceCommandHandler _handler;

    public CreateWorkspaceFromRemoteUrlTests()
    {
        _handler = new CreateRepositoryWorkspaceCommandHandler(
            _clone,
            NullLogger<CreateRepositoryWorkspaceCommandHandler>.Instance,
            _store);
    }

    [Fact]
    public async Task GitLabUrl_ClonesWithNestedNamespaceAndConnectionProvider()
    {
        var connection = _store.Add(GitProviderKind.GitLab, "gitlab.com");

        var result = await Create("https://gitlab.com/acme/platform/api.git", connection.Id);

        result.Success.Should().BeTrue();
        _clone.Last!.Provider.Should().Be(GitProviderKind.GitLab);
        _clone.Last.Host.Should().Be("gitlab.com");
        _clone.Last.Owner.Should().Be("acme/platform");
        _clone.Last.Repository.Should().Be("api");
        _clone.Last.GitConnectionId.Should().Be(connection.Id);
        result.Workspace!.Provider.Should().Be("GitLab");
    }

    [Fact]
    public async Task GenericHost_WithoutConnection_IsGenericProvider()
    {
        var result = await Create("https://git.company.local/team/repo.git", null);

        result.Success.Should().BeTrue();
        _clone.Last!.Provider.Should().Be(GitProviderKind.Generic);
    }

    [Fact]
    public async Task GitHubUrl_IsRejectedInFavourOfTheApp()
    {
        var result = await Create("https://github.com/octo/repo.git", null);

        result.Success.Should().BeFalse();
        result.IsValidationError.Should().BeTrue();
        _clone.Last.Should().BeNull();
    }

    [Fact]
    public async Task ConnectionForAnotherHost_IsRejected()
    {
        var connection = _store.Add(GitProviderKind.GitLab, "gitlab.com");

        var result = await Create("https://git.company.local/team/repo.git", connection.Id);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("gitlab.com");
        _clone.Last.Should().BeNull();
    }

    [Fact]
    public async Task UnknownConnection_IsRejected()
    {
        var result = await Create("https://gitlab.com/acme/api.git", Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.IsValidationError.Should().BeTrue();
    }

    [Fact]
    public async Task InvalidUrl_IsRejected()
    {
        var result = await Create("nonsense", null);

        result.Success.Should().BeFalse();
        result.IsValidationError.Should().BeTrue();
    }

    [Fact]
    public async Task NestedOwner_IsStillRejectedForPlainOwnerField()
    {
        var dto = new CreateRepositoryWorkspaceDto { Owner = "a/b", Repository = "r", Branch = "main" };

        var result = await _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(dto));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Owner contains invalid characters.");
    }

    private Task<CreateRepositoryWorkspaceResult> Create(string url, Guid? connectionId) =>
        _handler.HandleAsync(new CreateRepositoryWorkspaceCommand(new CreateRepositoryWorkspaceDto
        {
            RemoteUrl = url,
            Branch = "main",
            GitConnectionId = connectionId,
        }));

    private sealed class CapturingCloneService : IRepositoryCloneService
    {
        public CloneRequest? Last { get; private set; }

        public Task<CloneResult> CloneAsync(CloneRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult(new CloneResult
            {
                Success = true,
                WorkspaceId = Guid.NewGuid(),
                Owner = request.Owner,
                Repository = request.Repository,
                Branch = request.Branch,
                Status = RepositoryWorkspaceStatus.Completed,
            });
        }
    }

    private sealed class FakeConnectionStore : IGitConnectionStore
    {
        private readonly List<GitConnectionDto> _items = new();

        public GitConnectionDto Add(GitProviderKind provider, string host)
        {
            var dto = new GitConnectionDto(Guid.NewGuid(), provider, host, host, null, DateTime.UtcNow);
            _items.Add(dto);
            return dto;
        }

        public Task<IReadOnlyList<GitConnectionDto>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitConnectionDto>>(_items);

        public Task<GitConnectionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.FirstOrDefault(i => i.Id == id));

        public Task<GitConnectionDto> CreateAsync(GitProviderKind provider, string host, string displayName, string? username, string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(Add(provider, host));

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.RemoveAll(i => i.Id == id) > 0);
    }
}
