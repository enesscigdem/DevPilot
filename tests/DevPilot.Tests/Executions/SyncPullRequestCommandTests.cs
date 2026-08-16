using System.Net;
using System.Text.Json;
using DevPilot.Application.Executions.Commands.SyncPullRequest;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using DevPilot.Infrastructure.GitProviders;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class SyncPullRequestCommandTests
{
    [Fact]
    public async Task Sync_ExecutionWithoutOpenedPr_ReturnsConflict()
    {
        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(new TestHttpMessageHandler());
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.None);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("does not have an open pull request");
    }

    [Fact]
    public async Task Sync_ConcurrentSyncLease_BlocksSecondSync()
    {
        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(new TestHttpMessageHandler());
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        // Manually claim lease by another attempt
        execution.PullRequestSyncAttemptId = Guid.NewGuid();
        execution.PullRequestSyncClaimedAt = DateTime.UtcNow;

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("in progress");
    }

    [Fact]
    public async Task Sync_StaleSyncLease_ReclaimsAndSucceeds()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: CreateCheckRunsJson(new (long id, string name, string status, string? conclusion, string appName)[] { (101L, "Build", "completed", "success", "GitHub Actions") }),
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        // Set stale lease (> 1 min ago)
        execution.PullRequestSyncAttemptId = Guid.NewGuid();
        execution.PullRequestSyncClaimedAt = DateTime.UtcNow.AddMinutes(-5);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response.Should().NotBeNull();
        result.Response!.PullRequestIntegrityStatus.Should().Be("Valid");
        result.Response.CiStatus.Should().Be("Success");
    }

    [Fact]
    public async Task Sync_ReclaimedStaleAttempt_LateResultCannotOverwriteNewerSnapshot()
    {
        var repository = new InMemoryExecutionRepository();
        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var attemptA = Guid.NewGuid();
        var attemptB = Guid.NewGuid();

        // Attempt A claims lease
        await repository.TryClaimPullRequestSyncLeaseAsync(execution.Id, attemptA, DateTime.UtcNow);

        // Attempt B reclaims stale lease
        await repository.TryReclaimStalePullRequestSyncLeaseAsync(execution.Id, attemptB, DateTime.UtcNow, TimeSpan.FromSeconds(0));

        // Attempt B completes and persists snapshot
        var bSuccess = await repository.ReplacePullRequestTrackingSnapshotAsync(
            execution.Id,
            attemptB,
            ExecutionPullRequestRemoteState.Open,
            ExecutionPullRequestIntegrityStatus.Valid,
            closedAt: null,
            mergedAt: null,
            ExecutionCiStatus.Success,
            new[] { new ExecutionCiCheck { ExternalId = 1, Name = "B Check", Source = "GitHub Actions", CheckType = ExecutionCiCheckType.CheckRun, Status = "completed", Conclusion = "success" } },
            DateTime.UtcNow);

        bSuccess.Should().BeTrue();

        // Attempt A finally returns late with stale result -> MUST fail/no-op
        var aSuccess = await repository.ReplacePullRequestTrackingSnapshotAsync(
            execution.Id,
            attemptA,
            ExecutionPullRequestRemoteState.Closed,
            ExecutionPullRequestIntegrityStatus.Valid,
            closedAt: DateTime.UtcNow,
            mergedAt: null,
            ExecutionCiStatus.Failure,
            new[] { new ExecutionCiCheck { ExternalId = 2, Name = "A Stale Check", Source = "GitHub Actions", CheckType = ExecutionCiCheckType.CheckRun, Status = "completed", Conclusion = "failure" } },
            DateTime.UtcNow);

        aSuccess.Should().BeFalse();

        // Verify Attempt B snapshot remains intact
        var reloaded = await repository.GetByIdAsync(execution.Id);
        reloaded!.CiStatus.Should().Be(ExecutionCiStatus.Success);
        reloaded.CiChecks.Should().ContainSingle(c => c.Name == "B Check");
    }

    [Fact]
    public async Task Sync_FreshnessWindow_ReturnsCachedSnapshotWithoutCallingGitHub()
    {
        var testHandler = new TestHttpMessageHandler();
        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);
        execution.PullRequestLastSyncedAt = DateTime.UtcNow.AddSeconds(-3); // Synced 3s ago (< 10s)
        execution.PullRequestRemoteState = ExecutionPullRequestRemoteState.Open;
        execution.PullRequestIntegrityStatus = ExecutionPullRequestIntegrityStatus.Valid;
        execution.CiStatus = ExecutionCiStatus.Success;

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.CiStatus.Should().Be("Success");

        // Verify zero HTTP requests were made
        testHandler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Sync_OpenPrWithMatchingMetadataAndSha_SetsIntegrityValid()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: CreateCheckRunsJson(new (long id, string name, string status, string? conclusion, string appName)[] { (101L, "Tests", "completed", "success", "GitHub Actions") }),
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.PullRequestRemoteState.Should().Be("Open");
        result.Response.PullRequestIntegrityStatus.Should().Be("Valid");
        result.Response.CiStatus.Should().Be("Success");
        result.Response.Checks.Should().ContainSingle(c => c.Name == "Tests" && c.Conclusion == "success");
    }

    [Fact]
    public async Task Sync_LivePrClosed_SetsRemoteStateClosed()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "closed", false, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: "{\"check_runs\":[]}",
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.PullRequestRemoteState.Should().Be("Closed");
    }

    [Fact]
    public async Task Sync_LivePrMerged_StateClosedAndMergedTrue_SetsRemoteStateMerged()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "closed", true, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: "{\"check_runs\":[]}",
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.PullRequestRemoteState.Should().Be("Merged");
    }

    [Fact]
    public async Task Sync_HeadShaDiffersFromRemoteCommitSha_SetsIntegrityHeadChangedAndCiUnknown()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", "9999999999999999999999999999999999999999", "master"), // Different head SHA
            checkRunsJson: "{\"check_runs\":[]}",
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.PullRequestIntegrityStatus.Should().Be("HeadChanged");
        result.Response.CiStatus.Should().Be("Unknown");
        result.Response.Checks.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_HeadOrBaseRefOrRepoDiffers_SetsIntegrityIdentityMismatch()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "wrong-head-branch", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: "{\"check_runs\":[]}",
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.PullRequestIntegrityStatus.Should().Be("IdentityMismatch");
        result.Response.CiStatus.Should().Be("Unknown");
    }

    [Fact]
    public async Task Sync_CheckRunStatusRequestedOrWaiting_MapsToPending()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: CreateCheckRunsJson(new (long id, string name, string status, string? conclusion, string appName)[] {
                (101L, "Build", "waiting", null, "GitHub Actions"),
                (102L, "Tests", "requested", null, "GitHub Actions")
            }),
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.CiStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task Sync_CheckRunDeduplication_TwoDistinctIdsWithSameNameAndApp_BothPreserved()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: CreateCheckRunsJson(new (long id, string name, string status, string? conclusion, string appName)[] {
                (101L, "Build Matrix (Linux)", "completed", "success", "GitHub Actions"),
                (102L, "Build Matrix (Linux)", "completed", "success", "GitHub Actions") // Same name/app, distinct IDs!
            }),
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.CheckCount.Should().Be(2);
        result.Response.Checks.Select(c => c.ExternalId).Should().BeEquivalentTo(new long[] { 101L, 102L });
    }

    [Fact]
    public async Task Sync_CommitStatusDeduplication_MultipleStatusesSameContext_KeepsNewestOnly()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: "{\"check_runs\":[]}",
            statusesJson: JsonSerializer.Serialize(new[] {
                new { id = 202L, context = "security/codeql", state = "success", created_at = "2026-08-16T12:00:00Z" }, // Newest first
                new { id = 201L, context = "security/codeql", state = "failure", created_at = "2026-08-16T11:00:00Z" }  // Older
            }));

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.CheckCount.Should().Be(1);
        result.Response.Checks.Single().ExternalId.Should().Be(202L);
        result.Response.CiStatus.Should().Be("Success");
    }

    [Fact]
    public async Task Sync_CiAggregation_ZeroChecks_ReturnsNoChecks()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: "{\"check_runs\":[]}",
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.CiStatus.Should().Be("NoChecks");
    }

    [Fact]
    public async Task Sync_CiAggregation_NeutralOnly_ReturnsNeutral()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: CreateCheckRunsJson(new (long id, string name, string status, string? conclusion, string appName)[] { (101L, "Optional Check", "completed", "neutral", "GitHub Actions") }),
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.CiStatus.Should().Be("Neutral");
    }

    [Fact]
    public async Task Sync_CiAggregation_UnknownStatusOrConclusion_ReturnsUnknownNeverSuccess()
    {
        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", "161ec91ce5edffc1770ec7617818e2b9d57f2341", "master"),
            checkRunsJson: CreateCheckRunsJson(new (long id, string name, string status, string? conclusion, string appName)[] { (101L, "Future Check", "completed", "futuristic_custom_conclusion", "GitHub Actions") }),
            statusesJson: "[]");

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.Success);
        result.Response!.CiStatus.Should().Be("Unknown");
    }

    [Fact]
    public async Task Sync_GitHubApiError_ReturnsExternalFailureAndRetainsPriorSnapshot()
    {
        var testHandler = new TestHttpMessageHandler
        {
            CustomResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("GitHub Server Error")
            }
        };

        var repository = new InMemoryExecutionRepository();
        var client = CreateGitHubClient(testHandler);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var handler = new SyncPullRequestCommandHandler(repository, syncService, NullLogger<SyncPullRequestCommandHandler>.Instance);

        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.Open, prNumber: 42);

        // Seed prior good snapshot
        execution.PullRequestRemoteState = ExecutionPullRequestRemoteState.Open;
        execution.PullRequestIntegrityStatus = ExecutionPullRequestIntegrityStatus.Valid;
        execution.CiStatus = ExecutionCiStatus.Success;
        execution.PullRequestLastSyncedAt = DateTime.UtcNow.AddMinutes(-30);
        execution.CiChecks.Add(new ExecutionCiCheck { ExternalId = 999, Name = "Prior Check", Source = "GitHub Actions", CheckType = ExecutionCiCheckType.CheckRun, Status = "completed", Conclusion = "success" });

        var result = await handler.HandleAsync(new SyncPullRequestCommand(execution.Id));

        result.Status.Should().Be(SyncPullRequestResultStatus.ExternalFailure);
        result.ErrorMessage.Should().Contain("HTTP 500");

        // Verify prior snapshot remains intact in database
        var reloaded = await repository.GetByIdAsync(execution.Id);
        reloaded!.CiStatus.Should().Be(ExecutionCiStatus.Success);
        reloaded.CiChecks.Should().ContainSingle(c => c.Name == "Prior Check");
    }

    private static TaskExecution SeedExecution(
        InMemoryExecutionRepository repository,
        ExecutionPullRequestStatus prStatus = ExecutionPullRequestStatus.None,
        int? prNumber = null)
    {
        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            LocalPath = "/tmp/workspace",
            Status = RepositoryWorkspaceStatus.Completed
        };

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = workspace.Id,
            RepositoryWorkspace = workspace,
            Title = "Implement safe GitHub tracking",
            Description = "Task details",
            Status = DevelopmentTaskStatus.Completed,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            CommitStatus = ExecutionCommitStatus.Committed,
            CommitSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341",
            PushStatus = ExecutionPushStatus.Pushed,
            RemoteBranchName = "devpilot/task-1",
            RemoteCommitSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341",
            PullRequestStatus = prStatus,
            PullRequestNumber = prNumber,
            PullRequestUrl = prNumber.HasValue ? $"https://github.com/enesscigdem/DevPilot/pull/{prNumber}" : null,
            PullRequestBaseBranch = "master",
            CreatedAt = DateTime.UtcNow
        };

        repository.Executions[execution.Id] = execution;
        return execution;
    }

    private static IGitHubPullRequestClient CreateGitHubClient(TestHttpMessageHandler handler)
    {
        var factory = new TestHttpClientFactory(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "GitProvider:GitHub:BaseUrl", "https://api.github.com" },
                { "GitProvider:GitHub:Token", "fake-test-token" }
            })
            .Build();

        return new GitHubPullRequestClient(factory, config, NullLogger<GitHubPullRequestClient>.Instance);
    }

    private static TestHttpMessageHandler CreateTestHttpMessageHandler(
        string prJson,
        string checkRunsJson,
        string statusesJson)
    {
        return new TestHttpMessageHandler
        {
            PrJson = prJson,
            CheckRunsJson = checkRunsJson,
            StatusesJson = statusesJson
        };
    }

    private static string CreatePrJson(int number, string state, bool merged, string headRef, string headSha, string baseRef) =>
        JsonSerializer.Serialize(new
        {
            number = number,
            html_url = $"https://github.com/enesscigdem/DevPilot/pull/{number}",
            state = state,
            merged = merged,
            closed_at = state == "closed" ? (DateTime?)DateTime.UtcNow : null,
            merged_at = merged ? (DateTime?)DateTime.UtcNow : null,
            head = new { @ref = headRef, sha = headSha, repo = new { name = "DevPilot", owner = new { login = "enesscigdem" } } },
            @base = new { @ref = baseRef, sha = "0000000000000000000000000000000000000000", repo = new { name = "DevPilot", owner = new { login = "enesscigdem" } } },
            body = "DevPilot PR"
        });

    private static string CreateCheckRunsJson(IEnumerable<(long id, string name, string status, string? conclusion, string appName)> items) =>
        JsonSerializer.Serialize(new
        {
            check_runs = items.Select(i => new
            {
                id = i.id,
                name = i.name,
                status = i.status,
                conclusion = i.conclusion,
                started_at = "2026-08-16T12:00:00Z",
                completed_at = "2026-08-16T12:05:00Z",
                app = new { name = i.appName }
            })
        });

    private sealed class InMemoryExecutionRepository : IExecutionRepository
    {
        public Dictionary<Guid, TaskExecution> Executions { get; } = new();

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Executions.TryGetValue(id, out var exec);
            return Task.FromResult(exec);
        }

        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TaskExecution>>(Executions.Values.ToList());

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

        public Task<bool> TryClaimPullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e))
            {
                if (e.PullRequestSyncAttemptId != null && e.PullRequestSyncClaimedAt != null)
                    return Task.FromResult(false);

                e.PullRequestSyncAttemptId = attemptId;
                e.PullRequestSyncClaimedAt = claimedAt;
                e.PullRequestLastSyncAttemptAt = claimedAt;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> TryReclaimStalePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e))
            {
                if (e.PullRequestSyncClaimedAt != null && (claimedAt - e.PullRequestSyncClaimedAt.Value) < leaseTimeout)
                {
                    return Task.FromResult(false);
                }

                e.PullRequestSyncAttemptId = attemptId;
                e.PullRequestSyncClaimedAt = claimedAt;
                e.PullRequestLastSyncAttemptAt = claimedAt;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task ReleasePullRequestSyncLeaseAsync(Guid executionId, Guid attemptId, DateTime releasedAt, CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e) && e.PullRequestSyncAttemptId == attemptId)
            {
                e.PullRequestSyncAttemptId = null;
                e.PullRequestSyncClaimedAt = null;
            }
            return Task.CompletedTask;
        }

        public Task<bool> ReplacePullRequestTrackingSnapshotAsync(
            Guid executionId,
            Guid attemptId,
            ExecutionPullRequestRemoteState remoteState,
            ExecutionPullRequestIntegrityStatus integrityStatus,
            DateTime? closedAt,
            DateTime? mergedAt,
            ExecutionCiStatus ciStatus,
            IReadOnlyList<ExecutionCiCheck> checks,
            DateTime syncedAt,
            CancellationToken cancellationToken = default)
        {
            if (Executions.TryGetValue(executionId, out var e) && e.PullRequestSyncAttemptId == attemptId)
            {
                e.PullRequestRemoteState = remoteState;
                e.PullRequestIntegrityStatus = integrityStatus;
                e.PullRequestClosedAt = closedAt;
                e.PullRequestMergedAt = mergedAt;
                e.CiStatus = ciStatus;
                e.CiChecks = checks.ToList();
                e.PullRequestLastSyncedAt = syncedAt;
                e.PullRequestSyncAttemptId = null;
                e.PullRequestSyncClaimedAt = null;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public TestHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler);
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public string? PrJson { get; set; }
        public string? CheckRunsJson { get; set; }
        public string? StatusesJson { get; set; }
        public HttpResponseMessage? CustomResponse { get; set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (CustomResponse != null)
            {
                return Task.FromResult(CustomResponse);
            }

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/check-runs") && CheckRunsJson != null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CheckRunsJson) });
            }

            if (path.Contains("/statuses") && StatusesJson != null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StatusesJson) });
            }

            if (path.Contains("/pulls/") && PrJson != null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PrJson) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
