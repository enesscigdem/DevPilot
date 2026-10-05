using System.Text.Json;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

/// <summary>
/// Reliability / reporting Pass 2: test-weakening guard, explained verdicts, AI usage aggregation and
/// the persisted terminal verification snapshot.
/// </summary>
public sealed class ReliabilityPass2Tests
{
    // ── TestWeakeningDetector ──────────────────────────────────────────────────

    [Fact]
    public void Weakening_RemovedAssertion_IsSuspected()
    {
        var before = "[Fact] public void A() { Assert.Equal(1, x); Assert.True(y); }";
        var after = "[Fact] public void A() { Assert.Equal(1, x); }";

        var result = TestWeakeningDetector.Analyze(before, after);

        result.IsSuspected.Should().BeTrue();
        result.RemovedAssertions.Should().Be(1);
        result.Describe("tests/AppTests.cs").Should().Contain("1 assertion(s) removed");
    }

    [Theory]
    [InlineData("[Fact] public void A() { Assert.Equal(1, x); }", "[Fact(Skip = \"later\")] public void A() { Assert.Equal(1, x); }")]
    [InlineData("it('a', () => { expect(x).toBe(1); })", "it.skip('a', () => { expect(x).toBe(1); })")]
    [InlineData("def test_a():\n    assert x == 1", "@pytest.mark.skip\ndef test_a():\n    assert x == 1")]
    public void Weakening_AddedSkip_IsSuspected(string before, string after)
    {
        TestWeakeningDetector.Analyze(before, after).IsSuspected.Should().BeTrue();
    }

    [Fact]
    public void Weakening_DeletedTest_IsSuspected()
    {
        var before = "[Fact] public void A() { Assert.True(a); }\n[Fact] public void B() { Assert.True(b); }";
        var after = "[Fact] public void A() { Assert.True(a); }";

        var result = TestWeakeningDetector.Analyze(before, after);

        result.IsSuspected.Should().BeTrue();
        result.RemovedTests.Should().Be(1);
    }

    [Fact]
    public void Weakening_ChangedExpectationWithSameStructure_IsNotSuspected()
    {
        var before = "[Fact] public void A() { Assert.Equal(1, x); }";
        var after = "[Fact] public void A() { Assert.Equal(2, x); }";

        TestWeakeningDetector.Analyze(before, after).IsSuspected.Should().BeFalse();
    }

    [Fact]
    public void Weakening_AddedAssertions_IsNotSuspected()
    {
        var before = "[Fact] public void A() { Assert.Equal(1, x); }";
        var after = "[Fact] public void A() { Assert.Equal(1, x); Assert.NotNull(y); }";

        TestWeakeningDetector.Analyze(before, after).IsSuspected.Should().BeFalse();
    }

    // ── Evaluator: weakening is never a pass ───────────────────────────────────

    [Fact]
    public void Evaluator_TestWeakeningSuspected_ForcesNeedsReview_EvenWhenChecksPassed()
    {
        var activities = new List<ExecutionActivity>
        {
            Act(ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed, "Developer Agent completed.", new ExecutionActivityMetadata(EventKind: "GeneratingChange")),
            Act(ExecutionStage.Test, ExecutionActivityStatus.Completed, "Test repair may have weakened tests", new ExecutionActivityMetadata(RepositoryCheckId: "t", TestWeakeningSuspected: true)),
            Act(ExecutionStage.Test, ExecutionActivityStatus.Completed, "Tests passed.", new ExecutionActivityMetadata(RepositoryCheckId: "t", VerificationOutcome: "Verified")),
        };

        var outcome = ExecutionVerificationEvaluator.DetermineOutcome(NewExecution(), activities);

        outcome.Should().Be(ExecutionVerificationOutcome.NeedsReview);
        ExecutionVerificationEvaluator.IsDeliveryEligible(outcome).Should().BeFalse();
    }

