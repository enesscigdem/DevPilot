using System.Text.Json;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.RepositoryClone;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

/// <summary>
/// Reliability Pass 1: flake confirmation budget, compiler repair target rotation, baseline-unknown handling
/// and the single bound reliability options source.
/// </summary>
public sealed class ReliabilityPass1ProcessorTests
{
    private const string FocusedTestFailureOutput = """
        Failed DevPilot.Tests.AppTests.TestMethod1 [5 ms]
          Error Message:
           Expected 100 but got 0.
          Stack Trace:
             at DevPilot.Tests.AppTests.TestMethod1() in /workspace/path/src/App.cs:line 20
        Failed! - Failed: 1, Passed: 5, Skipped: 0, Total: 6
        """;

    private const string TwoFileCompilerOutput =
        "src/A.cs(10,5): error CS0103: The name 'alpha' does not exist in the current context\n" +
        "src/B.cs(3,1): error CS0103: The name 'beta' does not exist in the current context";

    private static readonly RepositoryCheck BuildCheck = new(
        "dotnet:build",
        ".NET build",
        RepositoryCheckKind.Build,
        "dotnet",
        "dotnet",
        new[] { "build" },
        "",
        true,
        TimeSpan.FromMinutes(1),
        RepositoryCheckSource.DotNetManifest,
        "App.sln",
        Order: 100);

    private static readonly RepositoryCheck TestCheck = new(
        "dotnet:test",
        ".NET tests",
        RepositoryCheckKind.Test,
        "dotnet",
        "dotnet",
        new[] { "test" },
        "",
        true,
        TimeSpan.FromMinutes(1),
        RepositoryCheckSource.DotNetManifest,
        "App.Tests.csproj",
        SupportsSkipBuild: true,
        SupportsTargetedTest: true,
        Order: 400);

    // ── Options: one bound source ──────────────────────────────────────────────

    [Fact]
    public void Options_ExecutionReliabilitySection_WinsOverLegacyDeveloperAgentKeys()
    {
        var config = Config(new()
        {
            ["DeveloperAgent:MaxGenerationCalls"] = "10",
            ["DeveloperAgent:MaxConcurrentFileGenerations"] = "3",
            ["DeveloperAgent:MaxCompileRepairRounds"] = "9",
            ["ExecutionReliability:MaxGenerationCalls"] = "25",
            ["ExecutionReliability:MaxCompileRepairRounds"] = "4",
        });

        var options = ExecutionReliabilityOptionsFactory.Create(config);

        options.MaxGenerationCalls.Should().Be(25);
        options.MaxCompileRepairRounds.Should().Be(4);
        options.MaxConcurrentFileGenerations.Should().Be(3, "legacy keys still act as fallbacks");
    }

    [Fact]
    public void Options_Defaults_PreserveProvenRuntimeValues()
    {
        var options = ExecutionReliabilityOptionsFactory.Create(null);

        options.MaxCompileRepairRounds.Should().Be(3);
        options.MaxTestRepairRounds.Should().Be(2);
        options.MaxGenerationCalls.Should().Be(40);
        options.MaxConcurrentFileGenerations.Should().Be(1);
        options.MaxOutputTokens.Should().Be(32768);
        options.EffectiveMaxCompactRetryOutputTokens.Should().Be(32768);
        options.MaxFlakeReruns.Should().Be(1);
        options.ImpactAnalysisMaxOutputTokens.Should().Be(6144);
        options.ImpactAnalysisRecoveryMaxOutputTokens.Should().Be(8192);
    }

    [Fact]
    public void Options_AreClampedToBoundedValues()
    {
        var config = Config(new()
        {
            ["ExecutionReliability:MaxFlakeReruns"] = "9",
            ["ExecutionReliability:MaxConcurrentFileGenerations"] = "99",
            ["ExecutionReliability:ImpactAnalysisMaxOutputTokens"] = "999999",
            ["ExecutionReliability:ImpactAnalysisRecoveryMaxOutputTokens"] = "10",
        });

        var options = ExecutionReliabilityOptionsFactory.Create(config);

        options.MaxFlakeReruns.Should().Be(1);
        options.MaxConcurrentFileGenerations.Should().Be(4);
        options.ImpactAnalysisMaxOutputTokens.Should().Be(16384);
        options.ImpactAnalysisRecoveryMaxOutputTokens.Should().BeGreaterThanOrEqualTo(options.ImpactAnalysisMaxOutputTokens);
    }

