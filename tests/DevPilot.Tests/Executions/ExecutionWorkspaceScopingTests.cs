using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Commands.CommitExecution;
using DevPilot.Application.Executions.Commands.CreatePullRequest;
using DevPilot.Application.Executions.Commands.MergeExecution;
using DevPilot.Application.Executions.Commands.PushExecution;
using DevPilot.Application.Executions.Commands.RejectExecutionReview;
using DevPilot.Application.Executions.Commands.SyncPullRequest;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Queries.GetExecutionActivity;
using DevPilot.Application.Executions.Queries.GetExecutionById;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Application.GitProviders;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class ExecutionWorkspaceScopingTests
{
    private readonly Guid _workspaceIdA = Guid.NewGuid();
    private readonly Guid _workspaceIdB = Guid.NewGuid();

    private readonly TaskExecution _executionInWorkspaceA;
    private readonly InMemoryExecutionRepository _executionRepository;
    private readonly FakeExecutionActivityRepository _activityRepository;

    public ExecutionWorkspaceScopingTests()
    {
        var taskA = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = _workspaceIdA,
            Title = "Task in Workspace A",
            RepositoryWorkspace = new RepositoryWorkspace
            {
                Id = _workspaceIdA,
                Owner = "testorg",
                Repository = "testrepo"
            }
        };

        _executionInWorkspaceA = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskA.Id,
            DevelopmentTask = taskA,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending,
            CommitStatus = ExecutionCommitStatus.None,
            PushStatus = ExecutionPushStatus.None,
            PullRequestStatus = ExecutionPullRequestStatus.None,
            MergeStatus = ExecutionMergeStatus.None,
            CreatedAt = DateTime.UtcNow,
        };

        _executionRepository = new InMemoryExecutionRepository();
        _executionRepository.Executions[_executionInWorkspaceA.Id] = _executionInWorkspaceA;

        _activityRepository = new FakeExecutionActivityRepository();
    }

    [Fact]
    public async Task GetExecutionById_WhenWorkspaceMismatches_ReturnsNotFound()
    {
        var impactRepo = new FakeImpactAnalysisRepository();
        var mergeOpts = Options.Create(new MergePolicyOptions());
        var handler = new GetExecutionByIdQueryHandler(_executionRepository, _activityRepository, impactRepo, mergeOpts);

        var result = await handler.HandleAsync(new GetExecutionByIdQuery(_executionInWorkspaceA.Id, _workspaceIdB));

        result.Found.Should().BeFalse();
        result.ErrorMessage.Should().Be("Execution not found.");
    }

    [Fact]
    public async Task GetExecutionById_WhenWorkspaceMatches_ReturnsExecution()
    {
        var impactRepo = new FakeImpactAnalysisRepository();
        var mergeOpts = Options.Create(new MergePolicyOptions());
        var handler = new GetExecutionByIdQueryHandler(_executionRepository, _activityRepository, impactRepo, mergeOpts);

        var result = await handler.HandleAsync(new GetExecutionByIdQuery(_executionInWorkspaceA.Id, _workspaceIdA));

        result.Found.Should().BeTrue();
        result.Execution.Should().NotBeNull();
        result.Execution!.Id.Should().Be(_executionInWorkspaceA.Id);
    }

    [Fact]
    public async Task GetExecutionById_WhenWorkspaceIsNull_ReturnsExecutionWithoutScopingFilter()
    {
        var impactRepo = new FakeImpactAnalysisRepository();
        var mergeOpts = Options.Create(new MergePolicyOptions());
        var handler = new GetExecutionByIdQueryHandler(_executionRepository, _activityRepository, impactRepo, mergeOpts);

        var result = await handler.HandleAsync(new GetExecutionByIdQuery(_executionInWorkspaceA.Id, null));

        result.Found.Should().BeTrue();
        result.Execution.Should().NotBeNull();
        result.Execution!.Id.Should().Be(_executionInWorkspaceA.Id);
    }

    [Fact]
    public async Task GetExecutionActivity_WhenWorkspaceMismatches_ReturnsNotFound()
    {
        var handler = new GetExecutionActivityQueryHandler(_executionRepository, _activityRepository);

        var result = await handler.HandleAsync(new GetExecutionActivityQuery(_executionInWorkspaceA.Id, _workspaceIdB));

        result.Found.Should().BeFalse();
        result.ErrorMessage.Should().Be($"Execution with ID '{_executionInWorkspaceA.Id}' was not found.");
    }

    [Fact]
    public async Task GetExecutionReview_WhenWorkspaceMismatches_ReturnsNotFoundBeforeCheckingDisk()
    {
        var workspaceManager = new FakeExecutionWorkspaceManager();
        var diffReader = new FakeExecutionGitDiffReader();
        var fpCalc = new FakeExecutionChangeFingerprintCalculator();
        var mergeOpts = Options.Create(new MergePolicyOptions());

        var handler = new GetExecutionReviewQueryHandler(
            _executionRepository,
            workspaceManager,
            diffReader,
            fpCalc,
            _activityRepository,
            mergeOpts,
            NullLogger<GetExecutionReviewQueryHandler>.Instance);

        var result = await handler.HandleAsync(new GetExecutionReviewQuery(_executionInWorkspaceA.Id, _workspaceIdB));

        result.Status.Should().Be(ExecutionReviewResultStatus.NotFound);
        workspaceManager.VerifyCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ApproveExecutionReview_WhenWorkspaceMismatches_ReturnsNotFound()
    {
        var workspaceManager = new FakeExecutionWorkspaceManager();
        var fpCalc = new FakeExecutionChangeFingerprintCalculator();
        var activityRecorder = new FakeExecutionActivityRecorder();
        var handler = new ApproveExecutionReviewCommandHandler(
            _executionRepository,
            _activityRepository,
            workspaceManager,
            fpCalc,
            activityRecorder,
            NullLogger<ApproveExecutionReviewCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ApproveExecutionReviewCommand(_executionInWorkspaceA.Id, "fp", _workspaceIdB));

        result.Status.Should().Be(ApproveExecutionReviewResultStatus.NotFound);
    }

    [Fact]
    public async Task RejectExecutionReview_WhenWorkspaceMismatches_ReturnsNotFound()
    {
        var workspaceManager = new FakeExecutionWorkspaceManager();
        var activityRecorder = new FakeExecutionActivityRecorder();
        var handler = new RejectExecutionReviewCommandHandler(
            _executionRepository,
            workspaceManager,
            activityRecorder,
            NullLogger<RejectExecutionReviewCommandHandler>.Instance);

        var result = await handler.HandleAsync(new RejectExecutionReviewCommand(_executionInWorkspaceA.Id, "reason", _workspaceIdB));

        result.Status.Should().Be(RejectExecutionReviewResultStatus.NotFound);
    }

    [Fact]
    public async Task CommitExecution_WhenWorkspaceMismatches_ReturnsNotFound()
    {
        var workspaceManager = new FakeExecutionWorkspaceManager();
        var gitCommitService = new FakeExecutionGitCommitService();
        var activityRecorder = new FakeExecutionActivityRecorder();
        var handler = new CommitExecutionCommandHandler(
            _executionRepository,
            workspaceManager,
            gitCommitService,
            activityRecorder,
            NullLogger<CommitExecutionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new CommitExecutionCommand(_executionInWorkspaceA.Id, _workspaceIdB));

        result.Status.Should().Be(CommitExecutionResultStatus.NotFound);
    }

    [Fact]
    public async Task PushExecution_WhenWorkspaceMismatches_ReturnsNotFound()
    {
        var gitPushService = new FakeExecutionGitPushService();
        var activityRecorder = new FakeExecutionActivityRecorder();
        var handler = new PushExecutionCommandHandler(
            _executionRepository,
            gitPushService,
            activityRecorder,
            NullLogger<PushExecutionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new PushExecutionCommand(_executionInWorkspaceA.Id, _workspaceIdB));

        result.Status.Should().Be(PushExecutionResultStatus.NotFound);
    }

    [Fact]
    public async Task CreatePullRequest_WhenWorkspaceMismatches_ReturnsNotFound()
    {
        var githubPrService = new FakeExecutionGitHubPullRequestService();
        var activityRecorder = new FakeExecutionActivityRecorder();
        var handler = new CreatePullRequestCommandHandler(
            _executionRepository,
            githubPrService,
            activityRecorder,
            NullLogger<CreatePullRequestCommandHandler>.Instance);

        var result = await handler.HandleAsync(new CreatePullRequestCommand(_executionInWorkspaceA.Id, _workspaceIdB));

        result.Status.Should().Be(CreatePullRequestResultStatus.NotFound);
    }

    [Fact]
    public async Task SyncPullRequest_WhenWorkspaceMismatches_ReturnsNotFound()
    {
        var githubSyncService = new FakeExecutionGitHubSyncService();
        var handler = new SyncPullRequestCommandHandler(
            _executionRepository,
            githubSyncService,
            NullLogger<SyncPullRequestCommandHandler>.Instance);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(_executionInWorkspaceA.Id, _workspaceIdB));

        result.Status.Should().Be(SyncPullRequestResultStatus.NotFound);
    }

    [Fact]
    public async Task MergeExecution_WhenWorkspaceMismatches_ReturnsNotFound()
    {
        var githubClient = new FakeGitHubPullRequestClient();
        var githubSyncService = new FakeExecutionGitHubSyncService();
        var activityRecorder = new FakeExecutionActivityRecorder();
        var mergeOpts = Options.Create(new MergePolicyOptions());

        var handler = new MergeExecutionCommandHandler(
            _executionRepository,
            githubClient,
            githubSyncService,
            activityRecorder,
            _activityRepository,
            mergeOpts,
            NullLogger<MergeExecutionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new MergeExecutionCommand(_executionInWorkspaceA.Id, _workspaceIdB));

        result.Status.Should().Be(MergeExecutionResultStatus.NotFound);
    }

    #region Fakes

    private sealed class FakeExecutionActivityRepository : IExecutionActivityRepository
    {
        public Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionActivity>>(Array.Empty<ExecutionActivity>());
    }

    private sealed class FakeImpactAnalysisRepository : IImpactAnalysisRepository
    {
        public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TaskImpactAnalysis?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskImpactAnalysis?>(null);
        public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult<TaskImpactAnalysis?>(null);
        public Task<IReadOnlyList<TaskImpactAnalysis>> GetByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskImpactAnalysis>>(Array.Empty<TaskImpactAnalysis>());
    }

    private sealed class FakeExecutionWorkspaceManager : IExecutionWorkspaceManager
    {
        public bool VerifyCalled { get; private set; }

        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default)
        {
            VerifyCalled = true;
            return Task.FromResult(new WorkspaceVerificationResult(true, true, true, true, null));
        }
    }

    private sealed class FakeExecutionGitDiffReader : IExecutionGitDiffReader
    {
        public Task<ExecutionGitDiffResult> ReadWorkspaceDiffAsync(string workspacePath, string branchName, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ExecutionGitDiffResult> ReadCommittedDiffAsync(string workspacePath, string baseCommitSha, string commitSha, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeExecutionChangeFingerprintCalculator : IExecutionChangeFingerprintCalculator
    {
        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(string workspacePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionFingerprintResult(true, "fp", "sha", false, 1, null));

        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(string workspacePath, string treeSha, string baseHeadSha, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionFingerprintResult(true, "fp", baseHeadSha, false, 1, null));
    }

    private sealed class FakeExecutionActivityRecorder : IExecutionActivityRecorder
    {
        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeExecutionGitCommitService : IExecutionGitCommitService
    {
        public Task<ExecutionCommitResult> CommitApprovedExecutionAsync(TaskExecution execution, string taskTitle, Guid attemptId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionCommitResult(true, false, "sha", DateTime.UtcNow, null));
    }

    private sealed class FakeExecutionGitPushService : IExecutionGitPushService
    {
        public Task<ExecutionPushResult> PushExecutionBranchAsync(TaskExecution execution, Guid attemptId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeExecutionGitHubPullRequestService : IExecutionGitHubPullRequestService
    {
        public Task<ExecutionPullRequestServiceResult> CreateOrAdoptPullRequestAsync(TaskExecution execution, Guid attemptId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeExecutionGitHubSyncService : IExecutionGitHubSyncService
    {
        public Task<GitHubSyncResultDto> SyncPullRequestAndCiAsync(TaskExecution execution, bool force, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeGitHubPullRequestClient : IGitHubPullRequestClient
    {
        public Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> CreatePullRequestAsync(string owner, string repo, string title, string head, string @base, string body, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubPullRequestDto>>> ListPullRequestsAsync(string owner, string repo, string head, string @base, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<GitHubPullRequestClientResult<GitHubPullRequestDto>> GetPullRequestAsync(string owner, string repo, int pullRequestNumber, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<GitHubBranchRefResult> GetBranchHeadShaAsync(string owner, string repo, string branchName, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCheckRunDto>>> ListCheckRunsForRefAsync(string owner, string repo, string commitSha, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<GitHubPullRequestClientResult<IReadOnlyList<GitHubCommitStatusDto>>> ListCommitStatusesForRefAsync(string owner, string repo, string commitSha, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<GitHubPullRequestClientResult<GitHubMergeResultDto>> MergePullRequestAsync(string owner, string repository, int pullNumber, string expectedHeadSha, string? commitTitle = null, string? commitMessage = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    #endregion
}
