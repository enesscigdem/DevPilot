using System.ComponentModel;
using System.Diagnostics;
using DevPilot.Application.Executions.Ports;
using DevPilot.Infrastructure.RepositoryClone;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevPilot.Infrastructure.Executions;

public sealed class GitExecutionWorkspaceManager : IExecutionWorkspaceManager
{
    private readonly IOptions<RepositoryCloneOptions> _cloneOptions;
    private readonly ILogger<GitExecutionWorkspaceManager> _logger;

    public GitExecutionWorkspaceManager(
        IOptions<RepositoryCloneOptions> cloneOptions,
        ILogger<GitExecutionWorkspaceManager> logger)
    {
        _cloneOptions = cloneOptions;
        _logger = logger;
    }

    public async Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(
        Guid executionId,
        Guid taskId,
        string sourceRepositoryLocalPath,
        string? sourceBranch = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceRepositoryLocalPath))
        {
            return Failure("Source repository local path is required.");
        }

        var sourcePath = Path.GetFullPath(sourceRepositoryLocalPath);
        if (!Directory.Exists(sourcePath))
        {
            return Failure($"Source repository path does not exist on disk: '{sourcePath}'.");
        }

        // 1. Verify source repository is a valid Git repository
        var (isRepo, repoOut, repoError) = await RunGitCommandAsync(sourcePath, cancellationToken, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
        if (!isRepo || repoOut?.Trim() != "true")
        {
            return Failure($"Source repository path '{sourcePath}' is not a valid Git repository. Error: {repoError}");
        }

        // 2. Explicitly capture original repository's branch BEFORE worktree creation
        var (initialBranchSuccess, initialBranchName, initialBranchError) = await RunGitCommandAsync(sourcePath, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (!initialBranchSuccess || string.IsNullOrWhiteSpace(initialBranchName))
        {
            return Failure($"Failed to determine initial branch of source repository at '{sourcePath}': {initialBranchError}");
        }
        var initialBranch = initialBranchName.Trim();

        // 3. Determine target workspace path (outside source repository)
        var workspaceRoot = GetWorkspaceRoot(sourcePath);
        var targetWorkspacePath = Path.GetFullPath(Path.Combine(workspaceRoot, "executions", executionId.ToString()));

        // Ensure target path is outside the source repository checkout
        if (IsWithinPath(targetWorkspacePath, sourcePath))
        {
            return Failure($"Target execution workspace path '{targetWorkspacePath}' cannot be nested inside source repository '{sourcePath}'.");
        }

        // Safety check: fail if workspace directory already exists
        if (Directory.Exists(targetWorkspacePath))
        {
            return Failure($"Execution workspace path already exists at '{targetWorkspacePath}'. Overwriting or reusing existing workspaces is prohibited.");
        }

        // 4. Determine deterministic safe branch name
        var shortTaskId = taskId.ToString("N")[..8];
        var shortExecutionId = executionId.ToString("N")[..8];
        var targetBranchName = $"devpilot/task-{shortTaskId}-{shortExecutionId}";

        // Safety check: fail if target branch already exists
        var (branchExists, _, _) = await RunGitCommandAsync(sourcePath, cancellationToken, "show-ref", "--verify", $"refs/heads/{targetBranchName}").ConfigureAwait(false);
        if (branchExists)
        {
            return Failure($"Target branch '{targetBranchName}' already exists in source repository. Overwriting or resetting existing branches is prohibited.");
        }

        // 5. Create worktree
        Directory.CreateDirectory(Path.GetDirectoryName(targetWorkspacePath)!);

        var startPoint = !string.IsNullOrWhiteSpace(sourceBranch) ? sourceBranch : initialBranch;
        var (worktreeCreated, worktreeOutput, worktreeError) = await RunGitCommandAsync(
            sourcePath,
            cancellationToken,
            "worktree",
            "add",
            "-b",
            targetBranchName,
            targetWorkspacePath,
            startPoint).ConfigureAwait(false);

        if (!worktreeCreated)
        {
            _logger.LogError("Failed to create Git worktree at '{Path}': {Error}", targetWorkspacePath, worktreeError);
            return Failure($"Failed to create Git worktree: {worktreeError ?? worktreeOutput}");
        }

        // 6. Post-creation Verifications
        if (!Directory.Exists(targetWorkspacePath))
        {
            return Failure($"Created execution workspace directory does not exist at '{targetWorkspacePath}'.");
        }

        // Verify checked out branch in execution workspace
        var (execBranchSuccess, execBranchName, execBranchError) = await RunGitCommandAsync(targetWorkspacePath, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (!execBranchSuccess || execBranchName?.Trim() != targetBranchName)
        {
            return Failure($"Execution workspace at '{targetWorkspacePath}' is on branch '{execBranchName?.Trim()}', expected '{targetBranchName}'. Error: {execBranchError}");
        }

        // Explicitly verify original managed repository's branch remains UNCHANGED after worktree creation
        var (postBranchSuccess, postBranchName, postBranchError) = await RunGitCommandAsync(sourcePath, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (!postBranchSuccess || postBranchName?.Trim() != initialBranch)
        {
            return Failure($"Original repository branch was unexpectedly modified from '{initialBranch}' to '{postBranchName?.Trim()}'. Error: {postBranchError}");
        }

        _logger.LogInformation(
            "Execution workspace prepared successfully. " +
            "ExecutionId: {ExecutionId}, TaskId: {TaskId}, Path: '{Path}', Branch: '{Branch}', SourceRepo: '{SourceRepo}' (Branch: '{SourceBranch}').",
            executionId,
            taskId,
            targetWorkspacePath,
            targetBranchName,
            sourcePath,
            initialBranch);

        // Determine exact base commit SHA of the newly created worktree
        var (_, baseShaOut, _) = await RunGitCommandAsync(targetWorkspacePath, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        var baseCommitSha = baseShaOut?.Trim();

        return new ExecutionWorkspaceResult(
            WorkspacePath: targetWorkspacePath,
            BranchName: targetBranchName,
            Success: true,
            BaseCommitSha: baseCommitSha);
    }

    public async Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(
        string workspacePath,
        string expectedBranchName,
        bool requireClean = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return new WorkspaceVerificationResult(
                IsValid: false, WorkspaceExists: false, BranchMatches: false, IsClean: false,
                ErrorMessage: "Execution workspace path is empty.");
        }

        if (string.IsNullOrWhiteSpace(expectedBranchName))
        {
            return new WorkspaceVerificationResult(
                IsValid: false, WorkspaceExists: Directory.Exists(workspacePath), BranchMatches: false, IsClean: false,
                ErrorMessage: "Expected branch name is empty.");
        }

        var fullPath = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(fullPath))
        {
            return new WorkspaceVerificationResult(
                IsValid: false, WorkspaceExists: false, BranchMatches: false, IsClean: false,
                ErrorMessage: $"Execution workspace directory does not exist on disk: '{fullPath}'.");
        }

        var (branchOk, currentBranch, branchError) = await RunGitCommandAsync(fullPath, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (!branchOk || string.IsNullOrWhiteSpace(currentBranch))
        {
            return new WorkspaceVerificationResult(
                IsValid: false, WorkspaceExists: true, BranchMatches: false, IsClean: false,
                ErrorMessage: $"Failed to determine Git branch at '{fullPath}': {branchError}");
        }

        var actualBranch = currentBranch.Trim();
        if (!string.Equals(actualBranch, expectedBranchName.Trim(), StringComparison.Ordinal))
        {
            return new WorkspaceVerificationResult(
                IsValid: false, WorkspaceExists: true, BranchMatches: false, IsClean: false,
                ErrorMessage: $"Execution workspace at '{fullPath}' is on branch '{actualBranch}', expected '{expectedBranchName}'.");
        }

        var (statusOk, statusOutput, statusError) = await RunGitCommandAsync(fullPath, cancellationToken, "status", "--porcelain").ConfigureAwait(false);
        if (!statusOk)
        {
            return new WorkspaceVerificationResult(
                IsValid: false, WorkspaceExists: true, BranchMatches: true, IsClean: false,
                ErrorMessage: $"Failed to check Git worktree status at '{fullPath}': {statusError}");
        }

        var isClean = string.IsNullOrWhiteSpace(statusOutput);
        if (!isClean && requireClean)
        {
            return new WorkspaceVerificationResult(
                IsValid: false, WorkspaceExists: true, BranchMatches: true, IsClean: false,
                ErrorMessage: $"Execution worktree at '{fullPath}' contains uncommitted or untracked changes.");
        }

        return new WorkspaceVerificationResult(
            IsValid: true, WorkspaceExists: true, BranchMatches: true, IsClean: isClean);
    }

    private static ExecutionWorkspaceResult Failure(string errorMessage)
    {
        return new ExecutionWorkspaceResult(
            WorkspacePath: string.Empty,
            BranchName: string.Empty,
            Success: false,
            ErrorMessage: errorMessage);
    }

    private string GetWorkspaceRoot(string sourcePath)
    {
        var configured = _cloneOptions.Value.WorkspaceRoot;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        // Fall back to sibling of source path directory or AppData fallback
        var parent = Directory.GetParent(sourcePath)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            return parent;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DevPilot",
            "Workspaces");

        return Path.GetFullPath(fallback);
    }

    private static bool IsWithinPath(string targetPath, string basePath)
    {
        var normalizedTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedTarget.Equals(normalizedBase, StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(bool IsSuccess, string? StdOut, string? StdErr)> RunGitCommandAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

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
            return (false, null, $"Git executable not found: {ex.Message}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup
            }
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        return (process.ExitCode == 0, output, error);
    }
}
