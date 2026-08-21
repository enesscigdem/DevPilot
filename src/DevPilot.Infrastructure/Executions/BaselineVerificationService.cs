using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Evaluates post-change verification failures against clean repository baseline evidence.
/// Only invoked lazily when an authoritative check fails; never runs for passing tasks.
/// Executes narrowest useful checks on isolated clean-base worktrees without mutating task execution worktrees.
/// Concurrent baseline runs for the same (repository, base SHA, check, filter) are deduplicated via the singleton coordinator.
/// </summary>
public sealed class BaselineVerificationService : IBaselineVerificationService
{
    private readonly IBaselineVerificationCoordinator _coordinator;
    private readonly IRepositoryCheckRunner _repositoryCheckRunner;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<BaselineVerificationService> _logger;

    public BaselineVerificationService(
        IBaselineVerificationCoordinator coordinator,
        IRepositoryCheckRunner repositoryCheckRunner,
        IProcessRunner processRunner,
        ILogger<BaselineVerificationService> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _repositoryCheckRunner = repositoryCheckRunner ?? throw new ArgumentNullException(nameof(repositoryCheckRunner));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BaselineFailureComparison> EvaluateTestFailureAsync(
        string workspacePath,
        string sourceRepositoryPath,
        string baseCommitSha,
        RepositoryCheck check,
        RepositoryCheckResult taskCheckResult,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseCommitSha) || string.IsNullOrWhiteSpace(sourceRepositoryPath))
        {
            return InconclusiveComparison(taskCheckResult, "Base commit SHA or source repository path is unavailable.");
        }

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllTestFailures(
            taskCheckResult.StdOut,
            taskCheckResult.StdErr,
            taskCheckResult.ErrorMessage);