    [Fact]
    public void CommittedAppSettings_HaveNoMachineSpecificWorkspaceRoot_AndSingleReliabilitySection()
    {
        var root = FindRepoRoot();
        var json = File.ReadAllText(Path.Combine(root, "src", "DevPilot.Api", "appsettings.json"));
        using var doc = JsonDocument.Parse(json);

        var workspaceRoot = doc.RootElement.GetProperty("RepositoryClone").GetProperty("WorkspaceRoot").GetString();
        workspaceRoot.Should().BeNullOrEmpty("a committed absolute path breaks every other machine");
        json.Should().NotContain("/Users/");

        doc.RootElement.TryGetProperty("ExecutionReliability", out _).Should().BeTrue();
        var agent = doc.RootElement.GetProperty("DeveloperAgent");
        agent.TryGetProperty("MaxGenerationCalls", out _).Should().BeFalse();
        agent.TryGetProperty("MaxConcurrentFileGenerations", out _).Should().BeFalse();
    }

    // ── Compiler repair target rotation ────────────────────────────────────────

    [Fact]
    public async Task CompileRepair_SameFailureAfterFirstTarget_RotatesToNextExactCandidate_OneFilePerRepair()
    {
        var runner = new ScriptedCheckRunner(
            buildResults: new[] { Fail(TwoFileCompilerOutput), Fail(TwoFileCompilerOutput), Pass() },
            testResult: _ => Pass());
        var agent = new FakeAgent("src/A.cs", "src/B.cs");
        var recorder = new FakeRecorder();

        await CreateProcessor(agent, runner, recorder).ProcessAsync(NewContext());

        agent.RepairRequests.Select(r => r.RepairFiles.Single()).Should().Equal("src/A.cs", "src/B.cs");
        runner.BuildRequests.Should().HaveCount(3, "every repair is followed by a fresh rebuild");
        recorder.Activities
            .Where(a => a.Metadata?.RepairRound == 2 && a.Metadata.RepairSelectionReason != null)
            .Should().Contain(a => a.Metadata!.RepairSelectionReason == "NextUnattemptedFile" &&
                                    a.Metadata.RepairFiles!.Single() == "src/B.cs");
        recorder.Activities.Should().NotContain(a => a.Message.Contains("no diagnostic progress"));
    }

    [Fact]
    public async Task CompileRepair_SameFailureAndNoCandidateLeft_StopsWithEvidence()
    {
        const string oneFile = "src/A.cs(10,5): error CS0103: The name 'alpha' does not exist in the current context";
        var runner = new ScriptedCheckRunner(
            buildResults: new[] { Fail(oneFile), Fail(oneFile), Fail(oneFile) },
            testResult: _ => Pass());
        var agent = new FakeAgent("src/A.cs", "src/B.cs");
        var recorder = new FakeRecorder();

        await CreateProcessor(agent, runner, recorder).ProcessAsync(NewContext());

        agent.RepairRequests.Should().HaveCount(1, "the only exact candidate was already attempted");
        recorder.Activities.Should().Contain(a =>
            a.Message == "Stopped with evidence: focused repair made no diagnostic progress." &&
            a.Metadata!.ProgressResult == "SameFailure");
    }

    [Fact]
    public async Task CompileRepair_NoOpRepairOfFirstTarget_TriesNextCandidateBeforeStopping()
    {
        var runner = new ScriptedCheckRunner(
            buildResults: new[] { Fail(TwoFileCompilerOutput) },
            testResult: _ => Pass());
        var agent = new FakeAgent("src/A.cs", "src/B.cs");
        var recorder = new FakeRecorder();

        var processor = CreateProcessor(
            agent,
            runner,
            recorder,
            fingerprintCalculator: new ConstantFingerprintCalculator("unchanged"));

        await processor.ProcessAsync(NewContext());

        agent.RepairRequests.Select(r => r.RepairFiles.Single()).Should().Equal("src/A.cs", "src/B.cs");
        recorder.Activities.Should().Contain(a => a.Message.Contains("trying the next candidate file"));
        recorder.Activities.Should().Contain(a => a.Message == "Stopped with evidence: focused repair produced no worktree change.");
        runner.BuildRequests.Should().HaveCount(1, "a no-op repair changes nothing, so no rebuild is needed");
    }