    // ── Verdict builder ────────────────────────────────────────────────────────

    [Fact]
    public void Verdict_Verified_HasSuccessHeadlineAndNextAction()
    {
        var verdict = ExecutionVerdictBuilder.BuildVerdict(
            NewExecution(),
            new List<ExecutionActivity>(),
            ExecutionVerificationOutcome.Verified);

        verdict.Severity.Should().Be("success");
        verdict.Headline.Should().StartWith("Verified");
        verdict.RecommendedAction.Should().NotBeNullOrWhiteSpace();
        verdict.BaselineUnverified.Should().BeFalse();
    }

    [Fact]
    public void Verdict_FailingTests_ReportCountGroupsAndSuggestedFix()
    {
        var activities = new List<ExecutionActivity>
        {
            Act(ExecutionStage.Build, ExecutionActivityStatus.Completed, "Build passed.", new ExecutionActivityMetadata(EventKind: "VerifyingRepository", RepositoryCheckId: "b")),
            Act(ExecutionStage.Test, ExecutionActivityStatus.Started, "Test repair started (round 1/3).", new ExecutionActivityMetadata(EventKind: "FixingFailingTest", RepairKind: "Test", RepairRound: 1, RepositoryCheckId: "t")),
            Act(ExecutionStage.Test, ExecutionActivityStatus.Failed, "Test validation failed: npm test failed with exit code 1.",
                new ExecutionActivityMetadata(
                    EventKind: "StoppedWithEvidence",
                    RepositoryCheckId: "t",
                    VerificationOutcome: "NeedsReview",
                    FailingTestCount: 8,
                    FailingTestGroups: new[] { "7 × Unable to find an element with the text: Henüz görev yok.", "1 × Found multiple elements" },
                    SuggestedFix: "Fix the failing tests so they match how the application behaves now.")),
        };

        var verdict = ExecutionVerdictBuilder.BuildVerdict(NewExecution(), activities, ExecutionVerificationOutcome.NeedsReview);

        verdict.FailingTestCount.Should().Be(8);
        verdict.Headline.Should().Be("Needs review: The build passed, but 8 tests still fail after 1 automatic repair round(s).");
        verdict.FailingTestGroups.Should().HaveCount(2);
        verdict.SuggestedFix.Should().StartWith("Fix the failing tests");
        verdict.RecommendedAction.Should().Contain("Fix failing tests");
    }

    [Fact]
    public void Verdict_NoFailingTestDetail_KeepsGenericNeedsReviewHeadline()
    {
        var activities = new List<ExecutionActivity>
        {
            Act(ExecutionStage.Test, ExecutionActivityStatus.Failed, "Test validation failed.",
                new ExecutionActivityMetadata(EventKind: "StoppedWithEvidence", RepositoryCheckId: "t", VerificationOutcome: "NeedsReview")),
        };

        var verdict = ExecutionVerdictBuilder.BuildVerdict(NewExecution(), activities, ExecutionVerificationOutcome.NeedsReview);

        verdict.FailingTestCount.Should().Be(0);
        verdict.SuggestedFix.Should().BeNull();
    }

