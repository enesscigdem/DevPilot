using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.Executions;
using DevPilot.Infrastructure.RepositoryClone;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevPilot.Tests.Executions;

public class LazyBaselineVerificationTests
{
    private readonly RepositoryCheck _testCheck = new(
        Id: "dotnet:test:unit",
        DisplayName: ".NET Test (Unit)",
        Kind: RepositoryCheckKind.Test,
        Ecosystem: "dotnet",
        Executable: "dotnet",
        Arguments: new[] { "test", "--no-build" },
        WorkingDirectory: "",
        Required: true,
        Timeout: TimeSpan.FromMinutes(2),
        Source: RepositoryCheckSource.DotNetManifest,
        EvidencePath: "Test.csproj",
        SupportsSkipBuild: true,
        SupportsTargetedTest: true);

    private readonly RepositoryCheck _buildCheck = new(
        Id: "dotnet:build",
        DisplayName: ".NET Build",
        Kind: RepositoryCheckKind.Build,
        Ecosystem: "dotnet",
        Executable: "dotnet",
        Arguments: new[] { "build" },
        WorkingDirectory: "",
        Required: true,
        Timeout: TimeSpan.FromMinutes(2),
        Source: RepositoryCheckSource.DotNetManifest,
        EvidencePath: "App.csproj");

    [Fact]
    public void CompareFailureSets_CleanBasePasses_PostChangeFails_ClassifiesAsNewRegression()
    {
        var postChangeFailures = new List<NormalizedFailureItem>
        {
            new("TestSuite.Tests.FeatureXTest", "TestSuite.Tests.FeatureXTest", "Assert.Equal() Failure", "Assert.Equal() Failure", "TestSuite/FeatureXTest.cs:42")
        };
        var baselineFailures = Array.Empty<NormalizedFailureItem>();

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            postChangeFailures,
            baselineFailures,
            baselineCheckSucceeded: true);

