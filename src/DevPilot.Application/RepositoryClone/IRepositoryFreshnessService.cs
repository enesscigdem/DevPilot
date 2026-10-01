namespace DevPilot.Application.RepositoryClone;

public enum RepositoryFreshnessStatus
{
    /// <summary>Local branch already matches origin.</summary>
    UpToDate,

    /// <summary>Local branch was behind origin and was fast-forwarded (no local work touched).</summary>
    FastForwarded,

    /// <summary>Local branch is behind origin and was NOT updated (local changes, wrong branch, or merge refused).</summary>
    Behind,

    /// <summary>Local branch has commits origin does not, and origin has commits the local branch lacks. Never touched.</summary>
    Diverged,

    /// <summary>Local branch only has extra local commits. Never touched.</summary>
    Ahead,

    /// <summary>Origin could not be reached; freshness is unknown.</summary>
    FetchFailed,

    /// <summary>Freshness is not applicable (not a git repo, detached HEAD, origin mismatch, ...).</summary>
    NotApplicable,
}

public sealed record RepositoryFreshnessRequest(
    string LocalPath,
    string Owner,
    string Repository,
    string? Branch);

public sealed record RepositoryFreshnessResult(
    RepositoryFreshnessStatus Status,
    string? BranchName,
    string? LocalCommitSha,
    string? RemoteCommitSha,
    int BehindCount,
    int AheadCount,
    string? PreviousCommitSha,
    string? Message)
{
    public bool FastForwarded => Status == RepositoryFreshnessStatus.FastForwarded;

    /// <summary>True when origin is known to have commits the local base does not contain.</summary>
    public bool IsStale => Status is RepositoryFreshnessStatus.Behind or RepositoryFreshnessStatus.Diverged;
}

/// <summary>
/// Fetches origin and fast-forwards the managed repository clone only when that is provably safe.
/// Never overwrites local work and never fails the caller: problems are reported in the result.
/// </summary>
public interface IRepositoryFreshnessService
{
    Task<RepositoryFreshnessResult> RefreshAsync(
        RepositoryFreshnessRequest request,
        CancellationToken cancellationToken = default);
}