    [Fact]
    public void Verdict_SameFailureStop_ExplainsWhyAndWhatToDo()
    {
        var activities = new List<ExecutionActivity>
        {
            Act(ExecutionStage.Build, ExecutionActivityStatus.Started, "Compile repair started.", new ExecutionActivityMetadata(EventKind: "FixingBuildIssue", RepairKind: "Compile", RepairRound: 1, RepositoryCheckId: "b")),
            Act(ExecutionStage.Build, ExecutionActivityStatus.Started, "Compile repair started.", new ExecutionActivityMetadata(EventKind: "FixingBuildIssue", RepairKind: "Compile", RepairRound: 2, RepositoryCheckId: "b")),
            Act(ExecutionStage.Build, ExecutionActivityStatus.Failed, "Stopped with evidence: focused repair made no diagnostic progress.",
                new ExecutionActivityMetadata(EventKind: "StoppedWithEvidence", ProgressResult: "SameFailure", RepositoryCheckId: "b", VerificationOutcome: "NeedsReview")),
        };

        var verdict = ExecutionVerdictBuilder.BuildVerdict(NewExecution(), activities, ExecutionVerificationOutcome.NeedsReview);

        verdict.Severity.Should().Be("danger");
        verdict.CompileRepairRounds.Should().Be(2);
        verdict.Findings.Should().Contain(f => f.Kind == "FailureReason" && f.Message.Contains("persisted after the focused repair"));
        verdict.Findings.Should().Contain(f => f.Kind == "CompileRepair" && f.Message.Contains("2 round"));
        verdict.RecommendedAction.Should().Contain("retry");
    }

    [Fact]
    public void Verdict_BaselineUnverifiedFlakeStaleBaseAndWeakening_AreAllSurfaced()
    {
        var activities = new List<ExecutionActivity>
        {
            Act(ExecutionStage.Workspace, ExecutionActivityStatus.Completed, "Base freshness: x",
                new ExecutionActivityMetadata(EventKind: "BaseFreshness", BaseFreshness: "Behind", BaseBehindCount: 3, BaseCommitSha: "abc")),
            Act(ExecutionStage.Test, ExecutionActivityStatus.Completed, "Flaky failure detected: tests passed on confirmation rerun.", new ExecutionActivityMetadata(RepositoryCheckId: "t")),
            Act(ExecutionStage.Test, ExecutionActivityStatus.Started, "Baseline comparison inconclusive", new ExecutionActivityMetadata(EventKind: "BaselineUnverified", BaselineUnverified: true, RepositoryCheckId: "t")),
            Act(ExecutionStage.Test, ExecutionActivityStatus.Completed, "Test repair may have weakened tests (AppTests.cs: 1 assertion(s) removed); flagged for review.",
                new ExecutionActivityMetadata(EventKind: "TestWeakeningSuspected", TestWeakeningSuspected: true, RepositoryCheckId: "t")),
        };

        var verdict = ExecutionVerdictBuilder.BuildVerdict(NewExecution(), activities, ExecutionVerificationOutcome.NeedsReview);

        verdict.StaleBase.Should().BeTrue();
        verdict.BaseBehindCount.Should().Be(3);
        verdict.FlakeConfirmed.Should().BeTrue();
        verdict.BaselineUnverified.Should().BeTrue();
        verdict.TestWeakeningSuspected.Should().BeTrue();
        verdict.Headline.Should().Contain("weakened");
        verdict.Findings.Select(f => f.Kind).Should().Contain(new[] { "StaleBase", "Flake", "BaselineUnverified", "TestWeakening" });
    }

    [Fact]
    public void Verdict_DiscoveredButNotRunChecks_AreListed()
    {
        var activities = new List<ExecutionActivity>
        {
            Act(ExecutionStage.Workspace, ExecutionActivityStatus.Completed, "Repository verification checks discovered.",
                new ExecutionActivityMetadata(EventKind: "RepositoryPreflight", DiscoveredChecks: new[] { "dotnet:build", "dotnet:test" })),
            Act(ExecutionStage.Build, ExecutionActivityStatus.Failed, "Build validation failed",
                new ExecutionActivityMetadata(RepositoryCheckId: "dotnet:build", VerificationOutcome: "NeedsReview", EventKind: "StoppedWithEvidence")),
        };

        var verdict = ExecutionVerdictBuilder.BuildVerdict(NewExecution(), activities, ExecutionVerificationOutcome.NeedsReview);

        verdict.ChecksNotRun.Should().Equal("dotnet:test");
        verdict.Findings.Should().Contain(f => f.Kind == "ChecksNotRun");
    }

