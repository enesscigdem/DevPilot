using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Application.Executions.Queries.GetExecutionReview;

public sealed class GetExecutionReviewQueryHandler : IGetExecutionReviewQueryHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IExecutionGitDiffReader _gitDiffReader;
    private readonly IExecutionChangeFingerprintCalculator _fingerprintCalculator;
    private readonly IExecutionActivityRepository _activityRepository;
    private readonly IOptions<MergePolicyOptions> _mergePolicyOptions;
    private readonly ILogger<GetExecutionReviewQueryHandler> _logger;

    public GetExecutionReviewQueryHandler(
        IExecutionRepository executionRepository,
        IExecutionWorkspaceManager workspaceManager,
        IExecutionGitDiffReader gitDiffReader,
        IExecutionChangeFingerprintCalculator fingerprintCalculator,
        IExecutionActivityRepository activityRepository,
        IOptions<MergePolicyOptions> mergePolicyOptions,
        ILogger<GetExecutionReviewQueryHandler> logger)
    {
        _executionRepository = executionRepository;
        _workspaceManager = workspaceManager;
        _gitDiffReader = gitDiffReader;
        _fingerprintCalculator = fingerprintCalculator;
        _activityRepository = activityRepository;
        _mergePolicyOptions = mergePolicyOptions;
        _logger = logger;
    }

    public async Task<GetExecutionReviewResult> HandleAsync(
        GetExecutionReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository
            .GetByIdAsync(query.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return GetExecutionReviewResult.NotFound("Execution not found.");
        }

        if (query.RepositoryWorkspaceId.HasValue &&
            execution.DevelopmentTask?.RepositoryWorkspaceId != query.RepositoryWorkspaceId.Value)
        {
            return GetExecutionReviewResult.NotFound("Execution not found.");
        }

        if (execution.Status == TaskExecutionStatus.Pending || execution.Status == TaskExecutionStatus.Running)
        {
            return GetExecutionReviewResult.Conflict(
                $"Execution is currently {execution.Status} and cannot be reviewed yet.");
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return GetExecutionReviewResult.Conflict(
                "Execution workspace path or branch name is not configured.");
        }

        var verificationResult = await _workspaceManager
            .VerifyWorkspaceStateAsync(
                execution.WorkspacePath,
                execution.BranchName,
                requireClean: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!verificationResult.IsValid)
        {
            return GetExecutionReviewResult.Conflict(
                $"Execution workspace verification failed: {verificationResult.ErrorMessage}");
        }

        var activities = await _activityRepository.GetByExecutionIdAsync(execution.Id, cancellationToken).ConfigureAwait(false);
        var buildPassed = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Completed);
        var testPassed = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Completed);
        var allowNoChecks = _mergePolicyOptions.Value.AllowNoChecks;
        var (canRequestMerge, mergeBlockedReason) = ExecutionMergeEligibility.EvaluateMergeEligibility(execution, allowNoChecks, buildPassed, testPassed);
        var (buildStatus, testStatus) = DetermineStageStatuses(execution, activities);

        if (execution.CommitStatus == ExecutionCommitStatus.Committed)
        {
            var validation = await ValidateCommittedGitIntegrityAsync(
                execution.WorkspacePath,
                execution.Id,
                execution.BaseCommitSha,
                execution.CommitSha,
                execution.ApprovedChangeFingerprint,
                cancellationToken).ConfigureAwait(false);

            if (!validation.IsValid)
            {
                return GetExecutionReviewResult.Conflict(
                    $"Committed execution git integrity check failed: {validation.ErrorMessage}");
            }

            var committedDiffResult = await _gitDiffReader
                .ReadCommittedDiffAsync(execution.WorkspacePath, execution.BaseCommitSha!, execution.CommitSha!, cancellationToken)
                .ConfigureAwait(false);

            if (!committedDiffResult.Success)
            {
                return GetExecutionReviewResult.Conflict(
                    $"Failed to read committed Git diff: {committedDiffResult.ErrorMessage}");
            }

            var committedReview = new ExecutionReviewDto(
                ExecutionId: execution.Id,
                TaskId: execution.DevelopmentTaskId,
                TaskTitle: execution.DevelopmentTask?.Title ?? string.Empty,
                ExecutionStatus: execution.Status.ToString(),
                BranchName: execution.BranchName,
                ChangedFileCount: committedDiffResult.ChangedFiles?.Count ?? 0,
                ChangedFiles: committedDiffResult.ChangedFiles ?? Array.Empty<ExecutionReviewFileDto>(),
                Diff: committedDiffResult.DiffText,
                DiffTruncated: committedDiffResult.DiffTruncated,
                Build: new ExecutionReviewStageStatusDto(buildStatus),
                Test: new ExecutionReviewStageStatusDto(testStatus),
                ReviewStatus: execution.ReviewStatus.ToString(),
                DecidedAt: execution.ReviewDecidedAt,
                RejectionReason: execution.ReviewRejectionReason,
                ChangeFingerprint: validation.CommittedFingerprint!,
                ApprovedSnapshotMatchesCurrent: true,
                CommitEligible: false,
                CommitStatus: execution.CommitStatus.ToString(),
                CommitSha: execution.CommitSha,
                CommittedAt: execution.CommittedAt,
                PushStatus: execution.PushStatus.ToString(),
                RemoteBranchName: execution.RemoteBranchName,
                RemoteCommitSha: execution.RemoteCommitSha,
                PushedAt: execution.PushedAt,
                CanRequestPush: CalculateCanRequestPush(execution),
                PullRequestStatus: execution.PullRequestStatus.ToString(),
                PullRequestNumber: execution.PullRequestNumber,
                PullRequestUrl: execution.PullRequestUrl,
                PullRequestCreatedAt: execution.PullRequestCreatedAt,
                CanRequestPullRequest: DevPilot.Application.Executions.Commands.CreatePullRequest.CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(execution),
                PullRequestRemoteState: execution.PullRequestRemoteState.ToString(),
                PullRequestIntegrityStatus: execution.PullRequestIntegrityStatus.ToString(),
                PullRequestLastSyncedAt: execution.PullRequestLastSyncedAt,
                CiStatus: execution.CiStatus.ToString(),
                CiChecks: (execution.CiChecks ?? Array.Empty<ExecutionCiCheck>())
                    .Select(c => new Commands.SyncPullRequest.ExecutionCiCheckDto(
                        Id: c.Id,
                        ExternalId: c.ExternalId,
                        Name: c.Name,
                        Source: c.Source,
                        CheckType: c.CheckType.ToString(),
                        Status: c.Status,
                        Conclusion: c.Conclusion,
                        StartedAt: c.StartedAt,
                        CompletedAt: c.CompletedAt))
                    .ToList(),
                MergeStatus: execution.MergeStatus.ToString(),
                MergeCommitSha: execution.MergeCommitSha,
                MergedAt: execution.MergedAt,
                CanRequestMerge: canRequestMerge,
                MergeBlockedReason: mergeBlockedReason,
                RepositoryWorkspaceId: execution.DevelopmentTask?.RepositoryWorkspaceId,
                RepositoryOwner: execution.DevelopmentTask?.RepositoryWorkspace?.Owner,
                RepositoryName: execution.DevelopmentTask?.RepositoryWorkspace?.Repository);

            return GetExecutionReviewResult.Ok(committedReview);
        }

        // Bounded snapshot revalidation for uncommitted execution worktree
        ExecutionFingerprintResult fingerprintResult = null!;
        ExecutionGitDiffResult diffResult = null!;
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var fpBefore = await _fingerprintCalculator
                .ComputeFingerprintAsync(execution.WorkspacePath, cancellationToken)
                .ConfigureAwait(false);

            diffResult = await _gitDiffReader
                .ReadWorkspaceDiffAsync(execution.WorkspacePath, execution.BranchName, cancellationToken)
                .ConfigureAwait(false);

            var fpAfter = await _fingerprintCalculator
                .ComputeFingerprintAsync(execution.WorkspacePath, cancellationToken)
                .ConfigureAwait(false);

            if (fpBefore.Success && fpAfter.Success && fpBefore.Fingerprint == fpAfter.Fingerprint)
            {
                fingerprintResult = fpBefore;
                break;
            }

            if (attempt == maxAttempts)
            {
                fingerprintResult = fpAfter;
            }
        }

        if (!diffResult.Success)
        {
            return GetExecutionReviewResult.Conflict(
                $"Failed to read Git execution review diff: {diffResult.ErrorMessage}");
        }

        var currentFingerprint = fingerprintResult.Fingerprint ?? string.Empty;
        var approvedMatchesCurrent = true;

        if (execution.ReviewStatus == ExecutionReviewStatus.Approved && !string.IsNullOrEmpty(execution.ApprovedChangeFingerprint))
        {
            approvedMatchesCurrent = string.Equals(execution.ApprovedChangeFingerprint, currentFingerprint, StringComparison.Ordinal);
        }

        var isApproved = execution.ReviewStatus == ExecutionReviewStatus.Approved;
        var isCommitted = execution.CommitStatus == ExecutionCommitStatus.Committed;
        var commitEligible = isApproved
                             && approvedMatchesCurrent
                             && !isCommitted
                             && !fingerprintResult.HasSensitiveFiles
                             && (diffResult.ChangedFiles?.Count ?? 0) > 0;

        var review = new ExecutionReviewDto(
            ExecutionId: execution.Id,
            TaskId: execution.DevelopmentTaskId,
            TaskTitle: execution.DevelopmentTask?.Title ?? string.Empty,
            ExecutionStatus: execution.Status.ToString(),
            BranchName: execution.BranchName,
            ChangedFileCount: diffResult.ChangedFiles?.Count ?? 0,
            ChangedFiles: diffResult.ChangedFiles ?? Array.Empty<ExecutionReviewFileDto>(),
            Diff: diffResult.DiffText,
            DiffTruncated: diffResult.DiffTruncated,
            Build: new ExecutionReviewStageStatusDto(buildStatus),
            Test: new ExecutionReviewStageStatusDto(testStatus),
            ReviewStatus: execution.ReviewStatus.ToString(),
            DecidedAt: execution.ReviewDecidedAt,
            RejectionReason: execution.ReviewRejectionReason,
            ChangeFingerprint: currentFingerprint,
            ApprovedSnapshotMatchesCurrent: approvedMatchesCurrent,
            CommitEligible: commitEligible,
            CommitStatus: execution.CommitStatus.ToString(),
            CommitSha: execution.CommitSha,
            CommittedAt: execution.CommittedAt,
            PushStatus: execution.PushStatus.ToString(),
            RemoteBranchName: execution.RemoteBranchName,
            RemoteCommitSha: execution.RemoteCommitSha,
            PushedAt: execution.PushedAt,
            CanRequestPush: CalculateCanRequestPush(execution),
            PullRequestStatus: execution.PullRequestStatus.ToString(),
            PullRequestNumber: execution.PullRequestNumber,
            PullRequestUrl: execution.PullRequestUrl,
            PullRequestCreatedAt: execution.PullRequestCreatedAt,
            CanRequestPullRequest: DevPilot.Application.Executions.Commands.CreatePullRequest.CreatePullRequestCommandHandler.CalculateCanRequestPullRequest(execution),
            PullRequestRemoteState: execution.PullRequestRemoteState.ToString(),
            PullRequestIntegrityStatus: execution.PullRequestIntegrityStatus.ToString(),
            PullRequestLastSyncedAt: execution.PullRequestLastSyncedAt,
            CiStatus: execution.CiStatus.ToString(),
            CiChecks: (execution.CiChecks ?? Array.Empty<ExecutionCiCheck>())
                .Select(c => new Commands.SyncPullRequest.ExecutionCiCheckDto(
                    Id: c.Id,
                    ExternalId: c.ExternalId,
                    Name: c.Name,
                    Source: c.Source,
                    CheckType: c.CheckType.ToString(),
                    Status: c.Status,
                    Conclusion: c.Conclusion,
                    StartedAt: c.StartedAt,
                    CompletedAt: c.CompletedAt))
                .ToList(),
            MergeStatus: execution.MergeStatus.ToString(),
            MergeCommitSha: execution.MergeCommitSha,
            MergedAt: execution.MergedAt,
            CanRequestMerge: canRequestMerge,
            MergeBlockedReason: mergeBlockedReason,
            RepositoryWorkspaceId: execution.DevelopmentTask?.RepositoryWorkspaceId,
            RepositoryOwner: execution.DevelopmentTask?.RepositoryWorkspace?.Owner,
            RepositoryName: execution.DevelopmentTask?.RepositoryWorkspace?.Repository);

        return GetExecutionReviewResult.Ok(review);
    }

    private static (string BuildStatus, string TestStatus) DetermineStageStatuses(
        Domain.Entities.TaskExecution execution,
        IReadOnlyList<ExecutionActivity> activities)
    {
        var buildPassed = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Completed);
        var buildFailed = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Failed);
        var buildStarted = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Started);

        var testPassed = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Completed);
        var testFailed = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Failed);
        var testStarted = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Started);

        string buildStatus;
        if (buildPassed || (execution.Status == TaskExecutionStatus.Completed && !buildFailed))
        {
            buildStatus = "Passed";
        }
        else if (buildFailed)
        {
            buildStatus = "Failed";
        }
        else if (buildStarted)
        {
            buildStatus = "Running";
        }
        else
        {
            buildStatus = "Unknown";
        }

        string testStatus;
        if (testPassed || (execution.Status == TaskExecutionStatus.Completed && !testFailed))
        {
            testStatus = "Passed";
        }
        else if (testFailed)
        {
            testStatus = "Failed";
        }
        else if (testStarted)
        {
            testStatus = "Running";
        }
        else
        {
            testStatus = "Unknown";
        }

        return (buildStatus, testStatus);
    }

    private static bool CalculateCanRequestPush(Domain.Entities.TaskExecution execution)
    {
        return execution.ReviewStatus == ExecutionReviewStatus.Approved &&
               execution.CommitStatus == ExecutionCommitStatus.Committed &&
               (execution.PushStatus == ExecutionPushStatus.None || execution.PushStatus == ExecutionPushStatus.Failed);
    }

    private static readonly System.Text.RegularExpressions.Regex GitShaRegex =
        new(@"^[0-9a-fA-F]{40}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task<(bool IsValid, string? ErrorMessage, string? CommitTreeSha, string? CommittedFingerprint)> ValidateCommittedGitIntegrityAsync(
        string workspacePath,
        Guid executionId,
        string? baseCommitSha,
        string? commitSha,
        string? approvedChangeFingerprint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseCommitSha) || !GitShaRegex.IsMatch(baseCommitSha))
        {
            return (false, "Persisted BaseCommitSha is missing or invalid SHA format.", null, null);
        }

        if (string.IsNullOrWhiteSpace(commitSha) || !GitShaRegex.IsMatch(commitSha))
        {
            return (false, "Persisted CommitSha is missing or invalid SHA format.", null, null);
        }

        if (string.IsNullOrWhiteSpace(approvedChangeFingerprint))
        {
            return (false, "Persisted ApprovedChangeFingerprint is missing.", null, null);
        }

        // 1. Validate BaseCommitSha object type is 'commit'
        var baseType = await GetGitObjectTypeAsync(workspacePath, baseCommitSha, cancellationToken).ConfigureAwait(false);
        if (baseType != "commit")
        {
            return (false, $"BaseCommitSha '{baseCommitSha}' is not a valid Git commit object.", null, null);
        }

        // 2. Validate CommitSha object type is 'commit'
        var commitType = await GetGitObjectTypeAsync(workspacePath, commitSha, cancellationToken).ConfigureAwait(false);
        if (commitType != "commit")
        {
            return (false, $"CommitSha '{commitSha}' is not a valid Git commit object.", null, null);
        }

        // 3. Verify parent of CommitSha is BaseCommitSha
        var parentSha = await RunGitCommandOutputAsync(workspacePath, cancellationToken, "rev-parse", $"{commitSha}^").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(parentSha) || !string.Equals(parentSha, baseCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"CommitSha '{commitSha}' parent commit does not match BaseCommitSha '{baseCommitSha}'.", null, null);
        }

        // 4. Verify exact DevPilot-Execution trailer
        var commitMessage = await RunGitCommandOutputAsync(workspacePath, cancellationToken, "log", "-1", "--format=%B", commitSha).ConfigureAwait(false);
        if (!HasExactExecutionTrailer(commitMessage, executionId))
        {
            return (false, $"CommitSha '{commitSha}' does not contain exact DevPilot-Execution trailer for execution '{executionId}'.", null, null);
        }

        // 5. Resolve commitTreeSha ONLY AFTER CommitSha is confirmed as a commit
        var commitTreeSha = await RunGitCommandOutputAsync(workspacePath, cancellationToken, "rev-parse", $"{commitSha}^{{tree}}").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(commitTreeSha) || !GitShaRegex.IsMatch(commitTreeSha))
        {
            return (false, $"Failed to resolve tree SHA for commit '{commitSha}'.", null, null);
        }

        // 6. Compute committed tree fingerprint
        var fpResult = await _fingerprintCalculator
            .ComputeStagedTreeFingerprintAsync(workspacePath, commitTreeSha, baseCommitSha, cancellationToken)
            .ConfigureAwait(false);

        if (!fpResult.Success || string.IsNullOrEmpty(fpResult.Fingerprint))
        {
            return (false, $"Failed to compute committed tree change fingerprint: {fpResult.ErrorMessage}", null, null);
        }

        if (!string.Equals(fpResult.Fingerprint, approvedChangeFingerprint, StringComparison.Ordinal))
        {
            return (false, "Committed candidate tree fingerprint does not match approved change fingerprint.", null, null);
        }

        return (true, null, commitTreeSha, fpResult.Fingerprint);
    }

    private static bool HasExactExecutionTrailer(string? commitMessage, Guid executionId)
    {
        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            return false;
        }

        var expectedValue = executionId.ToString();
        var lines = commitMessage.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("DevPilot-Execution:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed.Substring("DevPilot-Execution:".Length).Trim();
                if (string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<string?> GetGitObjectTypeAsync(string workspacePath, string objectSha, CancellationToken cancellationToken)
    {
        var output = await RunGitCommandOutputAsync(workspacePath, cancellationToken, "cat-file", "-t", objectSha).ConfigureAwait(false);
        return output?.Trim();
    }

    private static async Task<string?> RunGitCommandOutputAsync(string workspacePath, CancellationToken cancellationToken, params string[] arguments)
    {
        using var psiCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, psiCts.Token);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspacePath
        };

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.directory=*");

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new System.Diagnostics.Process { StartInfo = psi };

        try
        {
            process.Start();
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                var output = await stdOutTask.ConfigureAwait(false);
                return output.Trim();
            }
        }
        catch
        {
            // Controlled error fallback
        }

        return null;
    }
}
