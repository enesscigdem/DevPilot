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
            taskCheckResult.ErrorMessage,
            workspaceRoot: workspacePath);

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

        (BaselineCheckEvidence? baselineEvidence, bool cacheHit, long durationMs) = (null, false, 0);
        try
        {
            (baselineEvidence, cacheHit, durationMs) = await GetOrExecuteBaselineCheckAsync(
                key,
                sourceRepositoryPath,
                baseCommitSha,
                check,
                targetedFilter,
                isTest: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Baseline check execution failed with exception for check {CheckId}: {Message}", check.Id, ex.Message);
            return InconclusiveComparison(taskCheckResult, $"Baseline check execution failed: {ex.Message}");
        }

        if (baselineEvidence == null)
        {
            return InconclusiveComparison(taskCheckResult, "Baseline check could not be executed or was inconclusive.");
        }

        if (!baselineEvidence.Success && baselineEvidence.Failures.Count == 0)
        {
            return InconclusiveComparison(taskCheckResult, $"Baseline check infrastructure failure: {baselineEvidence.ErrorSummary ?? "unknown error"}");
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
            taskCheckResult.ErrorMessage,
            workspaceRoot: workspacePath);

        var key = new BaselineVerificationKey(
            RepositoryWorkspaceKey: Path.GetFullPath(sourceRepositoryPath).ToLowerInvariant(),
            BaseCommitSha: baseCommitSha.Trim(),
            CheckId: check.Id,
            TargetedTestFilter: null);

        (BaselineCheckEvidence? baselineEvidence, bool cacheHit, long durationMs) = (null, false, 0);
        try
        {
            (baselineEvidence, cacheHit, durationMs) = await GetOrExecuteBaselineCheckAsync(
                key,
                sourceRepositoryPath,
                baseCommitSha,
                check,
                targetedTestFilter: null,
                isTest: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Baseline check execution failed with exception for check {CheckId}: {Message}", check.Id, ex.Message);
            return InconclusiveComparison(taskCheckResult, $"Baseline check execution failed: {ex.Message}");
        }

        if (baselineEvidence == null)
        {
            return InconclusiveComparison(taskCheckResult, "Baseline check could not be executed or was inconclusive.");
        }

        if (!baselineEvidence.Success && baselineEvidence.Failures.Count == 0)
        {
            return InconclusiveComparison(taskCheckResult, $"Baseline check infrastructure failure: {baselineEvidence.ErrorSummary ?? "unknown error"}");
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

            // Every base SHA gets its own worktree (and its own node_modules), so old ones are pruned in the background.
            _ = Task.Run(() => PruneStaleBaselineWorktreesAsync(fullSource, Path.GetDirectoryName(baselineWorktreePath)!, repoHash, baselineWorktreePath));

            var checkRequest = new RepositoryCheckExecutionRequest(
                baselineWorktreePath,
                BranchName: "HEAD",
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

            if (result.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure)
            {
                _logger.LogWarning("Baseline check had infrastructure failure: {Err}", result.ErrorMessage);
                // Return empty failure item indicating infrastructure error so it is classified as Unknown
                return new BaselineCheckEvidence(
                    CheckId: check.Id,
                    BaseCommitSha: baseCommitSha,
                    Success: false,
                    Failures: Array.Empty<NormalizedFailureItem>(),
                    ErrorSummary: $"Infrastructure failure: {result.ErrorMessage}");
            }

            var failures = isTest
                ? ExecutionDiagnosticEvidence.ParseAllTestFailures(result.StdOut, result.StdErr, result.ErrorMessage, workspaceRoot: baselineWorktreePath)
                : ExecutionDiagnosticEvidence.ParseAllCompilerFailures(result.StdOut, result.StdErr, result.ErrorMessage, workspaceRoot: baselineWorktreePath);

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

    /// <summary>How many baseline worktrees per repository stay on disk (the one just used plus the newest others).</summary>
    internal const int RetainedBaselineWorktrees = 2;

    /// <summary>
    /// Removes baseline worktrees of this repository beyond the newest <see cref="RetainedBaselineWorktrees"/>. A worktree
    /// that is in use (its lock is held) is skipped, and any failure only means it is retried on the next baseline run.
    /// Other repositories' baselines are left alone: their worktrees are registered with their own source repository.
    /// </summary>
    private async Task PruneStaleBaselineWorktreesAsync(
        string sourceRepositoryPath,
        string baselinesRoot,
        string repoHash,
        string currentWorktreePath)
    {
        try
        {
            var current = Path.GetFullPath(currentWorktreePath);
            var stale = new DirectoryInfo(baselinesRoot)
                .EnumerateDirectories(repoHash + "_*")
                .Where(dir => !string.Equals(Path.GetFullPath(dir.FullName), current, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(dir => dir.LastWriteTimeUtc)
                .Skip(RetainedBaselineWorktrees - 1)
                .ToList();

            foreach (var dir in stale)
            {
                var worktreeLock = _coordinator.GetWorkspaceLock(Path.GetFullPath(dir.FullName));
                if (!await worktreeLock.WaitAsync(0).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    await RunGitAsync(sourceRepositoryPath, new[] { "worktree", "remove", "--force", dir.FullName }, CancellationToken.None).ConfigureAwait(false);
                    if (Directory.Exists(dir.FullName))
                    {
                        Directory.Delete(dir.FullName, recursive: true);
                    }

                    _logger.LogInformation("Removed stale baseline worktree {Path}.", dir.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not remove stale baseline worktree {Path}; it will be retried.", dir.FullName);
                }
                finally
                {
                    worktreeLock.Release();
                }
            }

            if (stale.Count > 0)
            {
                await RunGitAsync(sourceRepositoryPath, new[] { "worktree", "prune" }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Baseline worktree pruning failed; it will be retried on the next baseline run.");
        }
    }

    private async Task EnsureCleanBaselineWorktreeAsync(
        string sourceRepositoryPath,
        string baselineWorktreePath,
        string baseCommitSha,
        CancellationToken cancellationToken)
    {
        var parentDir = Path.GetDirectoryName(baselineWorktreePath);
        if (!string.IsNullOrWhiteSpace(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        if (Directory.Exists(baselineWorktreePath))
        {
            var gitHead = await RunGitAsync(baselineWorktreePath, new[] { "rev-parse", "HEAD" }, cancellationToken).ConfigureAwait(false);
            var gitAbbrev = await RunGitAsync(baselineWorktreePath, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, cancellationToken).ConfigureAwait(false);
            var isCorrectSha = gitHead.ExitCode == 0 && gitHead.StdOut?.Trim().Equals(baseCommitSha.Trim(), StringComparison.OrdinalIgnoreCase) == true;
            var isDetached = gitAbbrev.ExitCode == 0 && gitAbbrev.StdOut?.Trim().Equals("HEAD", StringComparison.OrdinalIgnoreCase) == true;

            if (isCorrectSha && isDetached)
            {
                // Clean worktree state back to clean base. The worktree path is keyed by the base SHA, so its installed
                // node_modules always match the base lockfile and are kept to avoid a full reinstall on every baseline run.
                await RunGitAsync(baselineWorktreePath, new[] { "reset", "--hard", "HEAD" }, cancellationToken).ConfigureAwait(false);
                await RunGitAsync(baselineWorktreePath, new[] { "clean", "-fdx", "-e", "node_modules" }, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Checkout correct commit detached
            var checkoutResult = await RunGitAsync(baselineWorktreePath, new[] { "checkout", "--detach", baseCommitSha.Trim() }, cancellationToken).ConfigureAwait(false);
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
        return new BaselineFailureComparison(
            Classification: BaselineFailureClassification.Unknown,
            PreExistingCount: 0,
            NewRegressionCount: 0,
            ChangedCount: 0,
            PreExistingFailures: Array.Empty<NormalizedFailureItem>(),
            NewRegressions: Array.Empty<NormalizedFailureItem>(),
            ChangedFailures: Array.Empty<NormalizedFailureItem>(),
            Summary: reason);
    }
}
