using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class GitExecutionCommitService : IExecutionGitCommitService
{
    private static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromSeconds(30);
    private readonly IExecutionChangeFingerprintCalculator _fingerprintCalculator;
    private readonly IExecutionRepository _executionRepository;
    private readonly ILogger<GitExecutionCommitService> _logger;

    public GitExecutionCommitService(
        IExecutionChangeFingerprintCalculator fingerprintCalculator,
        IExecutionRepository executionRepository,
        ILogger<GitExecutionCommitService> logger)
    {
        _fingerprintCalculator = fingerprintCalculator;
        _executionRepository = executionRepository;
        _logger = logger;
    }

    public async Task<ExecutionCommitResult> CommitApprovedExecutionAsync(
        TaskExecution execution,
        string taskTitle,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        if (execution.CommitStatus == ExecutionCommitStatus.Committed && !string.IsNullOrEmpty(execution.CommitSha))
        {
            return new ExecutionCommitResult(
                Success: true,
                IsAlreadyCommitted: true,
                CommitSha: execution.CommitSha,
                CommittedAt: execution.CommittedAt ?? DateTime.UtcNow);
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || !Directory.Exists(execution.WorkspacePath))
        {
            return new ExecutionCommitResult(false, ErrorMessage: $"Workspace path does not exist: '{execution.WorkspacePath}'.");
        }

        var fullWorkspacePath = Path.GetFullPath(execution.WorkspacePath);
        var branchName = execution.BranchName ?? "";

        // 1. Verify branch name format
        var checkBranch = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "check-ref-format", "--branch", branchName).ConfigureAwait(false);
        if (!checkBranch.IsSuccess)
        {
            await _executionRepository.SetCommitFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionCommitResult(false, ErrorMessage: $"Invalid branch name format: '{branchName}'.");
        }

        // 2. Verify current checked out branch is execution branch
        var currentBranchCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (!currentBranchCmd.IsSuccess || currentBranchCmd.StdOut.Trim() != branchName)
        {
            await _executionRepository.SetCommitFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionCommitResult(false, ErrorMessage: $"Execution worktree is on branch '{currentBranchCmd.StdOut.Trim()}', expected '{branchName}'.");
        }

        var baseCommitSha = execution.BaseCommitSha ?? "";
        if (string.IsNullOrWhiteSpace(baseCommitSha))
        {
            var headCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "rev-parse", "HEAD").ConfigureAwait(false);
            if (!headCmd.IsSuccess)
            {
                await _executionRepository.SetCommitFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return new ExecutionCommitResult(false, ErrorMessage: $"Failed to determine HEAD: {headCmd.StdErr}");
            }
            baseCommitSha = headCmd.StdOut.Trim();
        }

        // 3. Check for crash recovery on HEAD
        var headCheckCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "rev-parse", "HEAD").ConfigureAwait(false);
        var currentHead = headCheckCmd.IsSuccess ? headCheckCmd.StdOut.Trim() : "";

        if (!string.IsNullOrEmpty(currentHead) && currentHead != baseCommitSha)
        {
            var parentCheckCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "rev-parse", "HEAD~1").ConfigureAwait(false);
            var parentSha = parentCheckCmd.IsSuccess ? parentCheckCmd.StdOut.Trim() : "";

            if (parentSha == baseCommitSha)
            {
                var trailerCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "log", "-1", "--format=%(trailers:key=DevPilot-Execution,valueonly)").ConfigureAwait(false);
                var trailerValue = trailerCmd.StdOut.Trim();

                if (trailerValue == execution.Id.ToString() || trailerValue.Contains(execution.Id.ToString()))
                {
                    // Crash Recovery Success!
                    await SynchronizeNormalIndexAsync(fullWorkspacePath, currentHead, cancellationToken).ConfigureAwait(false);
                    var decidedAt = DateTime.UtcNow;
                    await _executionRepository.SetCommitCompletedAsync(execution.Id, attemptId, currentHead, decidedAt, cancellationToken).ConfigureAwait(false);
                    return new ExecutionCommitResult(Success: true, IsAlreadyCommitted: true, CommitSha: currentHead, CommittedAt: decidedAt);
                }
            }

            return new ExecutionCommitResult(false, ErrorMessage: $"Execution branch HEAD '{currentHead}' does not match base '{baseCommitSha}' or valid recovery commit.");
        }

        // 4. Staged Snapshot Freeze via Isolated Alternate Index
        var tempIndexFile = Path.Combine(Path.GetTempPath(), $"devpilot_commit_index_{attemptId:N}.tmp");
        var envVars = new Dictionary<string, string>
        {
            { "GIT_INDEX_FILE", tempIndexFile }
        };

        try
        {
            // Initialize alternate index from BaseCommitSha
            var readTreeCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, envVars, "read-tree", baseCommitSha).ConfigureAwait(false);
            if (!readTreeCmd.IsSuccess)
            {
                await _executionRepository.SetCommitFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return new ExecutionCommitResult(false, ErrorMessage: $"Failed to initialize alternate git index: {readTreeCmd.StdErr}");
            }

            // Stage worktree changes into alternate index
            var addCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, envVars, "add", "-A", "--", ".").ConfigureAwait(false);
            if (!addCmd.IsSuccess)
            {
                await _executionRepository.SetCommitFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return new ExecutionCommitResult(false, ErrorMessage: $"Failed to stage worktree changes into alternate index: {addCmd.StdErr}");
            }

            // Freeze tree SHA
            var writeTreeCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, envVars, "write-tree").ConfigureAwait(false);
            if (!writeTreeCmd.IsSuccess || string.IsNullOrWhiteSpace(writeTreeCmd.StdOut))
            {
                await _executionRepository.SetCommitFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return new ExecutionCommitResult(false, ErrorMessage: $"Failed to write staged tree: {writeTreeCmd.StdErr}");
            }

            var treeSha = writeTreeCmd.StdOut.Trim();

            // Recompute fingerprint of frozen staged tree
            var stagedFingerprintResult = await _fingerprintCalculator.ComputeStagedTreeFingerprintAsync(
                fullWorkspacePath,
                treeSha,
                baseCommitSha,
                cancellationToken).ConfigureAwait(false);

            if (!stagedFingerprintResult.Success || string.IsNullOrEmpty(stagedFingerprintResult.Fingerprint))
            {
                await _executionRepository.SetCommitFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return new ExecutionCommitResult(false, ErrorMessage: stagedFingerprintResult.ErrorMessage ?? "Failed to compute staged tree fingerprint.");
            }

            if (stagedFingerprintResult.Fingerprint != execution.ApprovedChangeFingerprint)
            {
                await _executionRepository.SetCommitFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return new ExecutionCommitResult(false, ErrorMessage: "Worktree content changed during commit staging. Fingerprint mismatch.");
            }

            // Sanitize subject for commit message
            var sanitizedSubject = SanitizeCommitSubject(taskTitle);
            var commitMsgSubject = $"devpilot: {sanitizedSubject}";
            var commitMsgTrailer = $"DevPilot-Execution: {execution.Id}";

            // Run git commit-tree
            var commitTreeCmd = await RunGitCommandAsync(
                fullWorkspacePath,
                cancellationToken,
                null,
                "commit-tree",
                treeSha,
                "-p", baseCommitSha,
                "-m", commitMsgSubject,
                "-m", commitMsgTrailer).ConfigureAwait(false);

            if (!commitTreeCmd.IsSuccess || string.IsNullOrWhiteSpace(commitTreeCmd.StdOut))
            {
                await _executionRepository.SetCommitFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                return new ExecutionCommitResult(false, ErrorMessage: $"Failed to create commit object: {commitTreeCmd.StdErr}");
            }

            var newCommitSha = commitTreeCmd.StdOut.Trim();

            // From this point onward: branch ref update may succeed! Do NOT set Failed if subsequent steps fail.
            var updateRefCmd = await RunGitCommandAsync(
                fullWorkspacePath,
                cancellationToken,
                null,
                "update-ref",
                $"refs/heads/{branchName}",
                newCommitSha,
                baseCommitSha).ConfigureAwait(false);

            if (!updateRefCmd.IsSuccess)
            {
                _logger.LogError("git update-ref failed for execution {ExecutionId}: {Error}", execution.Id, updateRefCmd.StdErr);
                // Retain InProgress state for recovery inspection
                return new ExecutionCommitResult(false, ErrorMessage: $"Failed to update branch reference: {updateRefCmd.StdErr}");
            }

            // Synchronize normal worktree index (index-only, no -u)
            await SynchronizeNormalIndexAsync(fullWorkspacePath, newCommitSha, cancellationToken).ConfigureAwait(false);

            // Persist completion state
            var committedAt = DateTime.UtcNow;
            await _executionRepository.SetCommitCompletedAsync(execution.Id, attemptId, newCommitSha, committedAt, cancellationToken).ConfigureAwait(false);

            return new ExecutionCommitResult(Success: true, CommitSha: newCommitSha, CommittedAt: committedAt);
        }
        finally
        {
            if (File.Exists(tempIndexFile))
            {
                try
                {
                    File.Delete(tempIndexFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temporary index file '{Path}'", tempIndexFile);
                }
            }
        }
    }

    private static async Task SynchronizeNormalIndexAsync(string workspacePath, string commitSha, CancellationToken cancellationToken)
    {
        // git read-tree <commitSha> updates the normal index to match commitSha without touching worktree files
        await RunGitCommandAsync(workspacePath, cancellationToken, null, "read-tree", commitSha).ConfigureAwait(false);
    }

    private static string SanitizeCommitSubject(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "update changes";
        }

        // Replace control/newline characters with space
        var cleaned = Regex.Replace(title, @"[\r\n\t\x00-\x1F\x7F]", " ").Trim();
        if (cleaned.Length > 72)
        {
            cleaned = cleaned[..72].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "update changes" : cleaned;
    }

    private static async Task<GitCommandResult> RunGitCommandAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables,
        params string[] arguments)
    {
        using var timeoutCts = new CancellationTokenSource(DefaultGitTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        if (environmentVariables != null)
        {
            foreach (var kvp in environmentVariables)
            {
                psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new GitCommandResult(false, Array.Empty<byte>(), "", $"Git executable not found: {ex.Message}");
        }

        using var msOut = new MemoryStream();
        var msOutTask = process.StandardOutput.BaseStream.CopyToAsync(msOut, linkedCts.Token);
        var errTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            await Task.WhenAll(msOutTask, errTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort
            }
            return new GitCommandResult(false, Array.Empty<byte>(), "", "Git operation timed out or was cancelled.");
        }

        var rawBytes = msOut.ToArray();
        var stdOutText = Encoding.UTF8.GetString(rawBytes);
        var stdErrText = await errTask.ConfigureAwait(false);

        return new GitCommandResult(process.ExitCode == 0, rawBytes, stdOutText, stdErrText ?? "");
    }

    private sealed record GitCommandResult(bool IsSuccess, byte[] RawBytes, string StdOut, string StdErr);
}