    // ── Baseline Unknown ───────────────────────────────────────────────────────

    [Fact]
    public async Task BaselineUnknown_CompileFailure_ContinuesRepair_AndNeverReportsVerified()
    {
        const string oneFile = "src/A.cs(10,5): error CS0103: The name 'alpha' does not exist in the current context";
        var runner = new ScriptedCheckRunner(
            buildResults: new[] { Fail(oneFile), Pass() },
            testResult: _ => Pass());
        var agent = new FakeAgent("src/A.cs");
        var recorder = new FakeRecorder();

        await CreateProcessor(agent, runner, recorder, baseline: UnknownBaseline()).ProcessAsync(NewContext());

        agent.RepairRequests.Should().HaveCount(1, "an inconclusive baseline must not stop bounded repair");
        recorder.Activities.Should().Contain(a =>
            a.Metadata != null && a.Metadata.BaselineUnverified == true && a.Metadata.EventKind == "BaselineUnverified");
        recorder.Activities.Should().NotContain(a => a.Message.StartsWith("Needs review: baseline comparison was inconclusive"));

        DetermineOutcome(recorder).Should().Be(ExecutionVerificationOutcome.PartiallyVerified);
        ExecutionVerificationEvaluator.IsDeliveryEligible(ExecutionVerificationOutcome.PartiallyVerified).Should().BeTrue();
    }

    [Fact]
    public async Task BaselineKnown_SameRepair_StillReportsVerified()
    {
        const string oneFile = "src/A.cs(10,5): error CS0103: The name 'alpha' does not exist in the current context";
        var runner = new ScriptedCheckRunner(
            buildResults: new[] { Fail(oneFile), Pass() },
            testResult: _ => Pass());
        var agent = new FakeAgent("src/A.cs");
        var recorder = new FakeRecorder();

        await CreateProcessor(agent, runner, recorder, baseline: new FakeBaseline()).ProcessAsync(NewContext());

        agent.RepairRequests.Should().HaveCount(1);
        recorder.Activities.Should().NotContain(a => a.Metadata != null && a.Metadata.BaselineUnverified == true);
        DetermineOutcome(recorder).Should().Be(ExecutionVerificationOutcome.Verified);
    }

    [Fact]
    public async Task BaselineUnknown_WhenRepairIsDisabled_EndsInNeedsReview_NotVerified()
    {
        const string oneFile = "src/A.cs(10,5): error CS0103: The name 'alpha' does not exist in the current context";
        var runner = new ScriptedCheckRunner(buildResults: new[] { Fail(oneFile) }, testResult: _ => Pass());
        var agent = new FakeAgent("src/A.cs");
        var recorder = new FakeRecorder();

        var processor = CreateProcessor(
            agent,
            runner,
            recorder,
            baseline: UnknownBaseline(),
            options: new ExecutionReliabilityOptions { MaxCompileRepairRounds = 0 });

        await processor.ProcessAsync(NewContext());

        agent.RepairRequests.Should().BeEmpty();
        recorder.Activities.Should().Contain(a =>
            a.Metadata != null && a.Metadata.VerificationOutcome == "NeedsReview" && a.Metadata.BaselineUnverified == true);
        DetermineOutcome(recorder).Should().Be(ExecutionVerificationOutcome.NeedsReview);
    }

    [Fact]
    public async Task BaselineUnknown_TestFailure_ContinuesRepair_AndIsReportedBaselineUnverified()
    {
        var testRuns = 0;
        var runner = new ScriptedCheckRunner(
            buildResults: Array.Empty<RepositoryCheckResult>(),
            testResult: request =>
            {
                testRuns++;
                // 1st: full failure. After the repair: targeted pass, then full pass.
                return testRuns == 1 ? Fail(FocusedTestFailureOutput) : Pass();
            });
        var agent = new FakeAgent("src/App.cs");
        var recorder = new FakeRecorder();

        await CreateProcessor(
            agent,
            runner,
            recorder,
            baseline: UnknownBaseline(),
            options: new ExecutionReliabilityOptions { MaxFlakeReruns = 0 }).ProcessAsync(NewContext());

        agent.RepairRequests.Should().HaveCount(1);
        recorder.Activities.Should().Contain(a => a.Metadata != null && a.Metadata.BaselineUnverified == true);
        DetermineOutcome(recorder).Should().Be(ExecutionVerificationOutcome.PartiallyVerified);
    }