    [Fact]
    public void Verdict_FailedExecution_ShowsSanitizedErrorHeadline()
    {
        var execution = NewExecution();
        execution.Status = TaskExecutionStatus.Failed;
        execution.ErrorMessage = "Developer Agent failed: AI provider request failed for file 'src/A.cs'.";

        var verdict = ExecutionVerdictBuilder.BuildVerdict(execution, new List<ExecutionActivity>(), ExecutionVerificationOutcome.Failed);

        verdict.Severity.Should().Be("danger");
        verdict.Headline.Should().Contain("AI provider request failed");
    }

    // ── Usage aggregation ──────────────────────────────────────────────────────

    [Fact]
    public void Usage_AggregatesTokensCallsAndTimings_WithoutDoubleCountingBuildRuns()
    {
        var activities = new List<ExecutionActivity>
        {
            ProviderCall(1000, 200, 1200, ExecutionActivityStatus.Completed),
            ProviderCall(500, 100, 800, ExecutionActivityStatus.Completed),
            ProviderCall(null, null, 400, ExecutionActivityStatus.Failed),
            Act(ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed, "Generation technical summary.",
                new ExecutionActivityMetadata(EventKind: "GenerationSummary", TotalGenerationTimeMs: 5000)),
            // Build: two retry runs (3000 + 2500) and a final "passed" that repeats the first run (1500) => 5500
            Act(ExecutionStage.Build, ExecutionActivityStatus.Failed, "Build retry failed.",
                new ExecutionActivityMetadata(EventKind: "VerifyingRepository", RepositoryCheckId: "b", RepairKind: "Compile", StageDurationMs: 3000)),
            Act(ExecutionStage.Build, ExecutionActivityStatus.Completed, "Build retry passed.",
                new ExecutionActivityMetadata(EventKind: "VerifyingRepository", RepositoryCheckId: "b", RepairKind: "Compile", StageDurationMs: 2500)),
            Act(ExecutionStage.Build, ExecutionActivityStatus.Completed, "Build passed.",
                new ExecutionActivityMetadata(EventKind: "VerifyingRepository", RepositoryCheckId: "b", StageDurationMs: 1500, VerificationOutcome: "Verified")),
            Act(ExecutionStage.Test, ExecutionActivityStatus.Completed, "Tests passed.",
                new ExecutionActivityMetadata(EventKind: "ReadyForReview", RepositoryCheckId: "t", StageDurationMs: 4000)),
            Act(ExecutionStage.Build, ExecutionActivityStatus.Completed, "Compile repair completed.",
                new ExecutionActivityMetadata(EventKind: "FixingBuildIssue", RepairKind: "Compile", RepairRound: 1, StageDurationMs: 700)),
        };

        var usage = ExecutionVerdictBuilder.AggregateUsage(activities, new AiPricingOptions
        {
            InputPerMillionTokensUsd = 3m,
            OutputPerMillionTokensUsd = 15m,
        });

        usage.ProviderCalls.Should().Be(3);
        usage.FailedProviderCalls.Should().Be(1);
        usage.CallsWithoutTokenData.Should().Be(1);
        usage.InputTokens.Should().Be(1500);
        usage.OutputTokens.Should().Be(300);
        usage.TotalTokens.Should().Be(1800);
        usage.ProviderTimeMs.Should().Be(2400);
        usage.EstimatedCostUsd.Should().Be(0.009m);

        usage.StageTimings.Should().Contain(t => t.Stage == "Generation" && t.DurationMs == 5000);
        usage.StageTimings.Should().Contain(t => t.Stage == "Build" && t.DurationMs == 5500);
        usage.StageTimings.Should().Contain(t => t.Stage == "Test" && t.DurationMs == 4000);
        usage.StageTimings.Should().Contain(t => t.Stage == "Repair" && t.DurationMs == 700);
    }

