using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class SyncAndMergeRaceTests
{
    [Fact]
    public async Task CrossOperationRace_OldSyncFinishingLate_CannotOverwriteConfirmedMergedState()
    {
        var repo = new InMemoryExecutionRepository();

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = Guid.NewGuid(),
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            CommitStatus = ExecutionCommitStatus.Committed,
            CommitSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341",
            PushStatus = ExecutionPushStatus.Pushed,
            RemoteBranchName = "devpilot/task-race",
            RemoteCommitSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341",
            BranchName = "devpilot/task-race",
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            PullRequestNumber = 10,
            PullRequestUrl = "https://github.com/owner/repo/pull/10",
            PullRequestBaseBranch = "master",
            PullRequestRemoteState = ExecutionPullRequestRemoteState.Open,
            PullRequestIntegrityStatus = ExecutionPullRequestIntegrityStatus.Valid,
            MergeStatus = ExecutionMergeStatus.None
        };
        repo.Seed(execution);

        // 1. Sync A starts and claims sync lease
        var syncAttemptId = Guid.NewGuid();
        var syncClaimed = await repo.TryClaimPullRequestSyncLeaseAsync(execution.Id, syncAttemptId, DateTime.UtcNow);
        syncClaimed.Should().BeTrue();

        // 2. Merge B starts and claims merge lease
        var mergeAttemptId = Guid.NewGuid();
        var mergeClaimed = await repo.TryClaimMergeLeaseAsync(execution.Id, mergeAttemptId, DateTime.UtcNow, TimeSpan.Zero);
        mergeClaimed.Should().BeTrue();

        // 3. Merge B confirms remote merge and persists Merged state
        var mergedAt = DateTime.UtcNow;
        await repo.SetExecutionMergedAsync(execution.Id, mergeAttemptId, "mergecommit999", mergedAt, "merge");

        var afterMerge = await repo.GetByIdAsync(execution.Id);
        afterMerge!.MergeStatus.Should().Be(ExecutionMergeStatus.Merged);
        afterMerge.PullRequestRemoteState.Should().Be(ExecutionPullRequestRemoteState.Merged);

        // 4. Old Sync A finishes late and tries to write its stale "Open" snapshot
        var replaced = await repo.ReplacePullRequestTrackingSnapshotAsync(
            execution.Id,
            syncAttemptId,
            ExecutionPullRequestRemoteState.Open, // Stale snapshot from before merge
            ExecutionPullRequestIntegrityStatus.Valid,
            closedAt: null,
            mergedAt: null,
            ExecutionCiStatus.Success,
            Array.Empty<ExecutionCiCheck>(),
            DateTime.UtcNow);

        replaced.Should().BeFalse("Old sync snapshot must not be applied over confirmed Merged state");

        var afterLateSync = await repo.GetByIdAsync(execution.Id);
        afterLateSync!.PullRequestRemoteState.Should().Be(ExecutionPullRequestRemoteState.Merged);
        afterLateSync.MergeStatus.Should().Be(ExecutionMergeStatus.Merged);
        afterLateSync.MergeCommitSha.Should().Be("mergecommit999");
    }

    [Fact]
    public async Task CrossOperationRace_NewSyncAfterMerge_CanSuccessfullyPersistMergedState()
    {
        var repo = new InMemoryExecutionRepository();

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = Guid.NewGuid(),
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            CommitStatus = ExecutionCommitStatus.Committed,
            CommitSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341",
            PushStatus = ExecutionPushStatus.Pushed,
            RemoteBranchName = "devpilot/task-race2",
            RemoteCommitSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341",
            BranchName = "devpilot/task-race2",
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            PullRequestNumber = 11,
            PullRequestUrl = "https://github.com/owner/repo/pull/11",
            PullRequestBaseBranch = "master",
            PullRequestRemoteState = ExecutionPullRequestRemoteState.Merged,
            PullRequestIntegrityStatus = ExecutionPullRequestIntegrityStatus.Valid,
            MergeStatus = ExecutionMergeStatus.Merged,
            MergeCommitSha = "mergecommit999",
            MergedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        repo.Seed(execution);

        // New Sync C after merge claims sync lease
        var newSyncAttemptId = Guid.NewGuid();
        var syncClaimed = await repo.TryClaimPullRequestSyncLeaseAsync(execution.Id, newSyncAttemptId, DateTime.UtcNow);
        syncClaimed.Should().BeTrue();

        // New Sync C persists its updated Merged snapshot
        var newMergedAt = DateTime.UtcNow.AddMinutes(-10);
        var replaced = await repo.ReplacePullRequestTrackingSnapshotAsync(
            execution.Id,
            newSyncAttemptId,
            ExecutionPullRequestRemoteState.Merged, // Live GitHub read returns Merged
            ExecutionPullRequestIntegrityStatus.Valid,
            closedAt: null,
            mergedAt: newMergedAt,
            ExecutionCiStatus.Success,
            Array.Empty<ExecutionCiCheck>(),
            DateTime.UtcNow);

        replaced.Should().BeTrue("New sync reading remote Merged state should be allowed");

        var afterNewSync = await repo.GetByIdAsync(execution.Id);
        afterNewSync!.PullRequestRemoteState.Should().Be(ExecutionPullRequestRemoteState.Merged);
    }
}
