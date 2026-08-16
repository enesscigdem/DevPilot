namespace DevPilot.Infrastructure.GitProviders;

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
}

public sealed class GitHubPullRequestClientResult<T>
{
    public bool IsSuccess { get; set; }

    public bool IsConfigurationError { get; set; }

    public bool IsConflict { get; set; }

    public bool IsRateLimit { get; set; }

    public string? ErrorMessage { get; set; }

    public T? Data { get; set; }

    public static GitHubPullRequestClientResult<T> Success(T data) =>
        new() { IsSuccess = true, Data = data };

    public static GitHubPullRequestClientResult<T> Failure(
        string errorMessage,
        bool isConfigurationError = false,
        bool isConflict = false,
        bool isRateLimit = false) =>
        new()
        {
            IsSuccess = false,
            ErrorMessage = errorMessage,
            IsConfigurationError = isConfigurationError,
            IsConflict = isConflict,
            IsRateLimit = isRateLimit
        };
}

public sealed record GitHubPullRequestDto(
    int Number,
    string HtmlUrl,
    string State,
    string HeadRef,
    string HeadSha,
    string HeadRepoOwner,
    string HeadRepoName,
    string BaseRef,
    string BaseRepoOwner,
    string BaseRepoName,
    string Body);

public sealed record GitHubBranchRefResult(
    bool IsSuccess,
    bool NotFound,
    string? Sha,
    string? ErrorMessage);
