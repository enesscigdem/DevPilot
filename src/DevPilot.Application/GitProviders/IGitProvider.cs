namespace DevPilot.Application.GitProviders;

public interface IGitProvider
{
    string ProviderName { get; }

    Task<GitProviderResult<IReadOnlyList<GitRepository>>> GetRepositoriesAsync(
        CancellationToken cancellationToken = default);

    Task<GitProviderResult<GitRepository>> GetRepositoryAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default);

    Task<GitProviderResult<IReadOnlyList<GitBranch>>> GetBranchesAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default);

    Task<GitProviderResult<string>> GetDefaultBranchAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default);
}
