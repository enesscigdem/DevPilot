using DevPilot.Application.Executions.Commands.RequestExecutionChanges;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class RequestExecutionChangesCommandTests
{
    private const string InitialBase = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // ---- request side -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Request_EmptyFeedback_IsABadRequest()
    {
        var fx = new Fixture();
        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, "   "));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.BadRequest);
        fx.Dispatcher.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_FeedbackOverTheLimit_IsABadRequest()
    {
        var fx = new Fixture();
        var feedback = new string('x', RequestExecutionChangesCommandHandler.MaxFeedbackLength + 1);

        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, feedback));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.BadRequest);
        fx.Dispatcher.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_UnknownExecution_IsNotFound()
    {
        var fx = new Fixture();
        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(Guid.NewGuid(), "fix it"));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.NotFound);
    }

    [Fact]
    public async Task Request_ForAnotherWorkspace_IsNotFound()
    {
        var fx = new Fixture();
        var result = await fx.Handler.RequestAsync(
            new RequestExecutionChangesCommand(fx.Execution.Id, "fix it", Guid.NewGuid()));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.NotFound);
        fx.Dispatcher.Enqueued.Should().BeEmpty();
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    public async Task Request_OnAnExecutionThatIsNotCompleted_IsAConflict(TaskExecutionStatus status)
    {
        var fx = new Fixture();
        fx.Execution.Status = status;

        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, "fix it"));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Conflict);
        fx.Dispatcher.Enqueued.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ExecutionPullRequestRemoteState.Merged)]
    [InlineData(ExecutionPullRequestRemoteState.Closed)]
    public async Task Request_AfterThePullRequestIsMergedOrClosed_IsAConflict(ExecutionPullRequestRemoteState state)
    {
        var fx = new Fixture();
        fx.Execution.PullRequestRemoteState = state;

        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, "fix it"));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("merged or closed");
    }

    [Fact]
    public async Task Request_AfterMerge_IsAConflict()
    {
        var fx = new Fixture();
        fx.Execution.MergeStatus = ExecutionMergeStatus.Merged;

        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, "fix it"));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Conflict);
    }

    [Fact]
    public async Task Request_WhileAPushIsRunning_IsAConflict()
    {
        var fx = new Fixture();
        fx.Execution.PushStatus = ExecutionPushStatus.InProgress;

        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, "fix it"));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("delivery step");
    }

    [Fact]
    public async Task Request_Eligible_TakesTheLeaseStoresTheFeedbackAndQueuesTheFix()
    {
        var fx = new Fixture();

        var result = await fx.Handler.RequestAsync(
            new RequestExecutionChangesCommand(fx.Execution.Id, "  Use a constant for the timeout.\u0007  ", fx.WorkspaceId));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Accepted);
        result.RevisionNumber.Should().Be(1);
        fx.Dispatcher.Enqueued.Should().ContainSingle();
        fx.Dispatcher.Enqueued[0].ExecutionId.Should().Be(fx.Execution.Id);
        fx.Execution.Status.Should().Be(TaskExecutionStatus.Running);
        fx.Execution.LeaseToken.Should().Be(fx.Dispatcher.Enqueued[0].LeaseToken);
        fx.Execution.LastChangeRequest.Should().Be("Use a constant for the timeout.");
        fx.Execution.LastChangeRequestResult.Should().BeNull();
        fx.Recorder.Messages.Should().Contain(m => m.Contains("Changes requested (revision 1)"));
    }

    [Fact]
    public async Task Request_WhenTheFixCannotBeQueued_RestoresTheExecution()
    {
        var fx = new Fixture();
        fx.Dispatcher.Throw = true;

        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, "fix it"));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Failed);
        fx.Execution.Status.Should().Be(TaskExecutionStatus.Completed);
        fx.Execution.LeaseToken.Should().BeNull();
        fx.Execution.ReviewStatus.Should().Be(ExecutionReviewStatus.Approved, "nothing changed, so the approval stays");
    }

    [Fact]
    public async Task Request_WhenTheClaimIsLost_IsAConflict()
    {
        var fx = new Fixture();
        fx.Store.RefuseClaim = true;

        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, "fix it"));

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Conflict);
        fx.Dispatcher.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_CountsRevisionsAfterEarlierOnes()
    {
        var fx = new Fixture();
        fx.Execution.ChangeRequestCount = 2;

        var result = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, "again"));

        result.RevisionNumber.Should().Be(3);
    }

    [Fact]
    public void NormalizeFeedback_DropsControlCharactersButKeepsLineBreaks()
    {
        RequestExecutionChangesCommandHandler.NormalizeFeedback("a\u0000b\nc\td\u001b ")
            .Should().Be("ab\nc\td");
        RequestExecutionChangesCommandHandler.NormalizeFeedback("\u0000 \u0007").Should().BeNull();
        RequestExecutionChangesCommandHandler.NormalizeFeedback(null).Should().BeNull();
    }

    // ---- background side ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Execute_WithAnotherLease_IsSkipped()
    {
        var fx = await Fixture.RequestedAsync();

        var result = await fx.Handler.ExecuteAsync(fx.Execution.Id, Guid.NewGuid());

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Skipped);
        fx.Processor.Contexts.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_GivesTheFeedbackAndTheExistingWorktreeToTheProcessor()
    {
        var fx = await Fixture.RequestedAsync("Rename the helper.");

        await fx.Handler.ExecuteAsync(fx.Execution.Id, fx.Execution.LeaseToken!.Value);

        var context = fx.Processor.Contexts.Should().ContainSingle().Subject;
        context.IsRevision.Should().BeTrue();
        context.ChangeRequest!.Feedback.Should().Be("Rename the helper.");
        context.ChangeRequest.RevisionNumber.Should().Be(1);
        context.VerifyOnlyWorkspace!.WorkspacePath.Should().Be(fx.Execution.WorkspacePath);
        context.VerifyOnlyWorkspace.BranchName.Should().Be(fx.Execution.BranchName);
        // Baseline comparisons use the repository base, not the commit that was delivered earlier.
        context.VerifyOnlyWorkspace.BaseCommitSha.Should().Be(InitialBase);
    }

    [Fact]
    public async Task Execute_WhenTheCodeChanged_RemovesTheApprovalAndTheDeliveryMarkersButKeepsThePullRequest()
    {
        var fx = await Fixture.RequestedAsync();
        fx.Processor.OnProcess = () => fx.Fingerprints.Current = "after";

        var result = await fx.Handler.ExecuteAsync(fx.Execution.Id, fx.Execution.LeaseToken!.Value);

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Accepted);
        var e = fx.Execution;
        e.Status.Should().Be(TaskExecutionStatus.Completed);
        e.ReviewStatus.Should().Be(ExecutionReviewStatus.Pending);
        e.ApprovedChangeFingerprint.Should().BeNull();
        e.ReviewDecidedAt.Should().BeNull();
        e.CommitStatus.Should().Be(ExecutionCommitStatus.None);
        e.PushStatus.Should().Be(ExecutionPushStatus.None);
        e.CiStatus.Should().Be(ExecutionCiStatus.Unknown);
        e.RevisionCount.Should().Be(1);
        e.PullRequestStatus.Should().Be(ExecutionPullRequestStatus.Open, "the same pull request is updated");
        e.PullRequestNumber.Should().Be(42);
        e.RemoteCommitSha.Should().Be("delivered-sha", "the next push fast-forwards from the last pushed commit");
        e.LastChangeRequestResult.Should().StartWith("Applied");
        fx.Recorder.Messages.Should().Contain(m => m.Contains("approval cleared"));
    }

    [Fact]
    public async Task Execute_WhenTheCodeDidNotChange_KeepsTheEarlierDecision()
    {
        var fx = await Fixture.RequestedAsync();

        await fx.Handler.ExecuteAsync(fx.Execution.Id, fx.Execution.LeaseToken!.Value);

        var e = fx.Execution;
        e.Status.Should().Be(TaskExecutionStatus.Completed);
        e.ReviewStatus.Should().Be(ExecutionReviewStatus.Approved);
        e.CommitStatus.Should().Be(ExecutionCommitStatus.Committed);
        e.PushStatus.Should().Be(ExecutionPushStatus.Pushed);
        e.RevisionCount.Should().Be(0);
        e.LastChangeRequestResult.Should().StartWith("No change");
    }

    [Fact]
    public async Task Execute_WhenTheFixFails_ReportsItAndLeavesAnUnchangedExecutionAlone()
    {
        var fx = await Fixture.RequestedAsync();
        fx.Processor.Throw = new InvalidOperationException("The AI answer was not valid JSON.");

        var result = await fx.Handler.ExecuteAsync(fx.Execution.Id, fx.Execution.LeaseToken!.Value);

        result.Status.Should().Be(RequestExecutionChangesResultStatus.Accepted);
        var e = fx.Execution;
        e.Status.Should().Be(TaskExecutionStatus.Completed);
        e.ReviewStatus.Should().Be(ExecutionReviewStatus.Approved);
        e.PushStatus.Should().Be(ExecutionPushStatus.Pushed);
        e.LastChangeRequestResult.Should().StartWith("Failed:").And.Contain("not valid JSON");
        fx.Recorder.Messages.Should().Contain(m => m.StartsWith("Requested fix failed"));
    }

    [Fact]
    public async Task Execute_WhenTheFixFailsAfterEditingFiles_StillRemovesTheApproval()
    {
        var fx = await Fixture.RequestedAsync();
        fx.Processor.OnProcess = () => fx.Fingerprints.Current = "half-done";
        fx.Processor.Throw = new InvalidOperationException("Build validation failed.");

        await fx.Handler.ExecuteAsync(fx.Execution.Id, fx.Execution.LeaseToken!.Value);

        var e = fx.Execution;
        e.ReviewStatus.Should().Be(ExecutionReviewStatus.Pending, "the tree no longer matches what was approved");
        e.CommitStatus.Should().Be(ExecutionCommitStatus.None);
        e.PushStatus.Should().Be(ExecutionPushStatus.None);
        e.LastChangeRequestResult.Should().StartWith("Failed:");
    }

    [Fact]
    public async Task Execute_TakesASnapshotBeforeAndAfterTheFix_SoTheFixDiffCanBeShown()
    {
        var fx = await Fixture.RequestedAsync();
        fx.Processor.OnProcess = () => fx.Fingerprints.Current = "after";

        await fx.Handler.ExecuteAsync(fx.Execution.Id, fx.Execution.LeaseToken!.Value);

        fx.Snapshots.Labels.Should().Equal("r1-base", "r1-result");
        fx.Execution.RevisionBaseSnapshotSha.Should().Be("snapshot-r1-base");
        fx.Execution.RevisionResultSnapshotSha.Should().Be("snapshot-r1-result");
    }

    [Fact]
    public async Task Execute_BeforeAnyDelivery_AlsoSendsAnApprovedReviewBackToPending()
    {
        var fx = new Fixture();
        fx.Execution.CommitStatus = ExecutionCommitStatus.None;
        fx.Execution.PushStatus = ExecutionPushStatus.None;
        fx.Execution.PullRequestStatus = ExecutionPullRequestStatus.None;
        fx.Execution.PullRequestNumber = null;
        await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, "fix it"));
        fx.Processor.OnProcess = () => fx.Fingerprints.Current = "after";

        await fx.Handler.ExecuteAsync(fx.Execution.Id, fx.Execution.LeaseToken!.Value);

        fx.Execution.ReviewStatus.Should().Be(ExecutionReviewStatus.Pending);
        fx.Execution.ApprovedChangeFingerprint.Should().BeNull();
        fx.Execution.RevisionCount.Should().Be(1);
    }

    // ---- fixtures -----------------------------------------------------------------------------------------------

    private sealed class Fixture
    {
        public Guid WorkspaceId { get; } = Guid.NewGuid();
        public InMemoryExecutionRepository Repository { get; } = new();
        public FakeRevisionStore Store { get; }
        public FakeProcessor Processor { get; } = new();
        public FakeDispatcher Dispatcher { get; } = new();
        public FakeRecorder Recorder { get; } = new();
        public FakeFingerprints Fingerprints { get; } = new();
        public FakeSnapshots Snapshots { get; } = new();
        public TaskExecution Execution { get; }
        public RequestExecutionChangesCommandHandler Handler { get; }

        public Fixture()
        {
            var task = new DevelopmentTask
            {
                Id = Guid.NewGuid(),
                Title = "Add retry",
                Description = "Add a retry to the client.",
                RepositoryWorkspaceId = WorkspaceId,
                RepositoryWorkspace = new RepositoryWorkspace { Id = WorkspaceId, Owner = "o", Repository = "r", Branch = "main" }
            };

            // A delivered execution: approved, committed, pushed, pull request open.
            Execution = new TaskExecution
            {
                Id = Guid.NewGuid(),
                DevelopmentTaskId = task.Id,
                DevelopmentTask = task,
                Status = TaskExecutionStatus.Completed,
                ReviewStatus = ExecutionReviewStatus.Approved,
                ApprovedChangeFingerprint = "approved",
                ReviewDecidedAt = DateTime.UtcNow,
                WorkspacePath = Path.GetTempPath(),
                BranchName = "devpilot/exec-1",
                BaseCommitSha = InitialBase,
                InitialBaseCommitSha = InitialBase,
                CommitStatus = ExecutionCommitStatus.Committed,
                CommitSha = "delivered-sha",
                PushStatus = ExecutionPushStatus.Pushed,
                RemoteBranchName = "devpilot/exec-1",
                RemoteCommitSha = "delivered-sha",
                PullRequestStatus = ExecutionPullRequestStatus.Open,
                PullRequestNumber = 42,
                PullRequestRemoteState = ExecutionPullRequestRemoteState.Open,
                CiStatus = ExecutionCiStatus.Success,
                PullRequestIntegrityStatus = ExecutionPullRequestIntegrityStatus.Valid
            };
            Repository.Seed(Execution);
            Repository.Tasks[task.Id] = task;

            Store = new FakeRevisionStore(Repository);
            Handler = new RequestExecutionChangesCommandHandler(
                Repository,
                Store,
                Processor,
                Dispatcher,
                Recorder,
                new FakeHeartbeat(),
                new FakeCancellationRegistry(),
                NullLogger<RequestExecutionChangesCommandHandler>.Instance,
                fingerprintCalculator: Fingerprints,
                snapshotService: Snapshots);
        }

        public static async Task<Fixture> RequestedAsync(string feedback = "Please fix the timeout.")
        {
            var fx = new Fixture();
            var requested = await fx.Handler.RequestAsync(new RequestExecutionChangesCommand(fx.Execution.Id, feedback));
            requested.Status.Should().Be(RequestExecutionChangesResultStatus.Accepted);
            return fx;
        }
    }

    private sealed class FakeRevisionStore : IExecutionRevisionStore
    {
        private readonly InMemoryExecutionRepository _repository;

        public FakeRevisionStore(InMemoryExecutionRepository repository) => _repository = repository;

        public bool RefuseClaim { get; set; }

        public Task<bool> ClaimCompletedForRevisionAsync(Guid executionId, Guid leaseToken, string feedback, DateTime requestedAt, CancellationToken cancellationToken = default)
        {
            if (RefuseClaim || !_repository.Executions.TryGetValue(executionId, out var e) || e.Status != TaskExecutionStatus.Completed)
            {
                return Task.FromResult(false);
            }

            e.Status = TaskExecutionStatus.Running;
            e.CompletedAt = null;
            e.LeaseToken = leaseToken;
            e.LastChangeRequest = feedback;
            e.LastChangeRequestAt = requestedAt;
            e.LastChangeRequestResult = null;
            e.ChangeRequestCount++;
            return Task.FromResult(true);
        }

        public Task<bool> RestoreCompletedAfterRevisionDispatchFailureAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default)
        {
            var e = _repository.Executions[executionId];
            if (e.Status != TaskExecutionStatus.Running || e.LeaseToken != leaseToken)
            {
                return Task.FromResult(false);
            }

            e.Status = TaskExecutionStatus.Completed;
            e.LeaseToken = null;
            return Task.FromResult(true);
        }

        public Task<bool> MarkRevisionPendingDeliveryAsync(Guid executionId, CancellationToken cancellationToken = default)
        {
            var e = _repository.Executions[executionId];
            e.RevisionCount++;
            e.ReviewStatus = ExecutionReviewStatus.Pending;
            e.ReviewDecidedAt = null;
            e.ApprovedChangeFingerprint = null;
            e.CommitStatus = ExecutionCommitStatus.None;
            e.PushStatus = ExecutionPushStatus.None;
            e.CiStatus = ExecutionCiStatus.Unknown;
            e.PullRequestIntegrityStatus = ExecutionPullRequestIntegrityStatus.Unknown;
            return Task.FromResult(true);
        }

        public Task SetRevisionSnapshotAsync(Guid executionId, string? baseSnapshotSha, string? resultSnapshotSha, CancellationToken cancellationToken = default)
        {
            var e = _repository.Executions[executionId];
            e.RevisionBaseSnapshotSha = baseSnapshotSha ?? e.RevisionBaseSnapshotSha;
            e.RevisionResultSnapshotSha = resultSnapshotSha ?? e.RevisionResultSnapshotSha;
            return Task.CompletedTask;
        }

        public Task SetRevisionResultAsync(Guid executionId, string result, CancellationToken cancellationToken = default)
        {
            _repository.Executions[executionId].LastChangeRequestResult = result;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcessor : IExecutionProcessor
    {
        public List<ExecutionProcessingContext> Contexts { get; } = new();
        public Action? OnProcess { get; set; }
        public Exception? Throw { get; set; }

        public Task ProcessAsync(ExecutionProcessingContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            OnProcess?.Invoke();
            if (Throw != null)
            {
                throw Throw;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeDispatcher : IExecutionRevisionDispatcher
    {
        public List<(Guid ExecutionId, Guid LeaseToken)> Enqueued { get; } = new();
        public bool Throw { get; set; }

        public void EnqueueReviseExecution(Guid executionId, Guid leaseToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("queue unavailable");
            }

            Enqueued.Add((executionId, leaseToken));
        }
    }

    private sealed class FakeRecorder : IExecutionActivityRecorder
    {
        public List<string> Messages { get; } = new();

        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFingerprints : IExecutionChangeFingerprintCalculator
    {
        public string Current { get; set; } = "before";

        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionFingerprintResult(true, Current, "head", false, 1));

        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(string workspacePath, string treeSha, string baseHeadSha, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionFingerprintResult(true, Current, baseHeadSha, false, 1));
    }

    private sealed class FakeSnapshots : IExecutionWorktreeSnapshotService
    {
        public List<string> Labels { get; } = new();

        public Task<string?> CaptureAsync(string workspacePath, Guid executionId, string label, CancellationToken cancellationToken = default)
        {
            Labels.Add(label);
            return Task.FromResult<string?>($"snapshot-{label}");
        }
    }

    private sealed class FakeHeartbeat : IExecutionHeartbeatService
    {
        public IAsyncDisposable StartHeartbeat(Guid executionId, Guid leaseToken, TimeSpan interval, TimeSpan leaseDuration, CancellationTokenSource linkedCts) =>
            new Noop();

        private sealed class Noop : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCancellationRegistry : IExecutionCancellationRegistry
    {
        public CancellationToken Register(Guid executionId, CancellationToken parentToken = default) => parentToken;
        public bool TryCancel(Guid executionId) => false;
        public void Unregister(Guid executionId)
        {
        }
    }
}
