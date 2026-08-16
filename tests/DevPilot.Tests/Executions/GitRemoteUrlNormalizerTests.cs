using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class GitRemoteUrlNormalizerTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    [InlineData("git@github.com:owner/repo.git", "owner", "repo")]
    [InlineData("git@github.com:owner/repo", "owner", "repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://user:token123@github.com/owner/repo.git", "owner", "repo")]
    public void ParseGitHubUrl_ValidForms_ExtractsOwnerAndRepo(string url, string expectedOwner, string expectedRepo)
    {
        var result = GitRemoteUrlNormalizer.ParseGitHubUrl(url);

        result.Should().NotBeNull();
        result!.Value.Owner.Should().Be(expectedOwner);
        result.Value.Repo.Should().Be(expectedRepo);
    }

    [Theory]
    [InlineData("https://github.com.evil.com/owner/repo.git")]
    [InlineData("https://evilgithub.com/owner/repo.git")]
    [InlineData("https://gitlab.com/owner/repo.git")]
    [InlineData("git@bitbucket.org:owner/repo.git")]
    [InlineData("invalid_url")]
    public void ParseGitHubUrl_InvalidOrLookalikeHosts_ReturnsNull(string url)
    {
        var result = GitRemoteUrlNormalizer.ParseGitHubUrl(url);

        result.Should().BeNull();
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Test input URL containing embedded credential pattern.")]
    public void MatchesRepository_CaseInsensitiveMatching_ReturnsTrue()
    {
        var matches = GitRemoteUrlNormalizer.MatchesRepository("HTTPS://GITHUB.COM/Owner/Repo.git", "owner", "repo");

        matches.Should().BeTrue();
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Test input URL containing embedded credential pattern.")]
    public void SanitizeOutput_StripsEmbeddedCredentialsFromOutput()
    {
        var dirtyOutput = "fatal: unable to access 'https://user:secret_token_123@github.com/owner/repo.git': Could not resolve host";
        var sanitized = GitRemoteUrlNormalizer.SanitizeOutput(dirtyOutput);

        sanitized.Should().NotContain("secret_token_123");
        sanitized.Should().Contain("https://***@github.com");
    }
}