    [Fact]
    public void Usage_WithoutPricingTable_ReportsNoCost_NeverAMadeUpNumber()
    {
        var usage = ExecutionVerdictBuilder.AggregateUsage(
            new List<ExecutionActivity> { ProviderCall(1000, 200, 1000, ExecutionActivityStatus.Completed) });

        usage.TotalTokens.Should().Be(1200);
        usage.EstimatedCostUsd.Should().BeNull();

        ExecutionVerdictBuilder.AggregateUsage(
            new List<ExecutionActivity> { ProviderCall(1000, 200, 1000, ExecutionActivityStatus.Completed) },
            new AiPricingOptions { InputPerMillionTokensUsd = 3m }).EstimatedCostUsd
            .Should().BeNull("a half-configured price table is not a price");
    }

    [Fact]
    public void Usage_NoProviderCalls_IsZeroNotNullTokens()
    {
        var usage = ExecutionVerdictBuilder.AggregateUsage(new List<ExecutionActivity>());

        usage.ProviderCalls.Should().Be(0);
        usage.TotalTokens.Should().Be(0);
        usage.StageTimings.Should().BeEmpty();
    }

    // ── Snapshot serialization / resolve ───────────────────────────────────────

    [Fact]
    public void Snapshot_RoundTrips_AndInvalidJsonIsIgnored()
    {
        var snapshot = ExecutionVerdictBuilder.BuildSnapshot(
            NewExecution(),
            new List<ExecutionActivity> { ProviderCall(10, 5, 100, ExecutionActivityStatus.Completed) },
            ExecutionVerificationOutcome.PartiallyVerified);

        var restored = ExecutionVerdictBuilder.TryDeserialize(ExecutionVerdictBuilder.Serialize(snapshot));

        restored.Should().NotBeNull();
        restored!.Verdict.Outcome.Should().Be("PartiallyVerified");
        restored.Usage.TotalTokens.Should().Be(15);
        restored.SchemaVersion.Should().Be(ExecutionVerdictBuilder.SnapshotSchemaVersion);

        ExecutionVerdictBuilder.TryDeserialize("{not json").Should().BeNull();
        ExecutionVerdictBuilder.TryDeserialize(null).Should().BeNull();
        ExecutionVerdictBuilder.TryDeserialize("{}").Should().BeNull();
    }

    [Fact]
    public void Resolve_PrefersPersistedSnapshotOnlyWhileItAgreesWithTheLiveOutcome()
    {
        var execution = NewExecution();
        var persisted = ExecutionVerdictBuilder.BuildSnapshot(execution, new List<ExecutionActivity>(), ExecutionVerificationOutcome.Verified) with
        {
            Usage = new ExecutionUsageDto(9, 0, 0, 1, 1, 2, 3, null, Array.Empty<ExecutionStageTimingDto>()),
        };
        execution.VerificationSnapshotJson = ExecutionVerdictBuilder.Serialize(persisted);

        var same = ExecutionVerdictBuilder.Resolve(execution, new List<ExecutionActivity>(), ExecutionVerificationOutcome.Verified);
        same.Usage.ProviderCalls.Should().Be(9, "the persisted record is the historical truth");

        var drifted = ExecutionVerdictBuilder.Resolve(execution, new List<ExecutionActivity>(), ExecutionVerificationOutcome.NeedsReview);
        drifted.Verdict.Outcome.Should().Be("NeedsReview", "a stale snapshot must never contradict gating");
        drifted.Usage.ProviderCalls.Should().Be(0);
    }

