using DevPilot.Application.GitProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.GitProviders;

public sealed record GitCredential(string Token, string Username, string Host = "");

public sealed record GitCredentialResult(
    bool IsSuccess,
    GitCredential? Credential,
    string? ErrorMessage,
    GitHubTokenFailureKind FailureKind = GitHubTokenFailureKind.None)
{
    public static GitCredentialResult Success(GitCredential credential) => new(true, credential, null);

    public static GitCredentialResult Failure(string message, GitHubTokenFailureKind kind = GitHubTokenFailureKind.ConfigurationError) =>
        new(false, null, message, kind);
}

/// <summary>Finds the token git and the host API should use for a repository, whatever the provider.</summary>
public interface IGitCredentialResolver
{
    Task<GitCredentialResult> ResolveAsync(
        GitProviderKind provider,
        string host,
        string owner,
        string repository,
        Guid? gitConnectionId,
        CancellationToken cancellationToken = default);

    Task<GitCredentialResult> ResolveForWorkspaceAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>Looks the workspace up by identity. Used by callers that only carry owner and repository.</summary>
    Task<GitCredentialResult> ResolveForRepositoryAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default);
}

public sealed class GitCredentialResolver : IGitCredentialResolver
{
    public const string GitHubUsername = "x-access-token";

    private readonly IGitHubAppTokenService _githubTokens;
    private readonly IGitSecretProtector _protector;
    private readonly DevPilotDbContext _dbContext;

    public GitCredentialResolver(
        IGitHubAppTokenService githubTokens,
        IGitSecretProtector protector,
        DevPilotDbContext dbContext)
    {
        _githubTokens = githubTokens;
        _protector = protector;
        _dbContext = dbContext;
    }

    public async Task<GitCredentialResult> ResolveAsync(
        GitProviderKind provider,
        string host,
        string owner,
        string repository,
        Guid? gitConnectionId,
        CancellationToken cancellationToken = default)
    {
        if (provider == GitProviderKind.GitHub)
        {
            var token = await _githubTokens.GetTokenForRepositoryAsync(owner, repository, cancellationToken).ConfigureAwait(false);
            return token.IsSuccess && !string.IsNullOrWhiteSpace(token.Token)
                ? GitCredentialResult.Success(new GitCredential(token.Token, GitHubUsername, "github.com"))
                : GitCredentialResult.Failure(token.ErrorMessage ?? "GitHub authorization failed.", token.FailureKind);
        }

        var normalizedHost = host.Trim().ToLowerInvariant();
        var query = _dbContext.GitConnections.AsNoTracking().Where(c => c.Provider == provider);
        var connection = gitConnectionId.HasValue
            ? await query.FirstOrDefaultAsync(c => c.Id == gitConnectionId.Value, cancellationToken).ConfigureAwait(false)
            : await query.Where(c => c.Host == normalizedHost).OrderByDescending(c => c.UpdatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (connection is null)
        {
            return GitCredentialResult.Failure(
                $"No access token is stored for {provider} host '{normalizedHost}'. Add a connection first.",
                GitHubTokenFailureKind.Disconnected);
        }

        var plain = _protector.Unprotect(connection.EncryptedToken);
        if (string.IsNullOrWhiteSpace(plain))
        {
            return GitCredentialResult.Failure(
                $"The stored token for '{connection.DisplayName}' cannot be read. Re-enter it.",
                GitHubTokenFailureKind.InstallationInvalidOrRevoked);
        }

        var username = !string.IsNullOrWhiteSpace(connection.Username)
            ? connection.Username!
            : DefaultUsername(provider);

        return GitCredentialResult.Success(new GitCredential(plain, username, normalizedHost));
    }

    /// <summary>
    /// The user name sent with a token when the connection names none. GitLab takes oauth2, Bitbucket access tokens take
    /// x-token-auth (app passwords need the real account name, which the connection then carries), Azure DevOps ignores it.
    /// </summary>
    public static string DefaultUsername(GitProviderKind provider) => provider switch
    {
        GitProviderKind.GitLab => "oauth2",
        GitProviderKind.Bitbucket => "x-token-auth",
        _ => "git",
    };

    public Task<GitCredentialResult> ResolveForWorkspaceAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(workspace.Provider, workspace.Host, workspace.Owner, workspace.Repository, workspace.GitConnectionId, cancellationToken);

    public async Task<GitCredentialResult> ResolveForRepositoryAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        // Only owner and repository are known here. The same pair on two hosts must not borrow the other host's token.
        var matches = await _dbContext.RepositoryWorkspaces
            .AsNoTracking()
            .Where(w => w.Owner == owner && w.Repository == repository)
            .OrderByDescending(w => w.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (matches.Select(w => (w.Provider, w.Host)).Distinct().Count() > 1)
        {
            return GitCredentialResult.Failure($"{owner}/{repository} is connected on more than one git host, so the credential target is ambiguous.");
        }

        var workspace = matches.FirstOrDefault();
        return workspace is null
            ? await ResolveAsync(GitProviderKind.GitHub, "github.com", owner, repository, null, cancellationToken).ConfigureAwait(false)
            : await ResolveForWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
    }
}
