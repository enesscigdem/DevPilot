using System.Diagnostics;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class GitExecutionPushServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task PushExecutionBranch_RemoteBranchAbsent_PushesExactCommitShaToBareRemote()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var (worktreePath, bareRemotePath, commitSha, branchName) = await SetupLocalGitRepoWithBareRemoteAsync(executionId);

        var repository = new InMemoryExecutionRepository();
        var service = new GitExecutionPushService(repository, NullLogger<GitExecutionPushService>.Instance);

        var execution = CreateExecution(executionId, worktreePath, branchName, commitSha);

        // Act
        var result = await service.PushExecutionBranchAsync(execution, Guid.NewGuid());

        // Assert
        result.Success.Should().BeTrue();
        result.RemoteCommitSha.Should().Be(commitSha);

        // Verify bare remote received the branch ref at exact commitSha
        var remoteSha = await RunGitAsync(bareRemotePath, "rev-parse", $"refs/heads/{branchName}");
        remoteSha.Trim().Should().Be(commitSha);
    }

    [Fact]
    public async Task PushExecutionBranch_RemoteBranchAlreadyAtSameSha_ReturnsIdempotentSuccess()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var (worktreePath, bareRemotePath, commitSha, branchName) = await SetupLocalGitRepoWithBareRemoteAsync(executionId);

        // Initial push to bare remote
        await RunGitAsync(worktreePath, "push", "origin", $"{commitSha}:refs/heads/{branchName}");

        var repository = new InMemoryExecutionRepository();
        var service = new GitExecutionPushService(repository, NullLogger<GitExecutionPushService>.Instance);

        var execution = CreateExecution(executionId, worktreePath, branchName, commitSha);

        // Act
        var result = await service.PushExecutionBranchAsync(execution, Guid.NewGuid());

        // Assert
        result.Success.Should().BeTrue();
        result.IsAlreadyPushed.Should().BeTrue();
        result.RemoteCommitSha.Should().Be(commitSha);
    }

    [Fact]
    public async Task PushExecutionBranch_RemoteBranchAtDifferentSha_ReturnsConflictWithoutPushing()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var (worktreePath, bareRemotePath, commitSha, branchName) = await SetupLocalGitRepoWithBareRemoteAsync(executionId);

        // Push initial commit as branch to bare remote
        var firstCommit = await RunGitAsync(worktreePath, "rev-parse", "HEAD~1");
        await RunGitAsync(worktreePath, "push", "origin", $"{firstCommit.Trim()}:refs/heads/{branchName}");

        var repository = new InMemoryExecutionRepository();
        var service = new GitExecutionPushService(repository, NullLogger<GitExecutionPushService>.Instance);

        var execution = CreateExecution(executionId, worktreePath, branchName, commitSha);

        // Act
        var result = await service.PushExecutionBranchAsync(execution, Guid.NewGuid());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already exists at a different commit SHA");

        // Remote branch must remain at firstCommit, NOT overwritten
        var remoteSha = await RunGitAsync(bareRemotePath, "rev-parse", $"refs/heads/{branchName}");
        remoteSha.Trim().Should().Be(firstCommit.Trim());
    }

    private async Task<(string WorktreePath, string BareRemotePath, string CommitSha, string BranchName)> SetupLocalGitRepoWithBareRemoteAsync(Guid executionId)
    {
        var tempBase = Path.Combine(Path.GetTempPath(), $"devpilot_push_test_{Guid.NewGuid():N}");
        var bareRemotePath = Path.Combine(tempBase, "remote.git");
        var worktreePath = Path.Combine(tempBase, "worktree");

        Directory.CreateDirectory(bareRemotePath);
        Directory.CreateDirectory(worktreePath);
        _tempDirs.Add(tempBase);

        // Init bare remote
        await RunGitAsync(bareRemotePath, "init", "--bare");

        // Init worktree
        await RunGitAsync(worktreePath, "init");
        await RunGitAsync(worktreePath, "config", "user.name", "DevPilot Test");
        await RunGitAsync(worktreePath, "config", "user.email", "devpilot@test.local");

        // First commit
        var file1 = Path.Combine(worktreePath, "file1.txt");
        await File.WriteAllTextAsync(file1, "initial content");
        await RunGitAsync(worktreePath, "add", "file1.txt");
        await RunGitAsync(worktreePath, "commit", "-m", "initial commit");

        // Create execution branch
        var branchName = "devpilot/exec-test-9999";
        await RunGitAsync(worktreePath, "checkout", "-b", branchName);

        // Second commit (execution commit with trailer)
        var file2 = Path.Combine(worktreePath, "file2.txt");
        await File.WriteAllTextAsync(file2, "execution content");
        await RunGitAsync(worktreePath, "add", "file2.txt");
        await RunGitAsync(worktreePath, "commit", "-m", "devpilot: execution commit", "-m", $"DevPilot-Execution: {executionId}");

        var commitSha = (await RunGitAsync(worktreePath, "rev-parse", "HEAD")).Trim();

        // Add remote origin pointing to local bare remote
        await RunGitAsync(worktreePath, "remote", "add", "origin", bareRemotePath);

        return (worktreePath, bareRemotePath, commitSha, branchName);
    }

    private static TaskExecution CreateExecution(Guid executionId, string worktreePath, string branchName, string commitSha)
    {
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            Title = "Execution push test",
            RepositoryWorkspace = new RepositoryWorkspace
            {
                Id = Guid.NewGuid(),
                Owner = "owner",
                Repository = "repo"
            }
        };

        return new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            CommitStatus = ExecutionCommitStatus.Committed,
            CommitSha = commitSha,
            WorkspacePath = worktreePath,
            BranchName = branchName,
            PushStatus = ExecutionPushStatus.None
        };
    }

    private static async Task<string> RunGitAsync(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return stdout;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    private sealed class InMemoryExecutionRepository : DevPilot.Application.Executions.Ports.IExecutionRepository
    {
        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskExecution?>(null);
        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskExecution>>(Array.Empty<TaskExecution>());
        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetReviewDecisionAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TrySetReviewDecisionWithFingerprintAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string fingerprint, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryClaimNewCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, string baseCommitSha, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStaleCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetCommitCompletedAsync(Guid executionId, Guid attemptId, string commitSha, DateTime committedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetCommitFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryClaimNewPushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStalePushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetPushCompletedAsync(Guid executionId, Guid attemptId, string remoteBranchName, string remoteCommitSha, DateTime pushedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPushFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryClaimNewPullRequestLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStalePullRequestLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetPullRequestOpenedAsync(Guid executionId, Guid attemptId, int pullRequestNumber, string pullRequestUrl, string baseBranch, DateTime createdAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPullRequestFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryClaimPullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStalePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ReleasePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime releasedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ReplacePullRequestTrackingSnapshotAsync(Guid executionId, Guid attemptId, DevPilot.Domain.Enums.ExecutionPullRequestRemoteState remoteState, DevPilot.Domain.Enums.ExecutionPullRequestIntegrityStatus integrityStatus, DateTime? closedAt, DateTime? mergedAt, DevPilot.Domain.Enums.ExecutionCiStatus ciStatus, IReadOnlyList<DevPilot.Domain.Entities.ExecutionCiCheck> checks, DateTime syncedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
