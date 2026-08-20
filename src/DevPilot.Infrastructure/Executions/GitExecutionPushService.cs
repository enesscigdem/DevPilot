using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.GitProviders;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class GitExecutionPushService : IExecutionGitPushService
{
    private static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromSeconds(30);

    private readonly IExecutionRepository _executionRepository;
    private readonly IGitHubAppTokenService _tokenService;
    private readonly ILogger<GitExecutionPushService> _logger;

    public GitExecutionPushService(
        IExecutionRepository executionRepository,
        IGitHubAppTokenService tokenService,
        ILogger<GitExecutionPushService> logger)
    {
        _executionRepository = executionRepository;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<ExecutionPushResult> PushExecutionBranchAsync(
        TaskExecution execution,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        var branchName = execution.BranchName ?? string.Empty;

        // 1. Idempotency check on DB state
        if (execution.PushStatus == ExecutionPushStatus.Pushed && !string.IsNullOrEmpty(execution.RemoteCommitSha))
        {
            return new ExecutionPushResult(
                Success: true,
                IsAlreadyPushed: true,
                RemoteBranchName: execution.RemoteBranchName ?? branchName,
                RemoteCommitSha: execution.RemoteCommitSha,
                PushedAt: execution.PushedAt ?? DateTime.UtcNow);
        }

        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || !Directory.Exists(execution.WorkspacePath))
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionPushResult(false, ErrorMessage: $"Workspace path does not exist: '{execution.WorkspacePath}'.");
        }

        var fullWorkspacePath = Path.GetFullPath(execution.WorkspacePath);
        var expectedCommitSha = execution.CommitSha ?? string.Empty;

        if (string.IsNullOrWhiteSpace(expectedCommitSha))
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionPushResult(false, ErrorMessage: "Execution commit SHA is missing.");
        }

        // 2. Validate BranchName ref-format and forbid master/main
        if (string.IsNullOrWhiteSpace(branchName) ||
            string.Equals(branchName, "master", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(branchName, "main", StringComparison.OrdinalIgnoreCase))
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionPushResult(false, ErrorMessage: $"Branch '{branchName}' is forbidden or invalid for remote push.");
        }

        var checkBranch = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "check-ref-format", "--branch", branchName).ConfigureAwait(false);
        if (!checkBranch.IsSuccess)
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionPushResult(false, ErrorMessage: $"Invalid branch name format: '{branchName}'.");
        }

        // 3. Verify workspace current branch & HEAD SHA match persisted CommitSha
        var currentBranchCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (!currentBranchCmd.IsSuccess || currentBranchCmd.StdOut.Trim() != branchName)
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionPushResult(false, ErrorMessage: $"Worktree is on branch '{currentBranchCmd.StdOut.Trim()}', expected '{branchName}'.");
        }

        var currentHeadCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "rev-parse", "HEAD").ConfigureAwait(false);
        if (!currentHeadCmd.IsSuccess || currentHeadCmd.StdOut.Trim() != expectedCommitSha)
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionPushResult(false, ErrorMessage: $"Worktree HEAD '{currentHeadCmd.StdOut.Trim()}' does not match committed commit SHA '{expectedCommitSha}'.");
        }

        // 4. Verify trailer integrity
        var logMsgCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "log", "-1", "--format=%B", expectedCommitSha).ConfigureAwait(false);
        var logMsg = logMsgCmd.StdOut.Trim();
        if (!logMsgCmd.IsSuccess || !logMsg.Contains(execution.Id.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionPushResult(false, ErrorMessage: "Execution commit trailer integrity verification failed.");
        }

        // 5. Verify origin repository identity matches DevelopmentTask workspace owner/name
        var repoOwner = execution.DevelopmentTask?.RepositoryWorkspace?.Owner ?? string.Empty;
        var repoName = execution.DevelopmentTask?.RepositoryWorkspace?.Repository ?? string.Empty;

        var originUrlCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, null, "remote", "get-url", "origin").ConfigureAwait(false);
        if (!originUrlCmd.IsSuccess || string.IsNullOrWhiteSpace(originUrlCmd.StdOut))
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionPushResult(false, ErrorMessage: "Remote origin is not configured in execution workspace.");
        }

        var originUrl = originUrlCmd.StdOut.Trim();
        if (!GitRemoteUrlNormalizer.MatchesRepository(originUrl, repoOwner, repoName))
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            return new ExecutionPushResult(false, ErrorMessage: "Remote origin repository URL does not match configured task repository owner and name.");
        }

        // 6. Resolve transient installation token for remote operations
        var tokenResult = await _tokenService.GetTokenForRepositoryAsync(repoOwner, repoName, cancellationToken).ConfigureAwait(false);
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
            var errMsg = tokenResult.FailureKind switch
            {
                GitHubTokenFailureKind.Disconnected => "Connect GitHub to push execution branch.",
                GitHubTokenFailureKind.RepositoryUnauthorized => $"DevPilot does not have access to repository '{repoOwner}/{repoName}'. Please update repository permissions.",
                GitHubTokenFailureKind.InstallationInvalidOrRevoked => "GitHub connection has expired or been revoked. Please reconnect GitHub.",
                _ => tokenResult.ErrorMessage ?? "Repository authorization failed for remote push."
            };
            return new ExecutionPushResult(false, ErrorMessage: errMsg);
        }

        string? tempHome = null;
        Dictionary<string, string>? env = null;

        try
        {
            tempHome = GitAuthenticationHelper.CreateTransientHomeDirectory(tokenResult.Token);
            env = new Dictionary<string, string>
            {
                ["HOME"] = tempHome,
                ["USERPROFILE"] = tempHome,
                ["GIT_CONFIG_NOSYSTEM"] = "1"
            };

            // 7. Preflight remote branch inspection via ls-remote
            var remoteRef = $"refs/heads/{branchName}";
            var lsRemoteCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, env, "ls-remote", "--heads", "origin", remoteRef).ConfigureAwait(false);

            if (!lsRemoteCmd.IsSuccess)
            {
                await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                var sanitizedErr = GitRemoteUrlNormalizer.SanitizeOutput(lsRemoteCmd.StdErr);
                return new ExecutionPushResult(false, ErrorMessage: $"Failed to inspect remote branch status: {sanitizedErr}");
            }

            var lsOutput = lsRemoteCmd.StdOut.Trim();
            if (!string.IsNullOrWhiteSpace(lsOutput))
            {
                var parts = lsOutput.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var remoteSha = parts.Length > 0 ? parts[0].Trim() : string.Empty;

                if (string.Equals(remoteSha, expectedCommitSha, StringComparison.OrdinalIgnoreCase))
                {
                    // Remote branch already exists at exact CommitSha (Idempotent Recovery)
                    var now = DateTime.UtcNow;
                    await _executionRepository.SetPushCompletedAsync(execution.Id, attemptId, branchName, expectedCommitSha, now, cancellationToken).ConfigureAwait(false);
                    return new ExecutionPushResult(Success: true, IsAlreadyPushed: true, RemoteBranchName: branchName, RemoteCommitSha: expectedCommitSha, PushedAt: now);
                }
                else
                {
                    // Remote branch exists at a different SHA -> Conflict!
                    await _executionRepository.SetPushFailedAsync(execution.Id, attemptId, cancellationToken).ConfigureAwait(false);
                    return new ExecutionPushResult(false, ErrorMessage: $"Remote branch '{branchName}' already exists at a different commit SHA '{remoteSha}'. Direct push refused.");
                }
            }

            // 8. Execute direct refspec push strictly as <CommitSha>:refs/heads/<BranchName>
            var pushCmd = await RunGitCommandAsync(
                fullWorkspacePath,
                cancellationToken,
                env,
                "push", "--porcelain", "origin", $"{expectedCommitSha}:{remoteRef}").ConfigureAwait(false);

            // 9. Post-push remote ref verification
            var postLsRemoteCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, env, "ls-remote", "--heads", "origin", remoteRef).ConfigureAwait(false);
            var postLsOutput = postLsRemoteCmd.StdOut.Trim();
            var postRemoteSha = string.Empty;
            if (!string.IsNullOrWhiteSpace(postLsOutput))
            {
                var parts = postLsOutput.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                postRemoteSha = parts.Length > 0 ? parts[0].Trim() : string.Empty;
            }

            if (string.Equals(postRemoteSha, expectedCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                var pushedAt = DateTime.UtcNow;
                await _executionRepository.SetPushCompletedAsync(execution.Id, attemptId, branchName, expectedCommitSha, pushedAt, cancellationToken).ConfigureAwait(false);
                return new ExecutionPushResult(Success: true, RemoteBranchName: branchName, RemoteCommitSha: expectedCommitSha, PushedAt: pushedAt);
            }

            // Push outcome is uncertain or failed without verification -> keep InProgress for retry recovery
            var sanitizedPushError = GitRemoteUrlNormalizer.SanitizeOutput(
                !string.IsNullOrWhiteSpace(pushCmd.StdErr) ? pushCmd.StdErr : "Remote branch SHA post-verification failed.");
            _logger.LogWarning("Remote push for execution {ExecutionId} did not pass post-verification: {Error}", execution.Id, sanitizedPushError);

            return new ExecutionPushResult(false, ErrorMessage: $"Remote push execution failed or could not be verified: {sanitizedPushError}");
        }
        finally
        {
            GitAuthenticationHelper.TryDeleteDirectory(tempHome);
        }
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

        GitAuthenticationHelper.ApplyEnvironment(psi, null);
        psi.EnvironmentVariables["GPG_TTY"] = "";

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
                // Best effort kill
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
