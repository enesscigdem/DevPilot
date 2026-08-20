using DevPilot.Domain.Entities;
using DevPilot.Infrastructure.GitProviders;

namespace DevPilot.Tests.GitProviders;

public sealed class FakeGitHubAppTokenService : IGitHubAppTokenService
{
    public bool IsConfigured { get; set; } = true;

    public string DefaultToken { get; set; } = "ghs_fake_installation_token_1234567890";

    public GitHubTokenFailureKind ConfiguredFailureKind { get; set; } = GitHubTokenFailureKind.None;

    public string? ConfiguredErrorMessage { get; set; }

    public bool VerifyUserOwnershipSuccess { get; set; } = true;

    public GitHubUserInstallationInfo? ConfiguredUserInstallation { get; set; } =
        new(12345678, "enesscigdem", "User", 9876543, "https://avatars.githubusercontent.com/u/9876543");

    public List<long> InvalidatedTokens { get; } = new();

    public Task<GitHubTokenResult> GetInstallationTokenAsync(
        long externalInstallationId,
        string? repositoryName = null,
        CancellationToken cancellationToken = default)
    {
        if (ConfiguredFailureKind != GitHubTokenFailureKind.None)
        {
            return Task.FromResult(GitHubTokenResult.Failure(ConfiguredFailureKind, ConfiguredErrorMessage ?? "Configured failure", externalInstallationId));
        }

        return Task.FromResult(GitHubTokenResult.Success(DefaultToken, DateTimeOffset.UtcNow.AddHours(1), externalInstallationId));
    }

    public Task<GitHubTokenResult> GetTokenForRepositoryAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        if (ConfiguredFailureKind != GitHubTokenFailureKind.None)
        {
            return Task.FromResult(GitHubTokenResult.Failure(ConfiguredFailureKind, ConfiguredErrorMessage ?? "Configured failure"));
        }

        return Task.FromResult(GitHubTokenResult.Success(DefaultToken, DateTimeOffset.UtcNow.AddHours(1), 12345678));
    }

    public Task<GitHubTokenResult> GetTokenForWorkspaceAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        return GetTokenForRepositoryAsync(workspace.Owner, workspace.Repository, cancellationToken);
    }

    public Task<GitHubUserInstallationVerificationResult> VerifyUserInstallationOwnershipAsync(
        string oauthCode,
        long expectedInstallationId,
        CancellationToken cancellationToken = default)
    {
        if (!VerifyUserOwnershipSuccess)
        {
            return Task.FromResult(new GitHubUserInstallationVerificationResult(false, null, ConfiguredErrorMessage ?? "User verification failed."));
        }

        return Task.FromResult(new GitHubUserInstallationVerificationResult(true, ConfiguredUserInstallation, null));
    }

    public void InvalidateToken(long externalInstallationId)
    {
        InvalidatedTokens.Add(externalInstallationId);
    }
}
