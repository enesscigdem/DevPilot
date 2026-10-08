using DevPilot.Domain.Enums;

namespace DevPilot.Application.Trackers;

/// <summary>A connection with its token decrypted, only ever handed to the tracker client.</summary>
public sealed record TrackerConnectionInfo(
    Guid Id,
    TrackerProviderKind Provider,
    string BaseUrl,
    string? Email,
    string Token);

public sealed record TrackerConnectionDto(
    Guid Id,
    TrackerProviderKind Provider,
    string BaseUrl,
    string DisplayName,
    string? Email,
    DateTime CreatedAt);

public sealed record TrackerIssue(
    string Key,
    string Summary,
    string? Description,
    string? Status,
    string? IssueType,
    string? Priority,
    IReadOnlyList<string> Labels,
    string Url);

public sealed class TrackerResult<T>
{
    public bool IsSuccess { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>The credentials were refused or are missing a permission, so retrying cannot help.</summary>
    public bool IsAuthError { get; private set; }

    public bool IsNotFound { get; private set; }

    public T? Data { get; private set; }

    public static TrackerResult<T> Success(T data) => new() { IsSuccess = true, Data = data };

    public static TrackerResult<T> Failure(string message, bool isAuthError = false, bool isNotFound = false) =>
        new() { ErrorMessage = message, IsAuthError = isAuthError, IsNotFound = isNotFound };
}

public interface ITrackerClient
{
    /// <summary>Checks the credentials and returns the account name they belong to.</summary>
    Task<TrackerResult<string>> ValidateAsync(TrackerConnectionInfo connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds issues. A query that looks like a query language expression is used as is, any other text is a text
    /// search, and an empty query lists the open issues assigned to the account.
    /// </summary>
    Task<TrackerResult<IReadOnlyList<TrackerIssue>>> SearchAsync(
        TrackerConnectionInfo connection,
        string? query,
        CancellationToken cancellationToken = default);

    Task<TrackerResult<TrackerIssue>> GetIssueAsync(
        TrackerConnectionInfo connection,
        string key,
        CancellationToken cancellationToken = default);

    Task<TrackerResult<bool>> AddCommentAsync(
        TrackerConnectionInfo connection,
        string key,
        string text,
        CancellationToken cancellationToken = default);
}

/// <summary>Stored tracker access. Tokens leave the store only through <see cref="GetInfoAsync"/>.</summary>
public interface ITrackerConnectionStore
{
    Task<IReadOnlyList<TrackerConnectionDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<TrackerConnectionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Null when the connection does not exist or its token can no longer be decrypted.</summary>
    Task<TrackerConnectionInfo?> GetInfoAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TrackerConnectionDto> CreateAsync(
        TrackerProviderKind provider,
        string baseUrl,
        string displayName,
        string? email,
        string token,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Reports progress on an imported task back to the issue it came from. Never throws and never blocks delivery.</summary>
public interface ITaskExternalNotifier
{
    Task NotifyAsync(Guid taskId, string message, CancellationToken cancellationToken = default);
}
