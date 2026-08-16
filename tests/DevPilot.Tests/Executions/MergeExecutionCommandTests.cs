using System.Net;
using System.Text.Json;
using DevPilot.Application.Executions.Commands.MergeExecution;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using DevPilot.Infrastructure.GitProviders;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class MergeExecutionCommandTests
{
    [Fact]
    public async Task Merge_ExecutionWithoutApprovedReview_ReturnsConflict()
    {
        var repository = new InMemoryExecutionRepository();
        var handler = CreateHandler(repository, new CustomTestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var execution = SeedExecution(repository, reviewStatus: ExecutionReviewStatus.Pending);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("preconditions do not allow merge");
    }

    [Fact]
    public async Task Merge_UncommittedExecution_ReturnsConflict()
    {
        var repository = new InMemoryExecutionRepository();
        var handler = CreateHandler(repository, new CustomTestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var execution = SeedExecution(repository, commitStatus: ExecutionCommitStatus.None);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
    }

    [Fact]
    public async Task Merge_UnpushedExecution_ReturnsConflict()
    {
        var repository = new InMemoryExecutionRepository();
        var handler = CreateHandler(repository, new CustomTestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var execution = SeedExecution(repository, pushStatus: ExecutionPushStatus.None);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
    }

    [Fact]
    public async Task Merge_ExecutionWithoutOpenedPR_ReturnsConflict()
    {
        var repository = new InMemoryExecutionRepository();
        var handler = CreateHandler(repository, new CustomTestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var execution = SeedExecution(repository, prStatus: ExecutionPullRequestStatus.None);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
    }

    [Fact]
    public async Task Merge_ClosedPRRemoteState_ReturnsConflict()
    {
        var repository = new InMemoryExecutionRepository();
        var handler = CreateHandler(repository, new CustomTestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var execution = SeedExecution(repository, prRemoteState: ExecutionPullRequestRemoteState.Closed);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
    }

    [Fact]
    public async Task Merge_LiveGitHubHeadShaMismatch_BlocksMergeWithoutUpdatingRemoteCommitSha()
    {
        var repository = new InMemoryExecutionRepository();
        var originalApprovedSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341";
        var differentLiveSha = "9999999999999999999999999999999999999999";

        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", differentLiveSha, "master"),
            checkRunsJson: CreateCheckRunsJson(new[] { (1L, "Build", "completed", "success", "Actions") }),
            statusesJson: "[]");

        var handler = CreateHandler(repository, testHandler);
        var execution = SeedExecution(repository, approvedSha: originalApprovedSha);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("differs from approved execution state");

        var reloaded = await repository.GetByIdAsync(execution.Id);
        reloaded!.RemoteCommitSha.Should().Be(originalApprovedSha);
        reloaded.MergeStatus.Should().Be(ExecutionMergeStatus.Failed);
    }

    [Fact]
    public async Task Merge_SendsExactPersistedShaAndMergeMethod()
    {
        var repository = new InMemoryExecutionRepository();
        var approvedSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341";
        string? sentPutBody = null;

        var testHandler = new CustomTestHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith("/merge"))
            {
                sentPutBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { sha = "mergecommit123", merged = true, message = "PR merged" }))
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/pulls/42"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreatePrJson(42, "open", false, "devpilot/task-1", approvedSha, "master"))
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/check-runs"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateCheckRunsJson(new[] { (1L, "CI", "completed", "success", "Actions") }))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });

        var handler = CreateHandler(repository, testHandler);
        var execution = SeedExecution(repository, approvedSha: approvedSha, ciStatus: ExecutionCiStatus.Success);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        sentPutBody.Should().NotBeNull();
        sentPutBody.Should().Contain("\"sha\":\"161ec91ce5edffc1770ec7617818e2b9d57f2341\"");
        sentPutBody.Should().Contain("\"merge_method\":\"merge\"");
    }

    [Fact]
    public async Task Merge_CiFailure_BlocksMerge()
    {
        var repository = new InMemoryExecutionRepository();
        var handler = CreateHandler(repository, new CustomTestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var execution = SeedExecution(repository, ciStatus: ExecutionCiStatus.Failure);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
    }

    [Fact]
    public async Task Merge_NoChecks_BlocksMergeWhenAllowNoChecksIsFalse()
    {
        var repository = new InMemoryExecutionRepository();
        var handler = CreateHandler(repository, new CustomTestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }), allowNoChecks: false);
        var execution = SeedExecution(repository, ciStatus: ExecutionCiStatus.NoChecks);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
    }

    [Fact]
    public async Task Merge_NoChecks_AllowsMergeWhenAllowNoChecksIsTrueAndBuildTestPassed()
    {
        var repository = new InMemoryExecutionRepository();
        var approvedSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341";

        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "open", false, "devpilot/task-1", approvedSha, "master"),
            checkRunsJson: "{\"total_count\":0,\"check_runs\":[]}",
            statusesJson: "[]",
            mergeResponseJson: JsonSerializer.Serialize(new { sha = "mergecommit123", merged = true, message = "Merged" }),
            postMergePrJson: CreatePrJson(42, "closed", true, "devpilot/task-1", approvedSha, "master", mergedAt: DateTime.UtcNow));

        var activityRepo = new TestActivityRepository
        {
            ActivitiesToReturn = new List<ExecutionActivity>
            {
                new() { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed },
                new() { Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed }
            }
        };

        var handler = CreateHandler(repository, testHandler, allowNoChecks: true, activityRepo: activityRepo);
        var execution = SeedExecution(repository, approvedSha: approvedSha, ciStatus: ExecutionCiStatus.NoChecks);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Success);
        result.Response!.MergeStatus.Should().Be("Merged");
    }

    [Fact]
    public async Task Merge_ConcurrentInProgressMerge_ReturnsConflict()
    {
        var repository = new InMemoryExecutionRepository();
        var handler = CreateHandler(repository, new CustomTestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        var execution = SeedExecution(repository);

        execution.MergeStatus = ExecutionMergeStatus.InProgress;
        execution.MergeAttemptId = Guid.NewGuid();
        execution.MergeClaimedAt = DateTime.UtcNow;

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("already in progress");
    }

    [Fact]
    public async Task Merge_StaleInProgressLease_ReclaimsAndRecoversIfPrAlreadyMerged()
    {
        var repository = new InMemoryExecutionRepository();
        var approvedSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341";
        var mergedTime = DateTime.UtcNow.AddMinutes(-5);

        var testHandler = CreateTestHttpMessageHandler(
            prJson: CreatePrJson(42, "closed", true, "devpilot/task-1", approvedSha, "master", mergedAt: mergedTime),
            checkRunsJson: "{\"total_count\":0,\"check_runs\":[]}",
            statusesJson: "[]");

        var activityRepo = new TestActivityRepository
        {
            ActivitiesToReturn = new List<ExecutionActivity>
            {
                new() { Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed },
                new() { Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed }
            }
        };

        var handler = CreateHandler(repository, testHandler, allowNoChecks: true, activityRepo: activityRepo);
        var execution = SeedExecution(repository, approvedSha: approvedSha, ciStatus: ExecutionCiStatus.NoChecks);

        execution.MergeStatus = ExecutionMergeStatus.InProgress;
        execution.MergeAttemptId = Guid.NewGuid();
        execution.MergeClaimedAt = DateTime.UtcNow.AddMinutes(-10); // Stale

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Success);
        result.Response!.MergeStatus.Should().Be("Merged");
        result.Response.MergedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Merge_PutTimeout_RemainsInProgressForRecovery()
    {
        var repository = new InMemoryExecutionRepository();
        var approvedSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341";

        var testHandler = new CustomTestHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith("/merge"))
            {
                throw new TaskCanceledException("Transport timeout during merge PUT");
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/pulls/42"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreatePrJson(42, "open", false, "devpilot/task-1", approvedSha, "master"))
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/check-runs"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateCheckRunsJson(new (long, string, string, string?, string)[] { (1L, "CI", "completed", "success", "Actions") }))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        });

        var handler = CreateHandler(repository, testHandler);
        var execution = SeedExecution(repository, approvedSha: approvedSha, ciStatus: ExecutionCiStatus.Success);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.ExternalFailure);
        result.ErrorMessage.Should().Contain("transport exception");

        var reloaded = await repository.GetByIdAsync(execution.Id);
        reloaded!.MergeStatus.Should().Be(ExecutionMergeStatus.InProgress);
    }

    [Fact]
    public async Task Merge_RepeatedPostAfterMerged_ReturnsPersistedResultIdempotently()
    {
        var repository = new InMemoryExecutionRepository();
        var execution = SeedExecution(repository);

        execution.MergeStatus = ExecutionMergeStatus.Merged;
        execution.MergeCommitSha = "mergecommit12345";
        execution.MergedAt = DateTime.UtcNow.AddHours(-1);
        execution.MergeMethod = "merge";

        var callCount = 0;
        var testHandler = new CustomTestHttpMessageHandler(req =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var handler = CreateHandler(repository, testHandler);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Success);
        result.Response!.MergeStatus.Should().Be("Merged");
        result.Response.MergeCommitSha.Should().Be("mergecommit12345");
        callCount.Should().Be(0); // Zero GitHub calls!
    }

    [Fact]
    public async Task Merge_NullCommitMessage_PayloadOmitsCommitMessageProperty()
    {
        string? capturedBody = null;
        var testHandler = new CustomTestHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sha\":\"merge123\",\"merged\":true}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var client = CreateClient(testHandler);
        await client.MergePullRequestAsync("owner", "repo", 42, "headsha123", commitTitle: "Title", commitMessage: null);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("\"commit_title\":\"Title\"");
        capturedBody.Should().Contain("\"sha\":\"headsha123\"");
        capturedBody.Should().Contain("\"merge_method\":\"merge\"");
        capturedBody.Should().NotContain("commit_message");
    }

    [Fact]
    public async Task Merge_NullCommitTitle_PayloadOmitsCommitTitleProperty()
    {
        string? capturedBody = null;
        var testHandler = new CustomTestHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sha\":\"merge123\",\"merged\":true}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var client = CreateClient(testHandler);
        await client.MergePullRequestAsync("owner", "repo", 42, "headsha123", commitTitle: null, commitMessage: "Message");

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("\"commit_message\":\"Message\"");
        capturedBody.Should().Contain("\"sha\":\"headsha123\"");
        capturedBody.Should().Contain("\"merge_method\":\"merge\"");
        capturedBody.Should().NotContain("commit_title");
    }

    [Fact]
    public async Task Merge_BothNullOptionalParams_PayloadContainsOnlyShaAndMergeMethod()
    {
        string? capturedBody = null;
        var testHandler = new CustomTestHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sha\":\"merge123\",\"merged\":true}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var client = CreateClient(testHandler);
        await client.MergePullRequestAsync("owner", "repo", 42, "headsha123", commitTitle: null, commitMessage: null);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("\"sha\":\"headsha123\"");
        capturedBody.Should().Contain("\"merge_method\":\"merge\"");
        capturedBody.Should().NotContain("commit_title");
        capturedBody.Should().NotContain("commit_message");
    }

    [Fact]
    public async Task Merge_NonNullOptionalParams_SerializesBothAsStrings()
    {
        string? capturedBody = null;
        var testHandler = new CustomTestHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sha\":\"merge123\",\"merged\":true}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var client = CreateClient(testHandler);
        await client.MergePullRequestAsync("owner", "repo", 42, "headsha123", commitTitle: "Title", commitMessage: "Message");

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("\"commit_title\":\"Title\"");
        capturedBody.Should().Contain("\"commit_message\":\"Message\"");
        capturedBody.Should().Contain("\"sha\":\"headsha123\"");
        capturedBody.Should().Contain("\"merge_method\":\"merge\"");
    }

    [Fact]
    public async Task Merge_GitHub422ValidationRejection_TransitionsToFailedAndReleasesLease()
    {
        var repository = new InMemoryExecutionRepository();
        var approvedSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341";

        var testHandler = new CustomTestHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith("/merge"))
            {
                return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent("{\"message\":\"Invalid request.\\n\\nFor 'properties/commit_message', nil is not a string.\",\"status\":\"422\"}")
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/pulls/42"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreatePrJson(42, "open", false, "devpilot/task-1", approvedSha, "master"))
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/check-runs"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateCheckRunsJson(new (long, string, string, string?, string)[] { (1L, "CI", "completed", "success", "Actions") }))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        });

        var handler = CreateHandler(repository, testHandler);
        var execution = SeedExecution(repository, approvedSha: approvedSha, ciStatus: ExecutionCiStatus.Success);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("422");

        var reloaded = await repository.GetByIdAsync(execution.Id);
        reloaded!.MergeStatus.Should().Be(ExecutionMergeStatus.Failed);
        reloaded.MergeAttemptId.Should().BeNull();
        reloaded.MergeClaimedAt.Should().BeNull();
    }

    [Fact]
    public async Task Merge_SuccessfulFinalization_ClearsMergeAttemptIdAndMergeClaimedAt()
    {
        var repository = new InMemoryExecutionRepository();
        var approvedSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341";

        var testHandler = CreateTestHttpMessageHandler(
            CreatePrJson(42, "open", false, "devpilot/task-1", approvedSha, "master"),
            CreateCheckRunsJson(new (long, string, string, string?, string)[] { (1L, "CI", "completed", "success", "Actions") }),
            "[]",
            mergeResponseJson: JsonSerializer.Serialize(new { sha = "8fd12faef652ff1b68836b5eca69ba0a94e80ce0", merged = true, message = "Pull request successfully merged" }),
            postMergePrJson: CreatePrJson(42, "closed", true, "devpilot/task-1", approvedSha, "master", mergedAt: DateTime.UtcNow));

        var handler = CreateHandler(repository, testHandler);
        var execution = SeedExecution(repository, approvedSha: approvedSha, ciStatus: ExecutionCiStatus.Success);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Success);

        var reloaded = await repository.GetByIdAsync(execution.Id);
        reloaded.Should().NotBeNull();
        reloaded!.MergeStatus.Should().Be(ExecutionMergeStatus.Merged);
        reloaded.MergeCommitSha.Should().NotBeNullOrEmpty();
        reloaded.MergedAt.Should().NotBeNull();
        reloaded.MergeMethod.Should().Be("merge");
        reloaded.PullRequestRemoteState.Should().Be(ExecutionPullRequestRemoteState.Merged);
        reloaded.PullRequestMergedAt.Should().NotBeNull();

        // Invariant: MergeAttemptId and MergeClaimedAt MUST be cleared (null)
        reloaded.MergeAttemptId.Should().BeNull();
        reloaded.MergeClaimedAt.Should().BeNull();
    }

    [Fact]
    public async Task SetExecutionMergedAsync_StaleAttemptId_DoesNotFinalizeOrClearActiveAttempt()
    {
        var repository = new InMemoryExecutionRepository();
        var execution = SeedExecution(repository);

        var activeAttemptId = Guid.NewGuid();
        var activeClaimedAt = DateTime.UtcNow;
        execution.MergeStatus = ExecutionMergeStatus.InProgress;
        execution.MergeAttemptId = activeAttemptId;
        execution.MergeClaimedAt = activeClaimedAt;

        var staleAttemptId = Guid.NewGuid();
        await repository.SetExecutionMergedAsync(execution.Id, staleAttemptId, "stale_sha_999", DateTime.UtcNow, "merge");

        var reloaded = await repository.GetByIdAsync(execution.Id);
        reloaded!.MergeStatus.Should().Be(ExecutionMergeStatus.InProgress);
        reloaded.MergeAttemptId.Should().Be(activeAttemptId);
        reloaded.MergeClaimedAt.Should().Be(activeClaimedAt);
        reloaded.MergeCommitSha.Should().BeNull();
    }

    [Fact]
    public async Task Merge_RepeatedPostAfterMerged_ZeroGitHubCallsAndNoDuplicateActivity()
    {
        var repository = new InMemoryExecutionRepository();
        var execution = SeedExecution(repository);
        execution.MergeStatus = ExecutionMergeStatus.Merged;
        execution.MergeCommitSha = "8fd12faef652ff1b68836b5eca69ba0a94e80ce0";
        execution.MergedAt = DateTime.UtcNow.AddMinutes(-5);
        execution.MergeMethod = "merge";

        var callCount = 0;
        var testHandler = new CustomTestHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var recorder = new TestActivityRecorder();
        var handler = CreateHandler(repository, testHandler, recorder: recorder);

        var result = await handler.HandleAsync(new MergeExecutionCommand(execution.Id));

        result.Status.Should().Be(MergeExecutionResultStatus.Success);
        result.Response!.MergeCommitSha.Should().Be("8fd12faef652ff1b68836b5eca69ba0a94e80ce0");
        result.Response.MergeStatus.Should().Be("Merged");

        callCount.Should().Be(0, "Repeated POST after Merged must issue zero GitHub calls");
        recorder.Recorded.Count.Should().Be(0, "Repeated POST after Merged must not create duplicate Merge activity");
    }

    private static GitHubPullRequestClient CreateClient(HttpMessageHandler httpHandler)
    {
        var clientFactory = new TestHttpClientFactory(httpHandler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitProvider:GitHub:BaseUrl"] = "https://api.github.com",
                ["GitProvider:GitHub:Token"] = "test-token"
            })
            .Build();

        return new GitHubPullRequestClient(clientFactory, config, NullLogger<GitHubPullRequestClient>.Instance);
    }

    private static MergeExecutionCommandHandler CreateHandler(
        IExecutionRepository repository,
        HttpMessageHandler httpHandler,
        bool allowNoChecks = false,
        IExecutionActivityRepository? activityRepo = null,
        IExecutionActivityRecorder? recorder = null)
    {
        var clientFactory = new TestHttpClientFactory(httpHandler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitProvider:GitHub:BaseUrl"] = "https://api.github.com",
                ["GitProvider:GitHub:Token"] = "test-token"
            })
            .Build();

        var client = new GitHubPullRequestClient(clientFactory, config, NullLogger<GitHubPullRequestClient>.Instance);
        var syncService = new ExecutionGitHubSyncService(client, NullLogger<ExecutionGitHubSyncService>.Instance);
        var recorderToUse = recorder ?? new TestActivityRecorder();
        var repoForActivity = activityRepo ?? new TestActivityRepository();
        var options = Options.Create(new MergePolicyOptions { AllowNoChecks = allowNoChecks });

        return new MergeExecutionCommandHandler(
            repository,
            client,
            syncService,
            recorder,
            repoForActivity,
            options,
            NullLogger<MergeExecutionCommandHandler>.Instance);
    }

    private static TaskExecution SeedExecution(
        InMemoryExecutionRepository repository,
        TaskExecutionStatus status = TaskExecutionStatus.Completed,
        ExecutionReviewStatus reviewStatus = ExecutionReviewStatus.Approved,
        ExecutionCommitStatus commitStatus = ExecutionCommitStatus.Committed,
        ExecutionPushStatus pushStatus = ExecutionPushStatus.Pushed,
        ExecutionPullRequestStatus prStatus = ExecutionPullRequestStatus.Open,
        ExecutionPullRequestRemoteState prRemoteState = ExecutionPullRequestRemoteState.Open,
        ExecutionPullRequestIntegrityStatus prIntegrity = ExecutionPullRequestIntegrityStatus.Valid,
        ExecutionCiStatus ciStatus = ExecutionCiStatus.Success,
        string approvedSha = "161ec91ce5edffc1770ec7617818e2b9d57f2341")
    {
        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            Title = "Task Title",
            Status = DevelopmentTaskStatus.Completed,
            RepositoryWorkspaceId = Guid.NewGuid(),
            RepositoryWorkspace = new RepositoryWorkspace
            {
                Id = Guid.NewGuid(),
                Owner = "enesscigdem",
                Repository = "DevPilot",
                Branch = "master"
            }
        };

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = status,
            ReviewStatus = reviewStatus,
            CommitStatus = commitStatus,
            CommitSha = approvedSha,
            CommittedAt = DateTime.UtcNow.AddMinutes(-20),
            PushStatus = pushStatus,
            RemoteBranchName = "devpilot/task-1",
            RemoteCommitSha = approvedSha,
            PushedAt = DateTime.UtcNow.AddMinutes(-15),
            BranchName = "devpilot/task-1",
            PullRequestStatus = prStatus,
            PullRequestNumber = 42,
            PullRequestUrl = "https://github.com/enesscigdem/DevPilot/pull/42",
            PullRequestBaseBranch = "master",
            PullRequestRemoteState = prRemoteState,
            PullRequestIntegrityStatus = prIntegrity,
            CiStatus = ciStatus,
            MergeStatus = ExecutionMergeStatus.None
        };

        repository.Seed(execution);
        return execution;
    }

    private static HttpMessageHandler CreateTestHttpMessageHandler(
        string prJson,
        string checkRunsJson,
        string statusesJson,
        string? mergeResponseJson = null,
        string? postMergePrJson = null)
    {
        var pullGetCount = 0;
        return new CustomTestHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith("/merge"))
            {
                var body = mergeResponseJson ?? JsonSerializer.Serialize(new { sha = "mergecommit123", merged = true, message = "Merged" });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/pulls/42"))
            {
                pullGetCount++;
                var body = (postMergePrJson != null && pullGetCount > 1) ? postMergePrJson : prJson;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/check-runs"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(checkRunsJson) };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/statuses"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(statusesJson) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
    }

    private static string CreatePrJson(int number, string state, bool merged, string headRef, string headSha, string baseRef, DateTime? mergedAt = null)
    {
        var mergedAtStr = mergedAt.HasValue ? $"\"{mergedAt.Value:O}\"" : "null";
        return $$"""
        {
          "number": {{number}},
          "html_url": "https://github.com/enesscigdem/DevPilot/pull/{{number}}",
          "state": "{{state}}",
          "merged": {{merged.ToString().ToLowerInvariant()}},
          "closed_at": null,
          "merged_at": {{mergedAtStr}},
          "body": "PR Body",
          "head": {
            "ref": "{{headRef}}",
            "sha": "{{headSha}}",
            "repo": { "name": "DevPilot", "owner": { "login": "enesscigdem" } }
          },
          "base": {
            "ref": "{{baseRef}}",
            "sha": "base123",
            "repo": { "name": "DevPilot", "owner": { "login": "enesscigdem" } }
          }
        }
        """;
    }

    private static string CreateCheckRunsJson((long id, string name, string status, string? conclusion, string appName)[] runs)
    {
        var runsJson = string.Join(",", runs.Select(r => $$"""
        {
          "id": {{r.id}},
          "name": "{{r.name}}",
          "status": "{{r.status}}",
          "conclusion": {{(r.conclusion != null ? $"\"{r.conclusion}\"" : "null")}},
          "started_at": "2026-08-16T18:00:00Z",
          "completed_at": "2026-08-16T18:01:00Z",
          "app": { "name": "{{r.appName}}" }
        }
        """));

        return $"{{\"total_count\":{runs.Length},\"check_runs\":[{runsJson}]}}";
    }

    private sealed class CustomTestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public CustomTestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public TestHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler);
    }

    private sealed class TestActivityRecorder : IExecutionActivityRecorder
    {
        public List<ExecutionActivity> Recorded { get; } = new();

        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default)
        {
            Recorded.Add(new ExecutionActivity { ExecutionId = executionId, Stage = stage, Status = status, Message = message });
            return Task.CompletedTask;
        }
    }

    private sealed class TestActivityRepository : IExecutionActivityRepository
    {
        public List<ExecutionActivity> ActivitiesToReturn { get; set; } = new();

        public Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionActivity>>(ActivitiesToReturn);
    }
}
