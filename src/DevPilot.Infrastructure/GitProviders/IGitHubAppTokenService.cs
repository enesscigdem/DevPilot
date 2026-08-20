using DevPilot.Domain.Entities;

namespace DevPilot.Infrastructure.GitProviders;

public enum GitHubTokenFailureKind
{
    None,
    ConfigurationError,
    Disconnected,
    InstallationInvalidOrRevoked,
    RepositoryUnauthorized,
    PermissionDenied,
    RateLimited,
    TransientError
}

public sealed record GitHubTokenResult(
    bool IsSuccess,
    string? Token,
    DateTimeOffset? ExpiresAt,
    GitHubTokenFailureKind FailureKind = GitHubTokenFailureKind.None,
    string? ErrorMessage = null,
    long? ExternalInstallationId = null)
{
    public static GitHubTokenResult Success(string token, DateTimeOffset expiresAt, long externalInstallationId) =>
        new(true, token, expiresAt, GitHubTokenFailureKind.None, null, externalInstallationId);

    public static GitHubTokenResult Failure(GitHubTokenFailureKind kind, string message, long? externalInstallationId = null) =>
        new(false, null, null, kind, message, externalInstallationId);
}

public sealed record GitHubUserInstallationInfo(
    long ExternalInstallationId,
    string AccountLogin,
    string AccountType,
    long TargetId,
    string? TargetAvatarUrl);

public sealed record GitHubUserInstallationVerificationResult(
    bool IsSuccess,
    GitHubUserInstallationInfo? Installation,
    string? ErrorMessage);

public interface IGitHubAppTokenService
{
    bool IsConfigured { get; }

    Task<GitHubTokenResult> GetInstallationTokenAsync(
        long externalInstallationId,
        string? repositoryName = null,
        CancellationToken cancellationToken = default);

    Task<GitHubTokenResult> GetTokenForRepositoryAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default);

    Task<GitHubTokenResult> GetTokenForWorkspaceAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default);

    Task<GitHubUserInstallationVerificationResult> VerifyUserInstallationOwnershipAsync(
        string oauthCode,
        long expectedInstallationId,
        CancellationToken cancellationToken = default);

    void InvalidateToken(long externalInstallationId);
}
