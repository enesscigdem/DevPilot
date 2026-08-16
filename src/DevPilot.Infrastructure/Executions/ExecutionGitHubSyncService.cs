using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.GitProviders;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class ExecutionGitHubSyncService : IExecutionGitHubSyncService
{
    private readonly IGitHubPullRequestClient _githubClient;
    private readonly ILogger<ExecutionGitHubSyncService> _logger;

    public ExecutionGitHubSyncService(
        IGitHubPullRequestClient githubClient,
        ILogger<ExecutionGitHubSyncService> logger)
    {
        _githubClient = githubClient;
        _logger = logger;
    }

    public async Task<GitHubSyncResultDto> SyncPullRequestAndCiAsync(
        TaskExecution execution,
        bool bypassFreshnessCache = false,
        CancellationToken cancellationToken = default)
    {
        var repoOwner = execution.DevelopmentTask?.RepositoryWorkspace?.Owner ?? string.Empty;
        var repoName = execution.DevelopmentTask?.RepositoryWorkspace?.Repository ?? string.Empty;
        var expectedBaseBranch = execution.PullRequestBaseBranch ?? execution.DevelopmentTask?.RepositoryWorkspace?.Branch ?? string.Empty;
        var expectedHeadBranch = execution.RemoteBranchName ?? execution.BranchName ?? string.Empty;
        var expectedHeadSha = execution.RemoteCommitSha ?? execution.CommitSha ?? string.Empty;

        if (!execution.PullRequestNumber.HasValue || execution.PullRequestNumber.Value <= 0)
        {
            return Failure("Execution does not have a valid persisted pull request number.", isExternalFailure: false);
        }

        if (string.IsNullOrWhiteSpace(repoOwner) || string.IsNullOrWhiteSpace(repoName))
        {
            return Failure("Repository workspace owner or repository name is missing.", isExternalFailure: false);
        }

        if (string.IsNullOrWhiteSpace(expectedHeadSha))
        {
            return Failure("Execution remote commit SHA is missing.", isExternalFailure: false);
        }

        // 1. Fetch live PR details from GitHub by exact PR number
        var prResult = await _githubClient.GetPullRequestAsync(repoOwner, repoName, execution.PullRequestNumber.Value, cancellationToken).ConfigureAwait(false);

        if (!prResult.IsSuccess || prResult.Data == null)
        {
            if (prResult.IsConfigurationError)
            {
                return Failure(prResult.ErrorMessage ?? "GitHub API configuration or authentication error.", isConfigurationError: true, isExternalFailure: true);
            }
            return Failure(prResult.ErrorMessage ?? "Failed to retrieve pull request details from GitHub.", isExternalFailure: true);
        }

        var pr = prResult.Data;

        // 2. Determine Remote State
        // Rule: merged == true (or merged_at set) takes precedence over state == "closed"
        ExecutionPullRequestRemoteState remoteState;
        if (pr.Merged || pr.MergedAt.HasValue)
        {
            remoteState = ExecutionPullRequestRemoteState.Merged;
        }
        else if (string.Equals(pr.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            remoteState = ExecutionPullRequestRemoteState.Closed;
        }
        else if (string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            remoteState = ExecutionPullRequestRemoteState.Open;
        }
        else
        {
            remoteState = ExecutionPullRequestRemoteState.Unknown;
        }

        // 3. Validate PR Integrity
        ExecutionPullRequestIntegrityStatus integrityStatus;

        bool repoMatches = (string.IsNullOrWhiteSpace(pr.HeadRepoOwner) || string.Equals(pr.HeadRepoOwner, repoOwner, StringComparison.OrdinalIgnoreCase)) &&
                           (string.IsNullOrWhiteSpace(pr.HeadRepoName) || string.Equals(pr.HeadRepoName, repoName, StringComparison.OrdinalIgnoreCase)) &&
                           (string.IsNullOrWhiteSpace(pr.BaseRepoOwner) || string.Equals(pr.BaseRepoOwner, repoOwner, StringComparison.OrdinalIgnoreCase)) &&
                           (string.IsNullOrWhiteSpace(pr.BaseRepoName) || string.Equals(pr.BaseRepoName, repoName, StringComparison.OrdinalIgnoreCase));

        bool refMatches = string.Equals(pr.HeadRef, expectedHeadBranch, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(pr.BaseRef, expectedBaseBranch, StringComparison.OrdinalIgnoreCase);

        if (!repoMatches || !refMatches || pr.Number != execution.PullRequestNumber.Value)
        {
            integrityStatus = ExecutionPullRequestIntegrityStatus.IdentityMismatch;
        }
        else if (!string.Equals(pr.HeadSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            integrityStatus = ExecutionPullRequestIntegrityStatus.HeadChanged;
        }
        else
        {
            integrityStatus = ExecutionPullRequestIntegrityStatus.Valid;
        }

        // If integrity is not Valid, do NOT fetch or trust CI for an unvalidated or changed SHA
        if (integrityStatus != ExecutionPullRequestIntegrityStatus.Valid)
        {
            _logger.LogWarning("Execution {ExecutionId} PR integrity is {IntegrityStatus}. Skipping CI lookup.", execution.Id, integrityStatus);
            return Success(remoteState, integrityStatus, pr.ClosedAt, pr.MergedAt, ExecutionCiStatus.Unknown, Array.Empty<ExecutionCiCheck>());
        }

        // 4. Query Check Runs and Commit Statuses for exact approved RemoteCommitSha
        var checkRunsResult = await _githubClient.ListCheckRunsForRefAsync(repoOwner, repoName, expectedHeadSha, cancellationToken).ConfigureAwait(false);
        if (!checkRunsResult.IsSuccess)
        {
            if (checkRunsResult.IsConfigurationError)
            {
                return Failure(checkRunsResult.ErrorMessage ?? "GitHub API authentication error for check runs.", isConfigurationError: true, isExternalFailure: true);
            }
            return Failure(checkRunsResult.ErrorMessage ?? "Failed to retrieve check runs from GitHub.", isExternalFailure: true);
        }

        var commitStatusesResult = await _githubClient.ListCommitStatusesForRefAsync(repoOwner, repoName, expectedHeadSha, cancellationToken).ConfigureAwait(false);
        if (!commitStatusesResult.IsSuccess)
        {
            if (commitStatusesResult.IsConfigurationError)
            {
                return Failure(commitStatusesResult.ErrorMessage ?? "GitHub API authentication error for commit statuses.", isConfigurationError: true, isExternalFailure: true);
            }
            return Failure(commitStatusesResult.ErrorMessage ?? "Failed to retrieve commit statuses from GitHub.", isExternalFailure: true);
        }

        var rawCheckRuns = checkRunsResult.Data ?? Array.Empty<GitHubCheckRunDto>();
        var rawCommitStatuses = commitStatusesResult.Data ?? Array.Empty<GitHubCommitStatusDto>();

        // Map to ExecutionCiCheck domain entities
        var checks = new List<ExecutionCiCheck>();

        foreach (var cr in rawCheckRuns)
        {
            checks.Add(new ExecutionCiCheck
            {
                ExternalId = cr.Id,
                Name = BoundString(cr.Name, 200),
                Source = BoundString(cr.AppName, 100),
                CheckType = ExecutionCiCheckType.CheckRun,
                Status = BoundString(cr.Status, 50),
                Conclusion = BoundString(cr.Conclusion, 50),
                StartedAt = cr.StartedAt,
                CompletedAt = cr.CompletedAt
            });
        }

        foreach (var cs in rawCommitStatuses)
        {
            checks.Add(new ExecutionCiCheck
            {
                ExternalId = cs.Id,
                Name = BoundString(cs.Context, 200),
                Source = BoundString($"CommitStatus: {cs.Context}", 100),
                CheckType = ExecutionCiCheckType.CommitStatus,
                Status = BoundString(cs.State, 50),
                Conclusion = BoundString(cs.State, 50),
                StartedAt = cs.CreatedAt,
                CompletedAt = cs.UpdatedAt ?? cs.CreatedAt
            });
        }

        // 5. Aggregate CI Status
        var aggregateStatus = AggregateCiStatus(rawCheckRuns, rawCommitStatuses);

        return Success(remoteState, integrityStatus, pr.ClosedAt, pr.MergedAt, aggregateStatus, checks);
    }

    public static ExecutionCiStatus AggregateCiStatus(
        IReadOnlyList<GitHubCheckRunDto> checkRuns,
        IReadOnlyList<GitHubCommitStatusDto> commitStatuses)
    {
        if (checkRuns.Count == 0 && commitStatuses.Count == 0)
        {
            return ExecutionCiStatus.NoChecks;
        }

        // Check for any Failure signal across both families
        // Failure conclusions/states
        bool hasFailure = false;
        foreach (var cr in checkRuns)
        {
            var conc = cr.Conclusion?.ToLowerInvariant();
            var stat = cr.Status?.ToLowerInvariant();
            if (conc is "failure" or "error" or "action_required" or "timed_out" or "cancelled" or "stale" ||
                stat is "failure" or "error")
            {
                hasFailure = true;
                break;
            }
        }

        if (!hasFailure)
        {
            foreach (var cs in commitStatuses)
            {
                var st = cs.State?.ToLowerInvariant();
                if (st is "failure" or "error")
                {
                    hasFailure = true;
                    break;
                }
            }
        }

        if (hasFailure)
        {
            return ExecutionCiStatus.Failure;
        }

        // Check for any Pending signal across both families
        bool hasPending = false;
        foreach (var cr in checkRuns)
        {
            var stat = cr.Status?.ToLowerInvariant();
            if (stat is "queued" or "in_progress" or "pending" or "requested" or "waiting" ||
                (stat != "completed" && string.IsNullOrEmpty(cr.Conclusion)))
            {
                hasPending = true;
                break;
            }
        }

        if (!hasPending)
        {
            foreach (var cs in commitStatuses)
            {
                var st = cs.State?.ToLowerInvariant();
                if (st is "pending")
                {
                    hasPending = true;
                    break;
                }
            }
        }

        if (hasPending)
        {
            return ExecutionCiStatus.Pending;
        }

        // Check for any Unknown/unrecognized signal
        bool hasUnknown = false;
        foreach (var cr in checkRuns)
        {
            var conc = cr.Conclusion?.ToLowerInvariant();
            var stat = cr.Status?.ToLowerInvariant();
            if (stat == "completed")
            {
                if (conc is not ("success" or "neutral" or "skipped"))
                {
                    hasUnknown = true;
                    break;
                }
            }
            else
            {
                hasUnknown = true;
                break;
            }
        }

        if (!hasUnknown)
        {
            foreach (var cs in commitStatuses)
            {
                var st = cs.State?.ToLowerInvariant();
                if (st is not ("success"))
                {
                    hasUnknown = true;
                    break;
                }
            }
        }

        if (hasUnknown)
        {
            return ExecutionCiStatus.Unknown;
        }

        // Check if there is at least one success signal
        int successCount = 0;
        int nonBlockingTerminalCount = 0;

        foreach (var cr in checkRuns)
        {
            var conc = cr.Conclusion?.ToLowerInvariant();
            if (conc == "success")
            {
                successCount++;
            }
            else if (conc is "neutral" or "skipped")
            {
                nonBlockingTerminalCount++;
            }
        }

        foreach (var cs in commitStatuses)
        {
            var st = cs.State?.ToLowerInvariant();
            if (st == "success")
            {
                successCount++;
            }
        }

        if (successCount > 0)
        {
            return ExecutionCiStatus.Success;
        }

        if (nonBlockingTerminalCount > 0)
        {
            return ExecutionCiStatus.Neutral;
        }

        return ExecutionCiStatus.Unknown;
    }

    private static string BoundString(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length > maxLen ? trimmed[..maxLen] : trimmed;
    }

    private static GitHubSyncResultDto Success(
        ExecutionPullRequestRemoteState remoteState,
        ExecutionPullRequestIntegrityStatus integrityStatus,
        DateTime? closedAt,
        DateTime? mergedAt,
        ExecutionCiStatus ciStatus,
        IReadOnlyList<ExecutionCiCheck> checks) =>
        new(
            Success: true,
            IsConfigurationError: false,
            IsExternalFailure: false,
            ErrorMessage: null,
            RemoteState: remoteState,
            IntegrityStatus: integrityStatus,
            ClosedAt: closedAt,
            MergedAt: mergedAt,
            CiStatus: ciStatus,
            Checks: checks);

    private static GitHubSyncResultDto Failure(
        string errorMessage,
        bool isConfigurationError = false,
        bool isExternalFailure = false) =>
        new(
            Success: false,
            IsConfigurationError: isConfigurationError,
            IsExternalFailure: isExternalFailure,
            ErrorMessage: errorMessage,
            RemoteState: ExecutionPullRequestRemoteState.Unknown,
            IntegrityStatus: ExecutionPullRequestIntegrityStatus.Unknown,
            ClosedAt: null,
            MergedAt: null,
            CiStatus: ExecutionCiStatus.Unknown,
            Checks: Array.Empty<ExecutionCiCheck>());
}
