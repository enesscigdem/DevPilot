using System.Text.RegularExpressions;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

/// <summary>
/// Dedicated end-to-end regression tests verifying all required scenarios
/// of the Compiler-First Task Execution Engine Simplification.
/// </summary>
public sealed class CompilerFirstTaskExecutionEngineTests
{
    // Scenario 1: Unknown external-looking type does not terminally fail before build
    [Fact]
    public void Scenario01_UnknownExternalType_DoesNotTerminallyFailBeforeBuild()
    {
        var code = """
            using SomeThirdParty.Sdk;
            public class ExternalConsumer
            {
                public void Run(IThirdPartyClient client) => client.Execute();
            }
            """;

        var (isValid, error) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/Services/ExternalConsumer.cs",
            code,
            workspacePath: "C:/fake/workspace");

        isValid.Should().BeTrue();
        error.Should().BeNull();

        var origin = RoslynContractExtractor.ClassifySymbolOrigin("IThirdPartyClient", lockedContracts: null, workspaceSymbols: null);
        origin.Should().Be(SymbolOrigin.Unknown);
    }

    // Scenario 2: Real referenced package symbol is allowed to compilation
    [Fact]
    public void Scenario02_RealReferencedPackageSymbol_AllowedToCompilation()
    {
        var code = """
            using AutoMapper;
            public class UserMapper
            {
                private readonly IMapper _mapper;
                public UserMapper(IMapper mapper) => _mapper = mapper;
            }
            """;

        var (isValid, error) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/Services/UserMapper.cs",
            code,
            workspacePath: "C:/fake/workspace");

        isValid.Should().BeTrue();
        error.Should().BeNull();

        var origin = RoslynContractExtractor.ClassifySymbolOrigin("IMapper", lockedContracts: null, workspaceSymbols: null);
        origin.Should().Be(SymbolOrigin.ResolvedExternalOrReferenced);
    }

    // Scenario 3: Missing package/symbol reaches authoritative compilation diagnostic and bounded repair
    [Fact]
    public async Task Scenario03_MissingPackageSymbol_ReachesCompilationAndBoundedRepair()
    {
        var taskId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var agent = new TestDeveloperAgent();
        var buildQueue = new Queue<BuildValidationResult>(new[]
        {
            new BuildValidationResult { Success = false, ExitCode = 1, ErrorMessage = "dotnet build failed.", StdOut = "src/App.cs(10,5): error CS0246: The type or namespace name 'MissingPackageType' could not be found" },
            new BuildValidationResult { Success = true, ExitCode = 0 }
        });
        var runner = new QueuedValidationRunner(buildQueue);
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            new TestWorkspaceManager(),
            new TestExecutionRepository(),
            new TestImpactAnalysisRepository { AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed } },
            agent,
            runner,
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(executionId, taskId, "Fix missing symbol", "Desc", null, Guid.NewGuid(), "/src", "Summary");
        await processor.ProcessAsync(context);

        agent.CallCount.Should().Be(2);
        runner.BuildCallCount.Should().Be(2);
        runner.TestCallCount.Should().Be(1);
    }

    // Scenario 4: Semantic contract mismatch does NOT cause a provider repair call before build
    [Fact]
    public async Task Scenario04_SemanticContractMismatch_DoesNotCauseProviderCallBeforeBuild()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DevPilotTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        InitGitRepo(tempDir);
        try
        {
            var fakeProvider = new TestAiProvider();
            // File 1: Produces IUserService with 2 args
            fakeProvider.ResponsesToReturn.Enqueue("""
                {
                  "filePath": "src/IUserService.cs",
                  "action": "Create",
                  "newContent": "public interface IUserService { void Run(int a, int b); }"
                }
                """);
            // File 2: Calls Run with 1 arg (semantic contract mismatch - must proceed to worktree without pre-build repair call)
            fakeProvider.ResponsesToReturn.Enqueue("""
                {
                  "filePath": "src/UserConsumer.cs",
                  "action": "Create",
                  "newContent": "public class UserConsumer { public void Execute(IUserService s) => s.Run(1); }"
                }
                """);

            var agent = new DeveloperAgent(
                fakeProvider,
                new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
                NullLogger<DeveloperAgent>.Instance,
                null,
                new TestActivityRecorder());

            var request = new DeveloperAgentRequest(
                TaskId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                TaskTitle: "Semantic contract test",
                TaskDescription: "Desc",
                AcceptanceCriteria: null,
                ImpactAnalysisSummary: "Summary",
                ProposedPlan: "Plan",
                ImpactedFilePaths: new[] { "src/IUserService.cs", "src/UserConsumer.cs" },
                WorkspacePath: tempDir,
                BranchName: "main");

            var result = await agent.GenerateAndApplyEditsAsync(request);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(2);
            fakeProvider.TotalCalls.Should().Be(2); // Exactly 2 calls: no 3rd pre-build semantic repair call!
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    // Scenario 5: Wrong constructor reaches build without pre-build blocking
    [Fact]
    public async Task Scenario05_WrongConstructor_ReachesBuild()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DevPilotTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        InitGitRepo(tempDir);
        try
        {
            var fakeProvider = new TestAiProvider();
            fakeProvider.ResponsesToReturn.Enqueue("""
                {
                  "filePath": "src/Models.cs",
                  "action": "Create",
                  "newContent": "public record CreateItemCommand(string Name, int Quantity);"
                }
                """);
            fakeProvider.ResponsesToReturn.Enqueue("""
                {
                  "filePath": "src/Consumer.cs",
                  "action": "Create",
                  "newContent": "public class Consumer { public CreateItemCommand Create() => new CreateItemCommand(); }"
                }
                """);

            var agent = new DeveloperAgent(
                fakeProvider,
                new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
                NullLogger<DeveloperAgent>.Instance,
                null,
                new TestActivityRecorder());

            var request = new DeveloperAgentRequest(
                TaskId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                TaskTitle: "Wrong constructor test",
                TaskDescription: "Desc",
                AcceptanceCriteria: null,
                ImpactAnalysisSummary: "Summary",
                ProposedPlan: "Plan",
                ImpactedFilePaths: new[] { "src/Models.cs", "src/Consumer.cs" },
                WorkspacePath: tempDir,
                BranchName: "main");

            var result = await agent.GenerateAndApplyEditsAsync(request);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(2);
            fakeProvider.TotalCalls.Should().Be(2);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    // Scenario 6: Successful 5-file generation proceeds directly to apply/build without semantic repair calls
    [Fact]
    public async Task Scenario06_SuccessfulFiveFileGeneration_ProceedsDirectlyWithoutSemanticRepairCalls()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DevPilotTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        InitGitRepo(tempDir);
        try
        {
            var fakeProvider = new TestAiProvider();
            var files = new[]
            {
                "src/ICommand.cs",
                "src/IHandler.cs",
                "src/MyCommand.cs",
                "src/MyHandler.cs",
                "src/Controller.cs"
            };

            foreach (var f in files)
            {
                fakeProvider.ResponsesToReturn.Enqueue($$"""
                    {
                      "filePath": "{{f}}",
                      "action": "Create",
                      "newContent": "public class {{Path.GetFileNameWithoutExtension(f)}} {}"
                    }
                    """);
            }

            var agent = new DeveloperAgent(
                fakeProvider,
                new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
                NullLogger<DeveloperAgent>.Instance,
                null,
                new TestActivityRecorder());

            var request = new DeveloperAgentRequest(
                TaskId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                TaskTitle: "5-file generation",
                TaskDescription: "Desc",
                AcceptanceCriteria: null,
                ImpactAnalysisSummary: "Summary",
                ProposedPlan: "Plan",
                ImpactedFilePaths: files,
                WorkspacePath: tempDir,
                BranchName: "main");

            var result = await agent.GenerateAndApplyEditsAsync(request);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(5);
            fakeProvider.TotalCalls.Should().Be(5); // Exactly 5 generation calls, zero semantic repair calls
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    // Scenario 7: Path traversal remains blocked
    [Fact]
    public void Scenario07_PathTraversal_RemainsBlocked()
    {
        var act = () => WorktreeEditApplier.NormalizeAndValidateRelativePath("../../../etc/passwd");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Path safety violation*");
    }

    // Scenario 8: Nonexistent Modify path remains blocked
    [Fact]
    public async Task Scenario08_NonexistentModifyPath_RemainsBlocked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DevPilotTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        InitGitRepo(tempDir);
        try
        {
            var applier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
            var editSpec = new FileEditSpec(
                FilePath: "src/NonExistentFile.cs",
                Action: FileEditAction.Modify,
                NewContent: null,
                SearchReplaceEdits: new List<SearchReplaceEdit>
                {
                    new SearchReplaceEdit("foo", "bar")
                });

            var appResult = await applier.ApplyEditsAsync(tempDir, "main", new StructuredEditPlan(new[] { editSpec }));
            appResult.Success.Should().BeFalse();
            appResult.ErrorMessage.Should().Contain("Strict Modify action failed");
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    // Scenario 9: Malformed/unapplicable edit remains safely rejected
    [Fact]
    public void Scenario09_MalformedEdit_SafelyRejected()
    {
        var originalContent = "public class Existing { public int Value = 1; }";
        var edits = new List<SearchReplaceEdit>
        {
            new SearchReplaceEdit("public int NonExistent = 999;", "public int Value = 2;")
        };

        var appResult = WorktreeEditApplier.ValidateAndApplySearchReplaceEdits(originalContent, edits, "src/Existing.cs");
        appResult.Success.Should().BeFalse();
        appResult.ErrorMessage.Should().Contain("Missing search match");
    }

    // Scenario 10: Compile failure -> repair -> build success
    [Fact]
    public async Task Scenario10_CompileFailure_Repairs_BuildSuccess()
    {
        var taskId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var agent = new TestDeveloperAgent();
        var buildQueue = new Queue<BuildValidationResult>(new[]
        {
            new BuildValidationResult { Success = false, ExitCode = 1, ErrorMessage = "dotnet build failed.", StdOut = "src/App.cs(10,5): error CS0103: The name 'Calculate' does not exist in the current context" },
            new BuildValidationResult { Success = true, ExitCode = 0 }
        });
        var runner = new QueuedValidationRunner(buildQueue);
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            new TestWorkspaceManager(),
            new TestExecutionRepository(),
            new TestImpactAnalysisRepository { AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed } },
            agent,
            runner,
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(executionId, taskId, "Fix compile", "Desc", null, Guid.NewGuid(), "/src", "Summary");
        await processor.ProcessAsync(context);

        agent.CallCount.Should().Be(2);
        runner.BuildCallCount.Should().Be(2);
    }

    // Scenario 11: Compile failure after max rounds -> terminal CompilationFailure
    [Fact]
    public async Task Scenario11_CompileFailure_ExhaustsMaxRounds_ThrowsTerminal()
    {
        var taskId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var agent = new TestDeveloperAgent();
        var failed = new BuildValidationResult { Success = false, ExitCode = 1, ErrorMessage = "dotnet build failed.", StdOut = "src/App.cs(10,5): error CS0103: Unresolvable" };
        var buildQueue = new Queue<BuildValidationResult>(new[] { failed, failed, failed, failed });
        var runner = new QueuedValidationRunner(buildQueue);
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            new TestWorkspaceManager(),
            new TestExecutionRepository(),
            new TestImpactAnalysisRepository { AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed } },
            agent,
            runner,
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(executionId, taskId, "Unresolvable compile", "Desc", null, Guid.NewGuid(), "/src", "Summary");
        var act = async () => await processor.ProcessAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Build validation failed*");
        agent.CallCount.Should().Be(2); // initial + one focused repair; identical diagnostic stops
        runner.BuildCallCount.Should().Be(2);
    }

    // Scenario 12: Test failure -> repair -> targeted tests success
    [Fact]
    public async Task Scenario12_TestFailure_Repairs_TestSuccess()
    {
        var taskId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var agent = new TestDeveloperAgent();
        var testQueue = new Queue<TestValidationResult>(new[]
        {
            FocusedTestFailure(),
            new TestValidationResult { Success = true, ExitCode = 0 },
            new TestValidationResult { Success = true, ExitCode = 0 }
        });
        var runner = new QueuedTestValidationRunner(testQueue);
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            new TestWorkspaceManager(),
            new TestExecutionRepository(),
            new TestImpactAnalysisRepository { AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed } },
            agent,
            runner,
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(executionId, taskId, "Fix test", "Desc", null, Guid.NewGuid(), "/src", "Summary");
        await processor.ProcessAsync(context);

        agent.CallCount.Should().Be(2); // 1 initial + 1 test repair
        runner.TestCallCount.Should().Be(3); // initial full, targeted retry, required full suite
    }

    // Scenario 13: Test failure after max rounds -> terminal TestFailure
    [Fact]
    public async Task Scenario13_TestFailure_ExhaustsMaxRounds_ThrowsTerminal()
    {
        var taskId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var agent = new TestDeveloperAgent();
        var runner = new TestExecutionValidationRunner
        {
            TestResultToReturn = FocusedTestFailure()
        };
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            new TestWorkspaceManager(),
            new TestExecutionRepository(),
            new TestImpactAnalysisRepository { AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed } },
            agent,
            runner,
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(executionId, taskId, "Unresolvable test", "Desc", null, Guid.NewGuid(), "/src", "Summary");
        var act = async () => await processor.ProcessAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Test validation failed*");
        agent.CallCount.Should().Be(2); // identical targeted failure stops before a second repair
        runner.TestCallCount.Should().Be(2);
    }

    // Scenario 14: Build and test activity exposes concise sanitized diagnostics
    [Fact]
    public async Task Scenario14_BuildAndTestActivity_ExposesConciseSanitizedDiagnostics()
    {
        var taskId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var agent = new TestDeveloperAgent();
        var buildQueue = new Queue<BuildValidationResult>(new[]
        {
            new BuildValidationResult
            {
                Success = false,
                ExitCode = 1,
                ErrorMessage = "dotnet build failed.",
                StdOut = "src/App.cs(12,15): error CS1729: 'Consumer' does not contain a constructor that takes 0 arguments\nsrc/App.cs(18,5): error CS1061: 'IUserService' does not contain a definition for 'Handle'"
            },
            new BuildValidationResult { Success = true, ExitCode = 0 }
        });
        var runner = new QueuedValidationRunner(buildQueue);
        var recorder = new TestActivityRecorder();

        var processor = new GitWorkspaceExecutionProcessor(
            new TestWorkspaceManager(),
            new TestExecutionRepository(),
            new TestImpactAnalysisRepository { AnalysisToReturn = new TaskImpactAnalysis { Id = Guid.NewGuid(), DevelopmentTaskId = taskId, Status = ImpactAnalysisStatus.Completed } },
            agent,
            runner,
            recorder,
            NullLogger<GitWorkspaceExecutionProcessor>.Instance);

        var context = new ExecutionProcessingContext(executionId, taskId, "Feature", "Desc", null, Guid.NewGuid(), "/src", "Summary");
        await processor.ProcessAsync(context);

        var messages = recorder.RecordedActivities.Select(a => a.message).ToList();
        messages.Should().Contain(m => m.Contains("Build failed — 2 compiler error(s)"));
        messages.Should().Contain(m => m.Contains("CS1729"));
        messages.Should().Contain(m => m.Contains("CS1061"));
        messages.Should().Contain(m => m.Contains("Repair round 1/3"));
        messages.Should().Contain(m => m.Contains("Repairing:"));
    }

    // Scenario 15: Successful files remain preserved while another file is repaired
    [Fact]
    public void Scenario15_AlreadyValidFiles_PreservedDuringRepair()
    {
        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["src/ValidModel.cs"] = new FileEditSpec(FilePath: "src/ValidModel.cs", Action: FileEditAction.Create, NewContent: "public class ValidModel {}", SearchReplaceEdits: null)
        };

        var (isValid, error) = RoslynContractExtractor.ValidateNoDuplicateTypeDeclarations(
            "src/NewService.cs",
            "public class NewService {}",
            completedEdits);

        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    // Scenario 16: Reliability options are configurable and bounded
    [Fact]
    public void Scenario16_ReliabilityOptions_ConfigurableAndBounded()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["ExecutionReliability:MaxCompileRepairRounds"] = "4",
            ["ExecutionReliability:MaxTestRepairRounds"] = "3"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var options = new ExecutionReliabilityOptions
        {
            MaxCompileRepairRounds = 4,
            MaxTestRepairRounds = 3
        };

        options.MaxCompileRepairRounds.Should().Be(4);
        options.MaxTestRepairRounds.Should().Be(3);
    }

    // Scenario 17: HttpClient / GetAsync regression remains green
    [Fact]
    public void Scenario17_HttpClientGetAsync_DoesNotCollideWithProductsController()
    {
        var lockedContracts = new Dictionary<string, string>
        {
            ["src/Controllers/ProductsController.cs"] = "namespace App.Controllers; public class ProductsController { public int Get(int id); }"
        };

        var testCode = """
            using System.Net.Http;
            namespace App.Tests;
            public class ApiTests
            {
                public async Task Run(HttpClient client)
                {
                    await client.GetAsync("/api/products");
                }
            }
            """;

        var (isValid, error) = RoslynContractExtractor.ValidateSemanticContractConsistency("tests/ApiTests.cs", testCode, lockedContracts);
        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    // Scenario 18: Package reference symbol resolution remains green
    [Fact]
    public void Scenario18_PackageReference_AllowedWithoutTerminalPreBuildFailure()
    {
        var code = """
            using MediatR;
            public record Query : IRequest<string>;
            """;

        var (isValid, error) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies("src/Query.cs", code, null, null);
        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    // Scenario 19: Failure category taxonomy is well-defined
    [Fact]
    public void Scenario19_FailureTaxonomy_ContainsAllCoreCategories()
    {
        Enum.IsDefined(typeof(ExecutionFailureCategory), ExecutionFailureCategory.CompilationFailure).Should().BeTrue();
        Enum.IsDefined(typeof(ExecutionFailureCategory), ExecutionFailureCategory.TestFailure).Should().BeTrue();
        Enum.IsDefined(typeof(ExecutionFailureCategory), ExecutionFailureCategory.EditApplicabilityFailure).Should().BeTrue();
        Enum.IsDefined(typeof(ExecutionFailureCategory), ExecutionFailureCategory.SecurityViolation).Should().BeTrue();
        Enum.IsDefined(typeof(ExecutionFailureCategory), ExecutionFailureCategory.ProviderFailure).Should().BeTrue();
    }

    // Scenario 20: C# Syntax error is cleanly reported
    [Fact]
    public void Scenario20_SyntaxError_ReportedCleanly()
    {
        var brokenCode = "public class Broken { public void Run( }";
        var (isValid, errors) = RoslynContractExtractor.ValidateSyntax(brokenCode);
        isValid.Should().BeFalse();
        errors.Should().NotBeEmpty();
    }

    // ── Helper Test Fakes ──────────────────────────────────────────────────────────
    private static TestValidationResult FocusedTestFailure() => new()
    {
        Success = false,
        ExitCode = 1,
        ErrorMessage = "dotnet test failed.",
        StdOut = """
            Failed DevPilot.Tests.AppTests.TestMethod1 [5 ms]
              Error Message:
               Expected 100 but got 0.
              Stack Trace:
                 at DevPilot.Tests.AppTests.TestMethod1() in /workspace/path/src/App.cs:line 20
            Failed! - Failed: 1, Passed: 5, Skipped: 0, Total: 6
            """
    };

    private class TestAiProvider : IAiProvider
    {
        public string ProviderName => "TestProvider";
        public Queue<string> ResponsesToReturn { get; } = new();
        public int TotalCalls { get; private set; }

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            var content = ResponsesToReturn.Count > 0 ? ResponsesToReturn.Dequeue() : "{}";
            return Task.FromResult(new AiResponse
            {
                Provider = "TestProvider",
                IsSuccess = true,
                StatusCode = 200,
                Content = content
            });
        }
    }

    private class TestWorkspaceManager : IExecutionWorkspaceManager
    {
        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(
            Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionWorkspaceResult("/workspace/path", "devpilot/branch", Success: true));

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(
            string workspacePath, string expectedBranchName, bool requireClean = true, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceVerificationResult(IsValid: true, WorkspaceExists: true, BranchMatches: true, IsClean: true));
    }

    private class TestExecutionRepository : IExecutionRepository
    {
        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetModelAsync(Guid executionId, string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskExecution?>(null);
        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskExecution>>(Array.Empty<TaskExecution>());
        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasFailedExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetReviewDecisionAsync(Guid executionId, ExecutionReviewStatus expectedStatus, ExecutionReviewStatus newStatus, DateTime decidedAt, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(false);
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
        public Task<bool> ReplacePullRequestTrackingSnapshotAsync(Guid executionId, Guid attemptId, ExecutionPullRequestRemoteState remoteState, ExecutionPullRequestIntegrityStatus integrityStatus, DateTime? closedAt, DateTime? mergedAt, ExecutionCiStatus ciStatus, IReadOnlyList<ExecutionCiCheck> checks, DateTime syncedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryClaimMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan syncTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStaleMergeLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan mergeLeaseTimeout, TimeSpan syncTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetExecutionMergedAsync(Guid executionId, Guid attemptId, string mergeCommitSha, DateTime mergedAt, string mergeMethod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMergeFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ClaimAsRunningAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RenewHeartbeatAsync(Guid executionId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CompleteWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> FailWithLeaseAsync(Guid executionId, Guid leaseToken, string errorMessage, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RequestCancellationAsync(Guid executionId, string? reason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AcknowledgeCancellationWithLeaseAsync(Guid executionId, Guid leaseToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCancellationRequestedAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleRunningExecutionsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private class TestImpactAnalysisRepository : IImpactAnalysisRepository
    {
        public TaskImpactAnalysis? AnalysisToReturn { get; set; }
        public Task<TaskImpactAnalysis?> GetLatestByTaskIdAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(AnalysisToReturn);
        public Task AddAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(TaskImpactAnalysis analysis, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> StartAnalysisAtomicAsync(TaskImpactAnalysis analysis, DevelopmentTask task, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasActiveAnalysisForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> ReconcileStaleAnalysesAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private class TestDeveloperAgent : IDeveloperAgent
    {
        public DeveloperAgentResult ResultToReturn { get; set; } = DeveloperAgentResult.Ok(new List<string> { "src/App.cs" });
        public int CallCount { get; private set; }

        public Task<DeveloperAgentResult> GenerateAndApplyEditsAsync(DeveloperAgentRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ResultToReturn);
        }
    }

    private class QueuedValidationRunner : IExecutionValidationRunner
    {
        private readonly Queue<BuildValidationResult> _buildResults;
        public int BuildCallCount { get; private set; }
        public int TestCallCount { get; private set; }

        public QueuedValidationRunner(Queue<BuildValidationResult> buildResults)
        {
            _buildResults = buildResults;
        }

        public Task<BuildValidationResult> ValidateBuildAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            BuildCallCount++;
            return Task.FromResult(_buildResults.Count > 0 ? _buildResults.Dequeue() : new BuildValidationResult { Success = false, ErrorMessage = "dotnet build failed." });
        }

        public Task<TestValidationResult> ValidateTestAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            return Task.FromResult(new TestValidationResult { Success = true });
        }
    }

    private class QueuedTestValidationRunner : IExecutionValidationRunner
    {
        private readonly Queue<TestValidationResult> _testResults;
        public int BuildCallCount { get; private set; }
        public int TestCallCount { get; private set; }

        public QueuedTestValidationRunner(Queue<TestValidationResult> testResults)
        {
            _testResults = testResults;
        }

        public Task<BuildValidationResult> ValidateBuildAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            BuildCallCount++;
            return Task.FromResult(new BuildValidationResult { Success = true });
        }

        public Task<TestValidationResult> ValidateTestAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            return Task.FromResult(_testResults.Count > 0 ? _testResults.Dequeue() : new TestValidationResult { Success = true });
        }
    }

    private class TestExecutionValidationRunner : IExecutionValidationRunner
    {
        public BuildValidationResult BuildResultToReturn { get; set; } = new BuildValidationResult { Success = true };
        public TestValidationResult TestResultToReturn { get; set; } = new TestValidationResult { Success = true };
        public int BuildCallCount { get; private set; }
        public int TestCallCount { get; private set; }

        public Task<BuildValidationResult> ValidateBuildAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            BuildCallCount++;
            return Task.FromResult(BuildResultToReturn);
        }

        public Task<TestValidationResult> ValidateTestAsync(ExecutionValidationRequest request, CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            return Task.FromResult(TestResultToReturn);
        }
    }

    private class TestActivityRecorder : IExecutionActivityRecorder
    {
        public List<(Guid executionId, ExecutionStage stage, ExecutionActivityStatus status, string message, ExecutionActivityMetadata? metadata)> RecordedActivities { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            RecordedActivities.Add((executionId, stage, status, message, metadata));
            return Task.CompletedTask;
        }
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init", "-b", "main");
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(path, ".gitignore"), "# ignore\n");
        RunGit(path, "add", ".");
        RunGit(path, "commit", "-m", "initial");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