        comparison.Classification.Should().Be(BaselineFailureClassification.NewRegression);
        comparison.NewRegressionCount.Should().Be(1);
        comparison.PreExistingCount.Should().Be(0);
        comparison.NewRegressions.Should().ContainSingle(f => f.FailureKey == "TestSuite.Tests.FeatureXTest");
    }

    [Fact]
    public void CompareFailureSets_CleanBaseHasSameFailures_ClassifiesAsPreExisting()
    {
        var postChangeFailures = new List<NormalizedFailureItem>
        {
            new("TestSuite.Tests.PreExistingTest", "TestSuite.Tests.PreExistingTest", "Assert.True() Failure", "Assert.True() Failure", "TestSuite/PreExistingTest.cs:10")
        };
        var baselineFailures = new List<NormalizedFailureItem>
        {
            new("TestSuite.Tests.PreExistingTest", "TestSuite.Tests.PreExistingTest", "Assert.True() Failure", "Assert.True() Failure", "TestSuite/PreExistingTest.cs:10")
        };

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            postChangeFailures,
            baselineFailures,
            baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.PreExisting);
        comparison.NewRegressionCount.Should().Be(0);
        comparison.PreExistingCount.Should().Be(1);
        comparison.PreExistingFailures.Should().ContainSingle(f => f.FailureKey == "TestSuite.Tests.PreExistingTest");
    }

    [Fact]
    public void CompareFailureSets_MultiFailureSet_BaselineABC_PostChangeABCD_ClassifiesDAsNewRegression()
    {
        var baselineFailures = new List<NormalizedFailureItem>
        {
            new("Suite.TestA", "Suite.TestA", "Failed assertion", "Failed assertion", "Suite/TestA.cs:1"),
            new("Suite.TestB", "Suite.TestB", "NullReferenceException", "NullReferenceException", "Suite/TestB.cs:2"),
            new("Suite.TestC", "Suite.TestC", "Timeout", "Timeout", "Suite/TestC.cs:3")
        };

        var postChangeFailures = new List<NormalizedFailureItem>
        {
            new("Suite.TestA", "Suite.TestA", "Failed assertion", "Failed assertion", "Suite/TestA.cs:1"),
            new("Suite.TestB", "Suite.TestB", "NullReferenceException", "NullReferenceException", "Suite/TestB.cs:2"),
            new("Suite.TestC", "Suite.TestC", "Timeout", "Timeout", "Suite/TestC.cs:3"),
            new("Suite.TestD", "Suite.TestD", "Expected 200 got 500", "Expected 200 got 500", "Suite/TestD.cs:4")
        };

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            postChangeFailures,
            baselineFailures,
            baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.NewRegression);
        comparison.PreExistingCount.Should().Be(3);
        comparison.NewRegressionCount.Should().Be(1);
        comparison.PreExistingFailures.Select(f => f.FailureKey).Should().BeEquivalentTo(new[] { "Suite.TestA", "Suite.TestB", "Suite.TestC" });
        comparison.NewRegressions.Select(f => f.FailureKey).Should().BeEquivalentTo(new[] { "Suite.TestD" });
    }

    [Fact]
    public void CompareFailureSets_CompilerDiagnostics_IdenticalDiagnostics_ClassifiesAsPreExisting()
    {
        var baselineFailures = new List<NormalizedFailureItem>
        {
            new("CS0103:OldFile.cs:L20", null, "CS0103: The name 'xyz' does not exist in the current context", "CS0103: The name 'xyz' does not exist in the current context", "OldFile.cs:20")
        };

        var postChangeFailures = new List<NormalizedFailureItem>
        {
            new("CS0103:OldFile.cs:L20", null, "CS0103: The name 'xyz' does not exist in the current context", "CS0103: The name 'xyz' does not exist in the current context", "OldFile.cs:20")
        };

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            postChangeFailures,
            baselineFailures,
            baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.PreExisting);
        comparison.PreExistingCount.Should().Be(1);
        comparison.NewRegressionCount.Should().Be(0);
    }

    [Fact]
    public async Task BaselineVerificationService_ConcurrentRequestsForSameKey_ExecutesOnlyOnceDueToDeduplication()
    {
        var coordinator = new BaselineVerificationCoordinator(NullLogger<BaselineVerificationCoordinator>.Instance);
        var checkRunner = new MockRepositoryCheckRunner();
        var processRunner = new MockProcessRunner();

        var taskFailure = new RepositoryCheckResult
        {
            Success = false,
            ExitCode = 1,
            StdOut = "Failed TestSuite.UnitTests.ExistingFailingTest [12ms]\n  Error Message:\n   Assert.Equal() Failure",
            StdErr = "",
            FailureCategory = RepositoryCheckFailureCategory.VerificationFailure
        };

        // Run 5 concurrent evaluations with same base commit and check across separate scoped service instances
        var tasks = Enumerable.Range(0, 5).Select(_ =>
        {
            var service = new BaselineVerificationService(
                coordinator,
                checkRunner,
                processRunner,
                NullLogger<BaselineVerificationService>.Instance);

            return service.EvaluateTestFailureAsync(
                workspacePath: "C:/workspaces/task-1",
                sourceRepositoryPath: "C:/repos/my-repo",
                baseCommitSha: "abc1234567890abcdef1234567890abcdef1234",
                check: _testCheck,
                taskCheckResult: taskFailure);
        });

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(5);
        results.All(r => r.Classification == BaselineFailureClassification.PreExisting).Should().BeTrue();
        // The check runner should only have been invoked ONCE across the 5 concurrent callers!
        checkRunner.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task BaselineVerificationService_TargetedTestProbe_PassesTestFilterToBaselineCheck()
    {
        var coordinator = new BaselineVerificationCoordinator(NullLogger<BaselineVerificationCoordinator>.Instance);
        var checkRunner = new MockRepositoryCheckRunner();
        var processRunner = new MockProcessRunner();
        var service = new BaselineVerificationService(
            coordinator,
            checkRunner,
            processRunner,
            NullLogger<BaselineVerificationService>.Instance);

        var taskFailure = new RepositoryCheckResult
        {
            Success = false,
            ExitCode = 1,
            StdOut = "Failed TestSuite.SpecificTests.FailingTestCase [12ms]\n  Error Message:\n   Assert.Equal() Failure",
            StdErr = "",
            FailureCategory = RepositoryCheckFailureCategory.VerificationFailure
        };

        var comparison = await service.EvaluateTestFailureAsync(
            workspacePath: "C:/workspaces/task-1",
            sourceRepositoryPath: "C:/repos/my-repo",
            baseCommitSha: "deadbeef1234567890abcdef1234567890abcdef",
            check: _testCheck,
            taskCheckResult: taskFailure);

        comparison.Classification.Should().Be(BaselineFailureClassification.PreExisting);
        checkRunner.LastExecutedRequest.Should().NotBeNull();
        checkRunner.LastExecutedRequest!.TestFilter.Should().Be("TestSuite.SpecificTests.FailingTestCase");
    }

    [Fact]
    public void ParseAllTestFailures_ExtractsMultipleFailingTests()
    {
        var stdout = @"
Starting test execution, please wait...
A total of 1 test files matched the specified pattern.
[xUnit.net 00:00:01.23]     TestProject.Tests.FirstTest [FAIL]
  Failed TestProject.Tests.FirstTest [15 ms]
  Error Message:
   Assert.Equal() Failure
   Expected: 1
   Actual:   2
  Stack Trace:
     at TestProject.Tests.FirstTest() in C:\app\TestProject\FirstTest.cs:line 25

  Failed TestProject.Tests.SecondTest [8 ms]
  Error Message:
   System.InvalidOperationException : Null ref
  Stack Trace:
     at TestProject.Tests.SecondTest() in C:\app\TestProject\SecondTest.cs:line 50

Failed!  - Failed:     2, Passed:    10, Skipped:     0, Total:    12, Duration: 120 ms
";

        var failures = ExecutionDiagnosticEvidence.ParseAllTestFailures(stdout, null, null);

        failures.Should().HaveCount(2);
        failures[0].TestName.Should().Be("TestProject.Tests.FirstTest");
        failures[0].Location.Should().Contain("TestProject/FirstTest.cs");
        failures[1].TestName.Should().Be("TestProject.Tests.SecondTest");
        failures[1].Location.Should().Contain("TestProject/SecondTest.cs");
    }

    [Fact]
    public void ParseAllCompilerFailures_ExtractsMultipleDiagnosticErrors()
    {
        var stderr = @"
C:\app\Services\OrderService.cs(45,12): error CS0103: The name 'total' does not exist in the current context [C:\app\App.csproj]
C:\app\Controllers\OrderController.cs(10,5): error CS0246: The type or namespace name 'OrderDto' could not be found [C:\app\App.csproj]
";

        var failures = ExecutionDiagnosticEvidence.ParseAllCompilerFailures(null, stderr, null);

        failures.Should().HaveCount(2);
        failures[0].NormalizedDiagnostic.Should().Contain("CS0103");
        failures[0].Location.Should().Contain("Services/OrderService.cs:45");
        failures[1].NormalizedDiagnostic.Should().Contain("CS0246");
        failures[1].Location.Should().Contain("Controllers/OrderController.cs:10");
    }

    [Fact]
    public async Task PassingExecution_DoesZeroBaselineWork()
    {
        var baselineMock = new FakeBaselineService();
        var workspaceManager = new FakeWorkspaceManager();
        var executionRepo = new InMemoryExecutionRepository();
        var impactRepo = new FakeImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis
            {
                Status = ImpactAnalysisStatus.Completed,
                StructuredResult = new ImpactAnalysisResultData
                {
                    ImpactedFiles = new List<ImpactedFile> { new() { FilePath = "src/File1.cs", ChangeType = ImpactFileChangeType.Modify } }
                }
            }
        };
        var devAgent = new FakeDevAgent();
        var checkRunner = new FakePassingCheckRunner(_buildCheck, _testCheck);
        var activityRecorder = new FakeActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            devAgent,
            checkRunner,
            activityRecorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance,
            baselineVerificationService: baselineMock);

        var context = new ExecutionProcessingContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Title",
            "Desc",
            null,
            Guid.NewGuid(),
            "C:/repos/test",
            "Summary");

        await processor.ProcessAsync(context, CancellationToken.None);

        // ZERO baseline calls on passing path!
        baselineMock.CompilerCalls.Should().Be(0);
        baselineMock.TestCalls.Should().Be(0);
    }

    [Fact]
    public async Task PreExistingTestFailure_ClassifiedPreExisting_ZeroAiRepair()
    {
        var baselineMock = new FakeBaselineService
        {
            TestResponse = new BaselineFailureComparison(
                Classification: BaselineFailureClassification.PreExisting,
                PreExistingCount: 1,
                NewRegressionCount: 0,
                ChangedCount: 0,
                PreExistingFailures: new[] { new NormalizedFailureItem("TestA", "TestA", "Fail", "Fail") },
                NewRegressions: Array.Empty<NormalizedFailureItem>(),
                ChangedFailures: Array.Empty<NormalizedFailureItem>(),
                Summary: "No new regressions: 1 pre-existing failure remains.")
        };
        var workspaceManager = new FakeWorkspaceManager();
        var executionRepo = new InMemoryExecutionRepository();
        var impactRepo = new FakeImpactAnalysisRepository
        {
            AnalysisToReturn = new TaskImpactAnalysis
            {
                Status = ImpactAnalysisStatus.Completed,
                StructuredResult = new ImpactAnalysisResultData
                {
                    ImpactedFiles = new List<ImpactedFile> { new() { FilePath = "src/File1.cs", ChangeType = ImpactFileChangeType.Modify } }
                }
            }
        };
        var devAgent = new FakeDevAgent();
        var checkRunner = new FakeFailingTestCheckRunner(_buildCheck, _testCheck);
        var activityRecorder = new FakeActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            workspaceManager,
            executionRepo,
            impactRepo,
            devAgent,
            checkRunner,
            activityRecorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance,
            baselineVerificationService: baselineMock);

        var context = new ExecutionProcessingContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Title",
            "Desc",
            null,
            Guid.NewGuid(),
            "C:/repos/test",
            "Summary");

        await processor.ProcessAsync(context, CancellationToken.None);

        // DeveloperAgent should have been called ONLY ONCE for initial generation, ZERO repair calls!
        devAgent.GenerateCalls.Should().Be(1);
    }

    [Fact]
    public void BaselineInfrastructureFailure_ClassifiesAsUnknown_NotNewRegression()
    {
        var taskFailures = new List<NormalizedFailureItem>
        {
            new("TaskFailure1", "Test1", "Failure", "Failure")
        };

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures,
            Array.Empty<NormalizedFailureItem>(),
            baselineCheckSucceeded: false,
            baselineIsInconclusive: true);

        comparison.Classification.Should().Be(BaselineFailureClassification.Unknown);
        comparison.NewRegressionCount.Should().Be(0);
        comparison.NewRegressions.Should().BeEmpty();
        comparison.PreExistingCount.Should().Be(0);
    }

    [Fact]
    public async Task BaselineCheckExecution_UsesDetachedHeadBranchName()
    {
        var checkRunner = new MockRepositoryCheckRunner();
        var coordinator = new BaselineVerificationCoordinator(NullLogger<BaselineVerificationCoordinator>.Instance);
        var processRunner = new MockProcessRunner();
        var service = new BaselineVerificationService(
            coordinator,
            checkRunner,
            processRunner,
            NullLogger<BaselineVerificationService>.Instance);

        var taskResult = new RepositoryCheckResult
        {
            Success = false,
            ExitCode = 1,
            StdOut = "Failed TestSuite.UnitTests.ExistingFailingTest\n Error Message:\n Assert.Equal() Failure"
        };

        var comparison = await service.EvaluateTestFailureAsync(
            workspacePath: "C:/tmp/executions/abc",
            sourceRepositoryPath: "C:/tmp/repo",
            baseCommitSha: "abc1234567890",
            check: _testCheck,
            taskCheckResult: taskResult);

        checkRunner.ExecuteCount.Should().Be(1);
        checkRunner.LastExecutedRequest.Should().NotBeNull();
        checkRunner.LastExecutedRequest!.BranchName.Should().Be("HEAD");
        comparison.Classification.Should().Be(BaselineFailureClassification.PreExisting);
    }

    [Fact]
    public async Task WorkspaceVerification_EmptyBranch_FailsValidation()
    {
        var manager = new GitExecutionWorkspaceManager(
            Options.Create(new RepositoryCloneOptions()),
            NullLogger<GitExecutionWorkspaceManager>.Instance);

        var result = await manager.VerifyWorkspaceStateAsync("C:/tmp/repo", expectedBranchName: "");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Expected branch name is empty.");
    }

    [Fact]
    public async Task EvaluateTestFailureAsync_BaselineInfrastructureFailure_ReturnsUnknown()
    {
        var checkRunner = new InfrastructureFailingCheckRunner();
        var coordinator = new BaselineVerificationCoordinator(NullLogger<BaselineVerificationCoordinator>.Instance);
        var processRunner = new MockProcessRunner();
        var service = new BaselineVerificationService(
            coordinator,
            checkRunner,
            processRunner,
            NullLogger<BaselineVerificationService>.Instance);

        var taskResult = new RepositoryCheckResult
        {
            Success = false,
            ExitCode = 1,
            StdOut = "Failed TestSuite.UnitTests.SomeTest"
        };

        var comparison = await service.EvaluateTestFailureAsync(
            workspacePath: "C:/tmp/executions/abc",
            sourceRepositoryPath: "C:/tmp/repo",
            baseCommitSha: "abc1234567890",
            check: _testCheck,
            taskCheckResult: taskResult);

        comparison.Classification.Should().Be(BaselineFailureClassification.Unknown);
    }

    private sealed class InfrastructureFailingCheckRunner : IRepositoryCheckRunner
    {
        public Task<RepositoryProfile> DiscoverAsync(RepositoryPreflightRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RepositoryProfile(RepositoryVerificationState.Configured, new[] { "dotnet" }, Array.Empty<RepositoryCheck>()));

        public Task<RepositoryCheckResult> ExecuteAsync(RepositoryCheckExecutionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RepositoryCheckResult
            {
                Success = false,
                ExitCode = 128,
                ErrorMessage = "Docker daemon is not running.",
                FailureCategory = RepositoryCheckFailureCategory.InfrastructureFailure
            });
    }

    private sealed class FakeBaselineService : IBaselineVerificationService
    {
        public int CompilerCalls { get; set; }
        public int TestCalls { get; set; }
        public BaselineFailureComparison? TestResponse { get; set; }
        public BaselineFailureComparison? CompilerResponse { get; set; }

        public Task<BaselineFailureComparison> EvaluateTestFailureAsync(string workspacePath, string sourceRepositoryPath, string baseCommitSha, RepositoryCheck check, RepositoryCheckResult taskCheckResult, CancellationToken cancellationToken = default)
        {
            TestCalls++;
            return Task.FromResult(TestResponse ?? new BaselineFailureComparison(BaselineFailureClassification.NewRegression, 0, 1, 0, Array.Empty<NormalizedFailureItem>(), new[] { new NormalizedFailureItem("Key", "Test", "Err", "Err") }, Array.Empty<NormalizedFailureItem>(), "Summary"));
        }

        public Task<BaselineFailureComparison> EvaluateCompilerFailureAsync(string workspacePath, string sourceRepositoryPath, string baseCommitSha, RepositoryCheck check, RepositoryCheckResult taskCheckResult, CancellationToken cancellationToken = default)
        {
            CompilerCalls++;
            return Task.FromResult(CompilerResponse ?? new BaselineFailureComparison(BaselineFailureClassification.NewRegression, 0, 1, 0, Array.Empty<NormalizedFailureItem>(), new[] { new NormalizedFailureItem("Key", null, "Err", "Err") }, Array.Empty<NormalizedFailureItem>(), "Summary"));
        }
    }

    private sealed class FakeWorkspaceManager : IExecutionWorkspaceManager
    {
        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExecutionWorkspaceResult("C:/ws/1", "devpilot/task-1", true, BaseCommitSha: "base123"));
        }

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkspaceVerificationResult(true, true, true, true));
        }
    }

    private sealed class FakeDevAgent : IDeveloperAgent
    {
        public int GenerateCalls { get; set; }
        public int RepairCalls { get; set; }
        public Task<DeveloperAgentResult> GenerateAndApplyEditsAsync(DeveloperAgentRequest request, CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            return Task.FromResult(DeveloperAgentResult.Ok(new[] { "src/File1.cs" }, model: "test-model"));
        }

        public Task<DeveloperAgentResult> ExecuteFocusedRepairAsync(FocusedRepairRequest request, CancellationToken cancellationToken = default)
        {
            RepairCalls++;
            return Task.FromResult(DeveloperAgentResult.Ok(request.RepairFiles, model: "test-model"));
        }
    }

    private sealed class FakeActivityRecorder : IExecutionActivityRecorder
    {
        public Task RecordActivityAsync(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePassingCheckRunner : IRepositoryCheckRunner
    {
        private readonly RepositoryCheck[] _checks;
        public FakePassingCheckRunner(params RepositoryCheck[] checks) => _checks = checks;

        public Task<RepositoryProfile> DiscoverAsync(RepositoryPreflightRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RepositoryProfile(RepositoryVerificationState.Configured, new[] { "dotnet" }, _checks));
        }

        public Task<RepositoryCheckResult> ExecuteAsync(RepositoryCheckExecutionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RepositoryCheckResult { Success = true, ExitCode = 0 });
        }
    }

    private sealed class FakeFailingTestCheckRunner : IRepositoryCheckRunner
    {
        private readonly RepositoryCheck[] _checks;
        public FakeFailingTestCheckRunner(params RepositoryCheck[] checks) => _checks = checks;

        public Task<RepositoryProfile> DiscoverAsync(RepositoryPreflightRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RepositoryProfile(RepositoryVerificationState.Configured, new[] { "dotnet" }, _checks));
        }

        public Task<RepositoryCheckResult> ExecuteAsync(RepositoryCheckExecutionRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Check.Kind == RepositoryCheckKind.Build)
            {
                return Task.FromResult(new RepositoryCheckResult { Success = true, ExitCode = 0 });
            }

            return Task.FromResult(new RepositoryCheckResult
            {
                Success = false,
                ExitCode = 1,
                StdOut = "Failed TestA\n Error Message:\n Fail",
                FailureCategory = RepositoryCheckFailureCategory.VerificationFailure
            });
        }
    }

    private sealed class MockRepositoryCheckRunner : IRepositoryCheckRunner
    {
        public int ExecuteCount { get; private set; }
        public RepositoryCheckExecutionRequest? LastExecutedRequest { get; private set; }

        public Task<RepositoryProfile> DiscoverAsync(RepositoryPreflightRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RepositoryProfile(
                State: RepositoryVerificationState.Configured,
                Ecosystems: new[] { "dotnet" },
                Checks: Array.Empty<RepositoryCheck>()));
        }

        public Task<RepositoryCheckResult> ExecuteAsync(RepositoryCheckExecutionRequest request, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            LastExecutedRequest = request;

            var testName = request.TestFilter ?? "TestSuite.UnitTests.ExistingFailingTest";
            var stdout = $"Failed {testName} [12ms]\n  Error Message:\n   Assert.Equal() Failure";

            var result = new RepositoryCheckResult
            {
                Success = false,
                ExitCode = 1,
                StdOut = stdout,
                StdErr = "",
                FailureCategory = RepositoryCheckFailureCategory.VerificationFailure
            };

            return Task.FromResult(result);
        }
    }

    private sealed class MockProcessRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunProcessAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var result = new ProcessExecutionResult(
                ExitCode: 0,
                StdOut: "HEAD\n",
                StdErr: "",
                StartTime: DateTimeOffset.UtcNow,
                CompletionTime: DateTimeOffset.UtcNow,
                Duration: TimeSpan.FromMilliseconds(50),
                IsTimedOut: false,
                IsTruncated: false);

            return Task.FromResult(result);
        }
    }
}
