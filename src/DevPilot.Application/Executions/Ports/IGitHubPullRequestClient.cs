namespace DevPilot.Application.Executions.Ports;

public interface IGitHubPullRequestClient
{
    Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> CreatePullRequestAsync(
        string owner,
        string repository,
        string head,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>> ListPullRequestsAsync(
        string owner,
        string repository,
        string head,
        string baseBranch,
        CancellationToken cancellationToken = default);

    Task<GitHubBranchRefResult> GetBranchHeadShaAsync(
        string owner,
        string repository,
        string branch,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> GetPullRequestAsync(
        string owner,
        string repository,
        int pullNumber,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>> ListCheckRunsForRefAsync(
        string owner,
        string repository,
        string refSha,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>> ListCommitStatusesForRefAsync(
        string owner,
        string repository,
        string refSha,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequestClientResult<GitHubMergeResultDto>> MergePullRequestAsync(
        string owner,
        string repository,
        int pullNumber,
        string expectedHeadSha,
        string? commitTitle = null,
        string? commitMessage = null,
        CancellationToken cancellationToken = default);
}

public sealed record GitHubMergeResultDto(
    string? MergeCommitSha,
    bool Merged);

public sealed class GitHubPullRequestClientResult<T>
{
    public bool IsSuccess { get; set; }

    public bool IsConfigurationError { get; set; }

    public bool IsConflict { get; set; }

    public bool IsRateLimit { get; set; }

    public bool IsExceededLimit { get; set; }

    public string? ErrorMessage { get; set; }

    public T? Data { get; set; }

    public static GitHubPullRequestClientResult<T> Success(T data) =>
        new() { IsSuccess = true, Data = data };

    public static GitHubPullRequestClientResult<T> Failure(
        string errorMessage,
        bool isConfigurationError = false,
        bool isConflict = false,
        bool isRateLimit = false,
        bool isExceededLimit = false) =>
        new()
        {
            IsSuccess = false,
            ErrorMessage = errorMessage,
            IsConfigurationError = isConfigurationError,
            IsConflict = isConflict,
            IsRateLimit = isRateLimit,
            IsExceededLimit = isExceededLimit
        };
}

public sealed record GitHubPullRequestDto(
    int Number,
    string HtmlUrl,
    string State,
    bool Merged,
    DateTime? ClosedAt,
    DateTime? MergedAt,
    string HeadRef,
    string HeadSha,
    string HeadRepoOwner,
    string HeadRepoName,
    string BaseRef,
    string BaseRepoOwner,
    string BaseRepoName,
    string Body);

public sealed record GitHubCheckRunDto(
    long Id,
    string Name,
    string Status,
    string? Conclusion,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string AppName);

public sealed record GitHubCommitStatusDto(
    long Id,
    string Context,
    string State,
    string? Description,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record GitHubBranchRefResult(
    bool IsSuccess,
    bool NotFound,
    string? Sha,
    string? ErrorMessage);
