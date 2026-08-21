using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

            // Simulate pre-existing test failure on baseline run matching task failure
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