    // ── Recorder ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recorder_PersistsOutcomeAndSnapshot_AndSwallowsStoreFailures()
    {
        var executionId = Guid.NewGuid();
        var repo = new InMemoryExecutionRepository();
        repo.Executions[executionId] = new TaskExecution { Id = executionId, Status = TaskExecutionStatus.Completed };
        var activities = new FakeActivityRepository(
            Act(ExecutionStage.DeveloperAgent, ExecutionActivityStatus.Completed, "Developer Agent completed.", new ExecutionActivityMetadata(EventKind: "GeneratingChange")),
            Act(ExecutionStage.Build, ExecutionActivityStatus.Completed, "Build passed.", new ExecutionActivityMetadata(RepositoryCheckId: "b", VerificationOutcome: "Verified")),
            Act(ExecutionStage.Test, ExecutionActivityStatus.Completed, "Tests passed.", new ExecutionActivityMetadata(RepositoryCheckId: "t", VerificationOutcome: "Verified")),
            ProviderCall(100, 50, 500, ExecutionActivityStatus.Completed));
        var store = new CapturingStore();

        var recorder = new ExecutionVerificationSnapshotRecorder(
            repo, activities, store, NullLogger<ExecutionVerificationSnapshotRecorder>.Instance,
            new AiPricingOptions { InputPerMillionTokensUsd = 1m, OutputPerMillionTokensUsd = 2m });

        await recorder.RecordAsync(executionId);

        store.ExecutionId.Should().Be(executionId);
        store.Outcome.Should().Be("Verified");
        var snapshot = ExecutionVerdictBuilder.TryDeserialize(store.Json);
        snapshot.Should().NotBeNull();
        snapshot!.Usage.TotalTokens.Should().Be(150);
        snapshot.Usage.EstimatedCostUsd.Should().NotBeNull();

        // A failing store never breaks the execution result.
        var failing = new ExecutionVerificationSnapshotRecorder(
            repo, activities, new CapturingStore { Throw = true }, NullLogger<ExecutionVerificationSnapshotRecorder>.Instance);
        var act = async () => await failing.RecordAsync(executionId);
        await act.Should().NotThrowAsync();

        // Unknown execution is a no-op.
        await recorder.RecordAsync(Guid.NewGuid());
    }

    // ── Processor: weakened repair is flagged in code ──────────────────────────

    [Fact]
    public async Task Processor_TestRepairThatRemovesAssertion_IsFlagged_AndEndsInNeedsReview()
    {
        const string before = "public class AppTests { [Fact] public void A() { Assert.Equal(1, x); Assert.True(y); } }";
        const string weakened = "public class AppTests { [Fact] public void A() { Assert.Equal(1, x); } }";

        var run = await RunTestRepairScenarioAsync(before, weakened);

        run.Recorder.Activities.Should().Contain(a =>
            a.Metadata != null && a.Metadata.TestWeakeningSuspected == true && a.Metadata.RepairFiles!.Single() == "tests/AppTests.cs");
        run.Outcome.Should().Be(ExecutionVerificationOutcome.NeedsReview);
        ExecutionVerdictBuilder.BuildVerdict(NewExecution(), run.ActivityEntities, run.Outcome)
            .TestWeakeningSuspected.Should().BeTrue();
    }

    [Fact]
    public async Task Processor_TestRepairThatKeepsAssertionStructure_IsNotFlagged()
    {
        const string before = "public class AppTests { [Fact] public void A() { Assert.Equal(1, x); } }";
        const string corrected = "public class AppTests { [Fact] public void A() { Assert.Equal(2, x); } }";

        var run = await RunTestRepairScenarioAsync(before, corrected);

        run.Recorder.Activities.Should().NotContain(a => a.Metadata != null && a.Metadata.TestWeakeningSuspected == true);
        run.Outcome.Should().Be(ExecutionVerificationOutcome.Verified);
    }