    // ── Flake confirmation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Flake_TargetedRerunReproducesFailure_NoFullSuiteRerun_AndBudgetIsSpentOncePerExecution()
    {
        var fullRuns = 0;
        var targetedCalls = 0;
        var runner = new ScriptedCheckRunner(
            buildResults: Array.Empty<RepositoryCheckResult>(),
            testResult: request =>
            {
                if (request.TestFilter != null)
                {
                    // Targeted: confirmation (1st) fails again; post-repair targeted passes.
                    return targetedCalls++ == 0 ? Fail(FocusedTestFailureOutput) : Pass();
                }

                fullRuns++;
                return fullRuns == 1 ? Fail(FocusedTestFailureOutput) : Pass();
            });
        var agent = new FakeAgent("src/App.cs");
        var recorder = new FakeRecorder();

        await CreateProcessor(agent, runner, recorder).ProcessAsync(NewContext());

        // initial full (fail) + post-repair full (pass); the confirmation was a cheap targeted rerun only.
        runner.TestRequests.Count(r => r.TestFilter == null).Should().Be(2);
        runner.TestRequests.Count(r => r.TestFilter != null).Should().Be(2, "one confirmation + one post-repair targeted run");
        runner.TestRequests.Take(2).Select(r => r.TestFilter).Should().Equal(null, "DevPilot.Tests.AppTests.TestMethod1");
        agent.RepairRequests.Should().HaveCount(1);
        recorder.Activities.Should().NotContain(a => a.Message.Contains("Flaky failure detected"));
    }

    [Fact]
    public async Task Flake_TargetedRerunPasses_OneFullRerunDecides_FlakyFailureSkipsRepair()
    {
        var fullRuns = 0;
        var runner = new ScriptedCheckRunner(
            buildResults: Array.Empty<RepositoryCheckResult>(),
            testResult: request =>
            {
                if (request.TestFilter != null)
                {
                    return Pass();
                }

                fullRuns++;
                return fullRuns == 1 ? Fail(FocusedTestFailureOutput) : Pass();
            });
        var agent = new FakeAgent("src/App.cs");
        var recorder = new FakeRecorder();

        await CreateProcessor(agent, runner, recorder).ProcessAsync(NewContext());

        agent.RepairRequests.Should().BeEmpty("a passing confirmation rerun means the failure was flaky");
        runner.TestRequests.Should().HaveCount(3, "full fail, targeted pass, one full rerun");
        recorder.Activities.Should().Contain(a => a.Message.Contains("Flaky failure detected"));
        DetermineOutcome(recorder).Should().Be(ExecutionVerificationOutcome.Verified);
    }

    [Fact]
    public async Task Flake_NoReliableTestName_UsesExactlyOneFullRerun()
    {
        var runner = new ScriptedCheckRunner(
            buildResults: Array.Empty<RepositoryCheckResult>(),
            testResult: _ => Fail("Test run failed."));
        var agent = new FakeAgent("src/App.cs");
        var recorder = new FakeRecorder();

        await CreateProcessor(agent, runner, recorder).ProcessAsync(NewContext());

        runner.TestRequests.Where(r => r.TestFilter == null).Should().HaveCount(2, "initial run + a single confirmation rerun (previously up to 3 runs)");
        runner.TestRequests.Should().OnlyContain(r => r.TestFilter == null);
    }