        var singleEvidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            taskCheckResult.StdOut,
            taskCheckResult.StdErr,
            taskCheckResult.ErrorMessage);

        // Targeted test probe: if check supports targeted test and test name is reliable, probe narrowest test filter
        string? targetedFilter = (check.SupportsTargetedTest && singleEvidence.HasReliableTestName && taskFailures.Count == 1)
            ? singleEvidence.TestName
            : null;

        var key = new BaselineVerificationKey(
            RepositoryWorkspaceKey: Path.GetFullPath(sourceRepositoryPath).ToLowerInvariant(),
            BaseCommitSha: baseCommitSha.Trim(),
            CheckId: check.Id,
            TargetedTestFilter: targetedFilter);

        var (baselineEvidence, cacheHit, durationMs) = await GetOrExecuteBaselineCheckAsync(
            key,
            sourceRepositoryPath,
            baseCommitSha,
            check,
            targetedFilter,
            isTest: true,
            cancellationToken).ConfigureAwait(false);

        if (baselineEvidence == null)
        {
            return InconclusiveComparison(taskCheckResult, "Baseline check could not be executed or was inconclusive.");
        }

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures,
            baselineEvidence.Failures,
            baselineEvidence.Success);

        return comparison with
        {
            CacheHit = cacheHit,
            BaseCommitSha = baseCommitSha,
            DurationMs = durationMs
        };
    }

    public async Task<BaselineFailureComparison> EvaluateCompilerFailureAsync(
        string workspacePath,
        string sourceRepositoryPath,
        string baseCommitSha,
        RepositoryCheck check,
        RepositoryCheckResult taskCheckResult,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseCommitSha) || string.IsNullOrWhiteSpace(sourceRepositoryPath))
        {
            return InconclusiveComparison(taskCheckResult, "Base commit SHA or source repository path is unavailable.");
        }

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(
            taskCheckResult.StdOut,
            taskCheckResult.StdErr,
            taskCheckResult.ErrorMessage);

        var key = new BaselineVerificationKey(
            RepositoryWorkspaceKey: Path.GetFullPath(sourceRepositoryPath).ToLowerInvariant(),
            BaseCommitSha: baseCommitSha.Trim(),
            CheckId: check.Id,
            TargetedTestFilter: null);

        var (baselineEvidence, cacheHit, durationMs) = await GetOrExecuteBaselineCheckAsync(
            key,
            sourceRepositoryPath,
            baseCommitSha,
            check,
            targetedTestFilter: null,
            isTest: false,
            cancellationToken).ConfigureAwait(false);

        if (baselineEvidence == null)
        {
            return InconclusiveComparison(taskCheckResult, "Baseline build check could not be executed or was inconclusive.");
        }

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures,
            baselineEvidence.Failures,
            baselineEvidence.Success);

        return comparison with
        {
            CacheHit = cacheHit,
            BaseCommitSha = baseCommitSha,
            DurationMs = durationMs
        };
    }

    private Task<(BaselineCheckEvidence? Evidence, bool CacheHit, long DurationMs)> GetOrExecuteBaselineCheckAsync(
        BaselineVerificationKey key,
        string sourceRepositoryPath,
        string baseCommitSha,
        RepositoryCheck check,
        string? targetedTestFilter,
        bool isTest,
        CancellationToken cancellationToken)
    {
        return _coordinator.GetOrExecuteAsync(
            key,
            ct => ExecuteBaselineCheckInternalAsync(
                sourceRepositoryPath,
                baseCommitSha,
                check,
                targetedTestFilter,
                isTest,
                ct),
            cancellationToken);
    }

    private async Task<BaselineCheckEvidence> ExecuteBaselineCheckInternalAsync(
        string sourceRepositoryPath,
        string baseCommitSha,
        RepositoryCheck check,
        string? targetedTestFilter,
        bool isTest,
        CancellationToken cancellationToken)
    {
        var fullSource = Path.GetFullPath(sourceRepositoryPath);
        var repoHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullSource)))[..8].ToLowerInvariant();
        var shortSha = baseCommitSha.Length >= 8 ? baseCommitSha[..8] : baseCommitSha;

        var workspaceRoot = GetWorkspaceRoot(fullSource);
        var baselineWorktreePath = Path.GetFullPath(Path.Combine(workspaceRoot, "baselines", $"{repoHash}_{shortSha}"));

        var repoLock = _coordinator.GetWorkspaceLock(baselineWorktreePath);
        await repoLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Prepare isolated clean baseline worktree at baseCommitSha
            await EnsureCleanBaselineWorktreeAsync(fullSource, baselineWorktreePath, baseCommitSha, cancellationToken).ConfigureAwait(false);

            var checkRequest = new RepositoryCheckExecutionRequest(
                baselineWorktreePath,
                BranchName: string.Empty,
                Check: check,
                SkipBuild: false,
                TestFilter: targetedTestFilter);

            var result = await _repositoryCheckRunner.ExecuteAsync(checkRequest, cancellationToken).ConfigureAwait(false);

            // Clean up verification side effects inside baseline workspace only
            try
            {
                await VerificationSideEffectCleaner.PurgeSideEffectsAsync(
                    baselineWorktreePath,
                    Array.Empty<string>(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Non-critical side-effect cleaner error in baseline workspace: {Path}", baselineWorktreePath);
            }

            var failures = isTest
                ? ExecutionDiagnosticEvidence.ParseAllTestFailures(result.StdOut, result.StdErr, result.ErrorMessage)
                : ExecutionDiagnosticEvidence.ParseAllCompilerFailures(result.StdOut, result.StdErr, result.ErrorMessage);

            return new BaselineCheckEvidence(
                CheckId: check.Id,
                BaseCommitSha: baseCommitSha,
                Success: result.Success,
                Failures: failures,
                ErrorSummary: result.ErrorMessage);
        }
        finally
        {
            repoLock.Release();
        }
    }

    private async Task EnsureCleanBaselineWorktreeAsync(
        string sourceRepositoryPath,
        string baselineWorktreePath,
        string baseCommitSha,
        CancellationToken cancellationToken)
    {
        var parentDir = Path.GetDirectoryName(baselineWorktreePath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        if (Directory.Exists(baselineWorktreePath))
        {
            var gitHead = await RunGitAsync(baselineWorktreePath, new[] { "rev-parse", "HEAD" }, cancellationToken).ConfigureAwait(false);
            if (gitHead.ExitCode == 0 && gitHead.StdOut?.Trim().Equals(baseCommitSha.Trim(), StringComparison.OrdinalIgnoreCase) == true)
            {
                // Clean worktree state back to clean base
                await RunGitAsync(baselineWorktreePath, new[] { "reset", "--hard", "HEAD" }, cancellationToken).ConfigureAwait(false);
                await RunGitAsync(baselineWorktreePath, new[] { "clean", "-fdx" }, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Checkout correct commit
            var checkoutResult = await RunGitAsync(baselineWorktreePath, new[] { "checkout", "--detach", baseCommitSha }, cancellationToken).ConfigureAwait(false);
            if (checkoutResult.ExitCode == 0)
            {
                await RunGitAsync(baselineWorktreePath, new[] { "reset", "--hard", "HEAD" }, cancellationToken).ConfigureAwait(false);
                await RunGitAsync(baselineWorktreePath, new[] { "clean", "-fdx" }, cancellationToken).ConfigureAwait(false);
                return;
            }

            // If checkout failed, remove worktree and recreate
            await RunGitAsync(sourceRepositoryPath, new[] { "worktree", "remove", "--force", baselineWorktreePath }, cancellationToken).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(baselineWorktreePath))
                {
                    Directory.Delete(baselineWorktreePath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to forcefully remove stale baseline directory: {Path}", baselineWorktreePath);
            }
        }

        // Add detached clean worktree at baseCommitSha
        var addResult = await RunGitAsync(
            sourceRepositoryPath,
            new[] { "worktree", "add", "--detach", baselineWorktreePath, baseCommitSha },
            cancellationToken).ConfigureAwait(false);

        if (addResult.ExitCode != 0)
        {
            _logger.LogWarning("Failed to create baseline worktree at {Path} (ExitCode: {Code}): {Err}", baselineWorktreePath, addResult.ExitCode, addResult.StdErr);
            throw new InvalidOperationException($"Could not create baseline worktree at commit {baseCommitSha}: {addResult.StdErr}");
        }
    }

    private async Task<ProcessExecutionResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return await _processRunner.RunProcessAsync(
            fileName: "git",
            arguments: arguments,
            workingDirectory: workingDirectory,
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string GetWorkspaceRoot(string repositoryPath)
    {
        var devpilotDir = Path.Combine(repositoryPath, ".devpilot");
        if (Directory.Exists(devpilotDir))
        {
            return devpilotDir;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "devpilot_workspaces");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static BaselineFailureComparison InconclusiveComparison(RepositoryCheckResult taskResult, string reason)
    {
        var taskFailures = (taskResult.CheckKind == RepositoryCheckKind.Test)
            ? ExecutionDiagnosticEvidence.ParseAllTestFailures(taskResult.StdOut, taskResult.StdErr, taskResult.ErrorMessage)
            : ExecutionDiagnosticEvidence.ParseAllCompilerFailures(taskResult.StdOut, taskResult.StdErr, taskResult.ErrorMessage);

        return new BaselineFailureComparison(
            Classification: BaselineFailureClassification.Unknown,
            PreExistingCount: 0,
            NewRegressionCount: taskFailures.Count,
            ChangedCount: 0,
            PreExistingFailures: Array.Empty<NormalizedFailureItem>(),
            NewRegressions: taskFailures,
            ChangedFailures: Array.Empty<NormalizedFailureItem>(),
            Summary: reason);
    }
}