    private static async Task<(ReliabilityRecorder Recorder, ExecutionVerificationOutcome Outcome, List<ExecutionActivity> ActivityEntities)>
        RunTestRepairScenarioAsync(string beforeContent, string repairedContent)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "DevPilotWeakening_" + Guid.NewGuid().ToString("N"));
        var testFile = Path.Combine(workspace, "tests", "AppTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        File.WriteAllText(testFile, beforeContent);

        try
        {
            var workspaceForStack = workspace.Replace('\\', '/');
            var failure = "Failed DevPilot.Tests.AppTests.A [5 ms]\n" +
                          "  Error Message:\n   Expected 1 but got 0.\n" +
                          "  Stack Trace:\n" +
                          $"     at DevPilot.Tests.AppTests.A() in {workspaceForStack}/tests/AppTests.cs:line 1\n" +
                          "Failed! - Failed: 1, Passed: 5, Skipped: 0, Total: 6";

            var testRuns = 0;
            var runner = new ScriptedRunner(_ => ++testRuns == 1
                ? new RepositoryCheckResult { Success = false, ExitCode = 1, ErrorMessage = "failed", StdOut = failure, FailureCategory = RepositoryCheckFailureCategory.VerificationFailure }
                : new RepositoryCheckResult { Success = true, ExitCode = 0 });

            var agent = new RewritingAgent("tests/AppTests.cs", () => File.WriteAllText(testFile, repairedContent));
            var recorder = new ReliabilityRecorder();

            var processor = new GitWorkspaceExecutionProcessor(
                new RealPathWorkspaceManager(workspace),
                new InMemoryExecutionRepository(),
                new PlanRepo("tests/AppTests.cs"),
                agent,
                runner,
                recorder,
                NullLogger<GitWorkspaceExecutionProcessor>.Instance,
                reliabilityOptions: new ExecutionReliabilityOptions { MaxFlakeReruns = 0 }.Normalize());

            await processor.ProcessAsync(new ExecutionProcessingContext(
                Guid.NewGuid(), Guid.NewGuid(), "Task", "Desc", null, Guid.NewGuid(), "/src", "Summary"));

            agent.RepairCalls.Should().Be(1);

            var execution = NewExecution();
            var entities = recorder.Activities
                .Select((a, i) => new ExecutionActivity
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = execution.Id,
                    Stage = a.Stage,
                    Status = a.Status,
                    Message = a.Message,
                    CreatedAt = DateTime.UtcNow.AddMilliseconds(i),
                    MetadataJson = a.Metadata == null ? null : JsonSerializer.Serialize(a.Metadata),
                })
                .ToList();

            return (recorder, ExecutionVerificationEvaluator.DetermineOutcome(execution, entities), entities);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    // ── Helpers / fakes ────────────────────────────────────────────────────────

    private static TaskExecution NewExecution() => new() { Id = Guid.NewGuid(), Status = TaskExecutionStatus.Completed };

    private static ExecutionActivity Act(
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        ExecutionActivityMetadata? metadata = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            Stage = stage,
            Status = status,
            Message = message,
            CreatedAt = DateTime.UtcNow,
            MetadataJson = metadata == null ? null : JsonSerializer.Serialize(metadata),
        };

    private static ExecutionActivity ProviderCall(int? input, int? output, long durationMs, ExecutionActivityStatus status) =>
        Act(
            ExecutionStage.DeveloperAgent,
            status,
            "Provider call completed: Generation.",
            new ExecutionActivityMetadata(
                EventKind: "ProviderCall",
                ProviderCallKind: "Generation",
                InputTokens: input,
                OutputTokens: output,
                StageDurationMs: durationMs));

    private sealed class FakeActivityRepository : IExecutionActivityRepository
    {
        private readonly IReadOnlyList<ExecutionActivity> _activities;

        public FakeActivityRepository(params ExecutionActivity[] activities) => _activities = activities;

        public Task<IReadOnlyList<ExecutionActivity>> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_activities);
    }

    private sealed class CapturingStore : IExecutionVerificationSnapshotStore
    {
        public Guid? ExecutionId { get; private set; }
        public string? Outcome { get; private set; }
        public string? Json { get; private set; }
        public bool Throw { get; set; }

        public Task SaveAsync(Guid executionId, string outcome, string snapshotJson, CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("db down");
            }

            ExecutionId = executionId;
            Outcome = outcome;
            Json = snapshotJson;
            return Task.CompletedTask;
        }
    }

    private sealed class ReliabilityRecorder : IExecutionActivityRecorder
    {
        public List<(ExecutionStage Stage, ExecutionActivityStatus Status, string Message, ExecutionActivityMetadata? Metadata)> Activities { get; } = new();

        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default)
        {
            Activities.Add((stage, status, message, metadata));
            return Task.CompletedTask;
        }
    }

    private sealed class RealPathWorkspaceManager : IExecutionWorkspaceManager
    {
        private readonly string _path;

        public RealPathWorkspaceManager(string path) => _path = path;

        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionWorkspaceResult(_path, "devpilot/branch", true, BaseCommitSha: "base123"));

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceVerificationResult(true, true, true, true));
    }

    private sealed class PlanRepo : IImpactAnalysisRepository
    {
        private readonly string[] _files;

        public PlanRepo(params string[] files) => _files = files;

        public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TaskImpactAnalysis?>(new TaskImpactAnalysis
            {
                Id = Guid.NewGuid(),
                DevelopmentTaskId = taskId,
                Status = ImpactAnalysisStatus.Completed,
                StructuredResult = new ImpactAnalysisResultData
                {
                    ImpactedFiles = _files.Select(f => new ImpactedFile { FilePath = f, ChangeType = ImpactFileChangeType.Modify }).ToList(),
                },
            });

        public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> StartAnalysisAtomicAsync(TaskImpactAnalysis analysis, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasActiveAnalysisForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleAnalysesAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class RewritingAgent : IDeveloperAgent
    {
        private readonly string _file;
        private readonly Action _onRepair;

        public RewritingAgent(string file, Action onRepair)
        {
            _file = file;
            _onRepair = onRepair;
        }

        public int RepairCalls { get; private set; }

        public Task<DeveloperAgentResult> GenerateAndApplyEditsAsync(DeveloperAgentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(DeveloperAgentResult.Ok(new List<string> { _file }, model: "test-model"));

        public Task<DeveloperAgentResult> ExecuteFocusedRepairAsync(FocusedRepairRequest request, CancellationToken cancellationToken = default)
        {
            RepairCalls++;
            _onRepair();
            return Task.FromResult(DeveloperAgentResult.Ok(request.RepairFiles.ToList(), model: "test-model"));
        }
    }

    private sealed class ScriptedRunner : IRepositoryCheckRunner
    {
        private static readonly RepositoryCheck Build = new(
            "dotnet:build", ".NET build", RepositoryCheckKind.Build, "dotnet", "dotnet", new[] { "build" }, "", true,
            TimeSpan.FromMinutes(1), RepositoryCheckSource.DotNetManifest, "App.sln", Order: 100);

        private static readonly RepositoryCheck Test = new(
            "dotnet:test", ".NET tests", RepositoryCheckKind.Test, "dotnet", "dotnet", new[] { "test" }, "", true,
            TimeSpan.FromMinutes(1), RepositoryCheckSource.DotNetManifest, "App.Tests.csproj",
            SupportsSkipBuild: true, SupportsTargetedTest: true, Order: 400);

        private readonly Func<RepositoryCheckExecutionRequest, RepositoryCheckResult> _testResult;

        public ScriptedRunner(Func<RepositoryCheckExecutionRequest, RepositoryCheckResult> testResult) => _testResult = testResult;

        public Task<RepositoryProfile> DiscoverAsync(RepositoryPreflightRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryProfile(RepositoryVerificationState.Configured, new[] { "dotnet" }, new[] { Build, Test }));

        public Task<RepositoryCheckResult> ExecuteAsync(RepositoryCheckExecutionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Check.Kind == RepositoryCheckKind.Build
                ? new RepositoryCheckResult { Success = true, ExitCode = 0 }
                : _testResult(request));
    }
}