    [Fact]
    public async Task Flake_DisabledWhenNoConfigurationOrOptionsAreSupplied()
    {
        var runner = new ScriptedCheckRunner(
            buildResults: Array.Empty<RepositoryCheckResult>(),
            testResult: _ => Fail("Test run failed."));
        var agent = new FakeAgent("src/App.cs");

        var processor = new GitWorkspaceExecutionProcessor(
            new FakeWorkspaceManager(),
            new InMemoryExecutionRepository(),
            new FakeImpactRepo(),
            agent,
            runner,
            new FakeRecorder(),
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        await processor.ProcessAsync(NewContext());

        runner.TestRequests.Should().HaveCount(1, "bare processors stay deterministic");
    }

    // ── Repository freshness evidence ──────────────────────────────────────────

    [Fact]
    public async Task Freshness_IsRefreshedBeforeWorkspacePrep_AndRecordedAsBaseEvidence()
    {
        var freshness = new FakeFreshness(new RepositoryFreshnessResult(
            RepositoryFreshnessStatus.FastForwarded, "main", "new111", "new111", 0, 0, "old000", "Base fast-forwarded 2 commit(s) to match origin."));
        var runner = new ScriptedCheckRunner(Array.Empty<RepositoryCheckResult>(), _ => Pass());
        var recorder = new FakeRecorder();

        await CreateProcessor(new FakeAgent("src/App.cs"), runner, recorder, freshness: freshness)
            .ProcessAsync(new ExecutionProcessingContext(
                Guid.NewGuid(), Guid.NewGuid(), "Task", "Desc", null, Guid.NewGuid(), "/src", "Summary",
                RepositoryOwner: "acme", RepositoryName: "widgets", BaseBranch: "main"));

        freshness.Requests.Should().ContainSingle();
        freshness.Requests[0].Owner.Should().Be("acme");
        freshness.Requests[0].Repository.Should().Be("widgets");
        freshness.Requests[0].LocalPath.Should().Be("/src");

        var evidence = recorder.Activities.Single(a => a.Metadata?.EventKind == "BaseFreshness");
        evidence.Metadata!.BaseFreshness.Should().Be("FastForwarded");
        evidence.Metadata.BaseCommitSha.Should().Be("base123");
        evidence.Metadata.RemoteBaseCommitSha.Should().Be("new111");
        evidence.Metadata.BaseBranchName.Should().Be("main");
        evidence.Status.Should().Be(ExecutionActivityStatus.Completed);
        DetermineOutcome(recorder).Should().Be(ExecutionVerificationOutcome.Verified, "freshness evidence never changes the verification verdict");
    }

    [Fact]
    public async Task Freshness_StaleBase_IsRecordedButNeverBlocksExecution()
    {
        var freshness = new FakeFreshness(new RepositoryFreshnessResult(
            RepositoryFreshnessStatus.Behind, "main", "aaa", "bbb", 4, 0, null, "Base is 4 commit(s) behind origin but local changes exist; not updated."));
        var runner = new ScriptedCheckRunner(Array.Empty<RepositoryCheckResult>(), _ => Pass());
        var recorder = new FakeRecorder();

        await CreateProcessor(new FakeAgent("src/App.cs"), runner, recorder, freshness: freshness)
            .ProcessAsync(new ExecutionProcessingContext(
                Guid.NewGuid(), Guid.NewGuid(), "Task", "Desc", null, Guid.NewGuid(), "/src", "Summary",
                RepositoryOwner: "acme", RepositoryName: "widgets", BaseBranch: "main"));

        recorder.Activities.Single(a => a.Metadata?.EventKind == "BaseFreshness").Metadata!.BaseBehindCount.Should().Be(4);
        runner.TestRequests.Should().NotBeEmpty("the execution still ran");
    }

    [Fact]
    public async Task Freshness_ServiceFailure_DoesNotFailExecution()
    {
        var freshness = new FakeFreshness(null) { Throw = true };
        var runner = new ScriptedCheckRunner(Array.Empty<RepositoryCheckResult>(), _ => Pass());
        var recorder = new FakeRecorder();

        var act = async () => await CreateProcessor(new FakeAgent("src/App.cs"), runner, recorder, freshness: freshness)
            .ProcessAsync(new ExecutionProcessingContext(
                Guid.NewGuid(), Guid.NewGuid(), "Task", "Desc", null, Guid.NewGuid(), "/src", "Summary",
                RepositoryOwner: "acme", RepositoryName: "widgets", BaseBranch: "main"));

        await act.Should().NotThrowAsync();
        recorder.Activities.Should().NotContain(a => a.Metadata != null && a.Metadata.EventKind == "BaseFreshness");
        runner.TestRequests.Should().NotBeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static GitWorkspaceExecutionProcessor CreateProcessor(
        FakeAgent agent,
        ScriptedCheckRunner runner,
        FakeRecorder recorder,
        FakeBaseline? baseline = null,
        ExecutionReliabilityOptions? options = null,
        IExecutionChangeFingerprintCalculator? fingerprintCalculator = null,
        IRepositoryFreshnessService? freshness = null) =>
        new(
            new FakeWorkspaceManager(),
            new InMemoryExecutionRepository(),
            new FakeImpactRepo(),
            agent,
            runner,
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance,
            changeFingerprintCalculator: fingerprintCalculator,
            baselineVerificationService: baseline,
            reliabilityOptions: (options ?? new ExecutionReliabilityOptions()).Normalize(),
            freshnessService: freshness);

    private static ExecutionProcessingContext NewContext() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Task", "Desc", null, Guid.NewGuid(), "/src", "Summary");

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static RepositoryCheckResult Pass() => new()
    {
        Success = true,
        ExitCode = 0,
        FailureCategory = RepositoryCheckFailureCategory.None,
    };

    private static RepositoryCheckResult Fail(string stdout) => new()
    {
        Success = false,
        ExitCode = 1,
        ErrorMessage = "check failed.",
        StdOut = stdout,
        FailureCategory = RepositoryCheckFailureCategory.VerificationFailure,
    };

    private static FakeBaseline UnknownBaseline() => new(new BaselineFailureComparison(
        BaselineFailureClassification.Unknown,
        0,
        0,
        0,
        Array.Empty<NormalizedFailureItem>(),
        Array.Empty<NormalizedFailureItem>(),
        Array.Empty<NormalizedFailureItem>(),
        "baseline run timed out"));

    private static ExecutionVerificationOutcome DetermineOutcome(FakeRecorder recorder)
    {
        var executionId = Guid.NewGuid();
        var created = DateTime.UtcNow;
        var activities = recorder.Activities
            .Select((a, i) => new ExecutionActivity
            {
                Id = Guid.NewGuid(),
                ExecutionId = executionId,
                Stage = a.Stage,
                Status = a.Status,
                Message = a.Message,
                CreatedAt = created.AddMilliseconds(i),
                MetadataJson = a.Metadata == null ? null : JsonSerializer.Serialize(a.Metadata),
            })
            .ToList();

        return ExecutionVerificationEvaluator.DetermineOutcome(
            new TaskExecution { Id = executionId, Status = TaskExecutionStatus.Completed },
            activities);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DevPilot.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate DevPilot.sln.");
    }

    private sealed class FakeWorkspaceManager : IExecutionWorkspaceManager
    {
        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(
            Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionWorkspaceResult("/workspace/path", "devpilot/branch", true, BaseCommitSha: "base123"));

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(
            string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceVerificationResult(true, true, true, true));
    }

    private sealed class FakeImpactRepo : IImpactAnalysisRepository
    {
        public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TaskImpactAnalysis?>(new TaskImpactAnalysis
            {
                Id = Guid.NewGuid(),
                DevelopmentTaskId = taskId,
                Status = ImpactAnalysisStatus.Completed,
                StructuredResult = new ImpactAnalysisResultData
                {
                    ImpactedFiles = new List<ImpactedFile>
                    {
                        new() { FilePath = "src/A.cs", ChangeType = ImpactFileChangeType.Modify },
                        new() { FilePath = "src/B.cs", ChangeType = ImpactFileChangeType.Modify },
                        new() { FilePath = "src/App.cs", ChangeType = ImpactFileChangeType.Modify },
                    },
                },
            });

        public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> StartAnalysisAtomicAsync(TaskImpactAnalysis analysis, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasActiveAnalysisForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleAnalysesAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class FakeFreshness : IRepositoryFreshnessService
    {
        private readonly RepositoryFreshnessResult? _result;

        public FakeFreshness(RepositoryFreshnessResult? result) => _result = result;

        public bool Throw { get; set; }
        public List<RepositoryFreshnessRequest> Requests { get; } = new();

        public Task<RepositoryFreshnessResult> RefreshAsync(RepositoryFreshnessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(_result!);
        }
    }

    private sealed class FakeAgent : IDeveloperAgent
    {
        private readonly IReadOnlyList<string> _generatedFiles;

        public FakeAgent(params string[] generatedFiles) => _generatedFiles = generatedFiles;

        public List<FocusedRepairRequest> RepairRequests { get; } = new();

        public Task<DeveloperAgentResult> GenerateAndApplyEditsAsync(DeveloperAgentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(DeveloperAgentResult.Ok(_generatedFiles.ToList(), model: "test-model"));

        public Task<DeveloperAgentResult> ExecuteFocusedRepairAsync(FocusedRepairRequest request, CancellationToken cancellationToken = default)
        {
            RepairRequests.Add(request);
            return Task.FromResult(DeveloperAgentResult.Ok(request.RepairFiles.ToList(), model: "test-model"));
        }
    }

    private sealed class FakeRecorder : IExecutionActivityRecorder
    {
        public List<(ExecutionStage Stage, ExecutionActivityStatus Status, string Message, ExecutionActivityMetadata? Metadata)> Activities { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Activities.Add((stage, status, message, metadata));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBaseline : IBaselineVerificationService
    {
        private readonly BaselineFailureComparison? _response;

        public FakeBaseline(BaselineFailureComparison? response = null) => _response = response;

        private BaselineFailureComparison Response(string key, string testName, string location) =>
            _response ?? new BaselineFailureComparison(
                BaselineFailureClassification.NewRegression,
                0,
                1,
                0,
                Array.Empty<NormalizedFailureItem>(),
                new[] { new NormalizedFailureItem(key, testName, "Err", "Err", location) },
                Array.Empty<NormalizedFailureItem>(),
                "new regression");

        public Task<BaselineFailureComparison> EvaluateTestFailureAsync(string workspacePath, string sourceRepositoryPath, string baseCommitSha, RepositoryCheck check, RepositoryCheckResult taskCheckResult, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response("DevPilot.Tests.AppTests.TestMethod1", "DevPilot.Tests.AppTests.TestMethod1", "src/App.cs:20"));

        public Task<BaselineFailureComparison> EvaluateCompilerFailureAsync(string workspacePath, string sourceRepositoryPath, string baseCommitSha, RepositoryCheck check, RepositoryCheckResult taskCheckResult, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response("CS0103:src/A.cs:L10", null!, "src/A.cs:10"));
    }

    private sealed class ConstantFingerprintCalculator : IExecutionChangeFingerprintCalculator
    {
        private readonly string _fingerprint;

        public ConstantFingerprintCalculator(string fingerprint) => _fingerprint = fingerprint;

        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionFingerprintResult(true, Fingerprint: _fingerprint));

        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(string workspacePath, string treeSha, string baseHeadSha, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionFingerprintResult(true, Fingerprint: treeSha, BaseHeadSha: baseHeadSha));
    }

    private sealed class ScriptedCheckRunner : IRepositoryCheckRunner
    {
        private readonly Queue<RepositoryCheckResult> _buildResults;
        private readonly Func<RepositoryCheckExecutionRequest, RepositoryCheckResult> _testResult;
        private RepositoryCheckResult _lastBuildResult = Pass();

        public ScriptedCheckRunner(
            IEnumerable<RepositoryCheckResult> buildResults,
            Func<RepositoryCheckExecutionRequest, RepositoryCheckResult> testResult)
        {
            _buildResults = new Queue<RepositoryCheckResult>(buildResults);
            _testResult = testResult;
        }

        public List<RepositoryCheckExecutionRequest> BuildRequests { get; } = new();
        public List<RepositoryCheckExecutionRequest> TestRequests { get; } = new();

        public Task<RepositoryProfile> DiscoverAsync(RepositoryPreflightRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryProfile(
                RepositoryVerificationState.Configured,
                new[] { "dotnet" },
                new[] { BuildCheck, TestCheck }));

        public Task<RepositoryCheckResult> ExecuteAsync(RepositoryCheckExecutionRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Check.Kind == RepositoryCheckKind.Build)
            {
                BuildRequests.Add(request);
                // Once the script is exhausted, the last scripted result repeats (so "always failing" is easy to express).
                if (_buildResults.Count > 0)
                {
                    _lastBuildResult = _buildResults.Dequeue();
                }

                return Task.FromResult(_lastBuildResult);
            }

            TestRequests.Add(request);
            return Task.FromResult(_testResult(request));
        }
    }
}
