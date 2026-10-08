using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.GitProviders;

/// <summary>Sends each call to the client of the host the repository lives on. Generic git hosts have no pull request API.</summary>
internal sealed class RoutingPullRequestClient : IGitHubPullRequestClient
{
    private const string NoPullRequestApi =
        "This git host has no pull request API. The branch was pushed; open the merge request on the host.";

    private readonly DevPilotDbContext _dbContext;
    private readonly GitHubPullRequestClient _github;
    private readonly GitLabPullRequestClient _gitlab;
    private readonly AzureDevOpsPullRequestClient _azure;
    private readonly BitbucketPullRequestClient _bitbucket;

    public RoutingPullRequestClient(
        DevPilotDbContext dbContext,
        GitHubPullRequestClient github,
        GitLabPullRequestClient gitlab,
        AzureDevOpsPullRequestClient azure,
        BitbucketPullRequestClient bitbucket)
    {
        _dbContext = dbContext;
        _github = github;
        _gitlab = gitlab;
        _azure = azure;
        _bitbucket = bitbucket;
    }

    public async Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> CreatePullRequestAsync(
        string owner, string repository, string head, string baseBranch, string title, string body,
        CancellationToken cancellationToken = default)
    {
        var client = await PickAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        return client is null
            ? GitHubPullRequestClientResult<GitHubPullRequestDto>.Failure(NoPullRequestApi, isConflict: true)
            : await client.CreatePullRequestAsync(owner, repository, head, baseBranch, title, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>> ListPullRequestsAsync(
        string owner, string repository, string head, string baseBranch,
        CancellationToken cancellationToken = default)
    {
        var client = await PickAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        return client is null
            ? GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>.Failure(NoPullRequestApi, isConflict: true)
            : await client.ListPullRequestsAsync(owner, repository, head, baseBranch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubBranchRefResult> GetBranchHeadShaAsync(
        string owner, string repository, string branch,
        CancellationToken cancellationToken = default)
    {
        var client = await PickAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        return client is null
            ? new GitHubBranchRefResult(false, false, null, NoPullRequestApi)
            : await client.GetBranchHeadShaAsync(owner, repository, branch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> GetPullRequestAsync(
        string owner, string repository, int pullNumber,
        CancellationToken cancellationToken = default)
    {
        var client = await PickAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        return client is null
            ? GitHubPullRequestClientResult<GitHubPullRequestDto>.Failure(NoPullRequestApi, isConflict: true)
            : await client.GetPullRequestAsync(owner, repository, pullNumber, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>> ListCheckRunsForRefAsync(
        string owner, string repository, string refSha,
        CancellationToken cancellationToken = default)
    {
        var client = await PickAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        return client is null
            ? GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>.Failure(NoPullRequestApi, isConflict: true)
            : await client.ListCheckRunsForRefAsync(owner, repository, refSha, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>> ListCommitStatusesForRefAsync(
        string owner, string repository, string refSha,
        CancellationToken cancellationToken = default)
    {
        var client = await PickAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        return client is null
            ? GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>.Failure(NoPullRequestApi, isConflict: true)
            : await client.ListCommitStatusesForRefAsync(owner, repository, refSha, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubPullRequestClientResult<GitHubMergeResultDto>> MergePullRequestAsync(
        string owner, string repository, int pullNumber, string expectedHeadSha,
        string? commitTitle = null, string? commitMessage = null,
        CancellationToken cancellationToken = default)
    {
        var client = await PickAsync(owner, repository, cancellationToken).ConfigureAwait(false);
        return client is null
            ? GitHubPullRequestClientResult<GitHubMergeResultDto>.Failure(NoPullRequestApi, isConflict: true)
            : await client.MergePullRequestAsync(owner, repository, pullNumber, expectedHeadSha, commitTitle, commitMessage, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IGitHubPullRequestClient?> PickAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        var provider = await _dbContext.RepositoryWorkspaces
            .AsNoTracking()
            .Where(w => w.Owner == owner && w.Repository == repository)
            .OrderByDescending(w => w.UpdatedAt)
            .Select(w => (GitProviderKind?)w.Provider)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return (provider ?? GitProviderKind.GitHub) switch
        {
            GitProviderKind.GitLab => _gitlab,
            GitProviderKind.AzureDevOps => _azure,
            GitProviderKind.Bitbucket => _bitbucket,
            GitProviderKind.Generic => null,
            _ => _github,
        };
    }
}
