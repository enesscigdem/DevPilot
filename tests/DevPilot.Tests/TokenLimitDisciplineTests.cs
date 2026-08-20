using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class TokenLimitDisciplineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly DeveloperAgent _developerAgent;
    private readonly TestExecutionActivityRecorder _activityRecorder;

    public TokenLimitDisciplineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotTokenDisciplineTests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/token-discipline-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        InitGitRepo(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Original Repo");
        Directory.CreateDirectory(Path.Combine(_originalRepoDir, "src", "Contracts"));
        File.WriteAllText(Path.Combine(_originalRepoDir, "src", "Contracts", "Contracts.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Directory.CreateDirectory(Path.Combine(_originalRepoDir, "src", "Services"));
        File.WriteAllText(Path.Combine(_originalRepoDir, "src", "Services", "Services.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Directory.CreateDirectory(Path.Combine(_originalRepoDir, "tests", "DevPilot.Tests"));
        File.WriteAllText(Path.Combine(_originalRepoDir, "tests", "DevPilot.Tests", "DevPilot.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["DeveloperAgent:TransientRecoveryCooldownMs"] = "0",
            ["DeveloperAgent:MaxGenerationCalls"] = "15",
            ["DeveloperAgent:TokenBudgets:ModifyPatch"] = "6144",
            ["DeveloperAgent:MaxOutputTokens"] = "32768"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();

        _fakeAiProvider = new FakeAiProvider { ProviderName = "Kimi" };
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _activityRecorder = new TestExecutionActivityRecorder();
        _developerAgent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            config,
            _activityRecorder);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_originalRepoDir))
            {
                RunGit(_originalRepoDir, "worktree", "prune");
            }
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task SmokeTask_OneExistingTestFile_ConciseValidResponse_SucceedsOnFirstCallWithoutEscalation()
    {
        // Arrange: Exact shape of the one-file smoke test task
        var testFilePath = "tests/DevPilot.Tests/RepositoryWorkspaces/GetWorkspaceOverviewQueryHandlerTests.cs";
        var fullTestPath = Path.Combine(_worktreeDir, testFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTestPath)!);

        var existingTestContent = """
            using Xunit;
            using FluentAssertions;

            namespace DevPilot.Tests.RepositoryWorkspaces;

            public class GetWorkspaceOverviewQueryHandlerTests
            {
                [Fact]
                public void ExistingTest()
                {
                    true.Should().BeTrue();
                }
            }
            """;
        await File.WriteAllTextAsync(fullTestPath, existingTestContent);

        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            FinishReason = "stop",
            Content = $$"""
                {
                  "filePath": "{{testFilePath}}",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public class GetWorkspaceOverviewQueryHandlerTests\n{\n    [Fact]",
                      "replace": "public class GetWorkspaceOverviewQueryHandlerTests\n{\n    [Fact]\n    public void NewFocusedTest()\n    {\n        1.Should().Be(1);\n    }\n\n    [Fact]"
                    }
                  ]
                }
                """
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add regression test to GetWorkspaceOverviewQueryHandlerTests",
            TaskDescription: "Add one focused regression test method",
            AcceptanceCriteria: "NewFocusedTest passes",
            ImpactAnalysisSummary: $"Impacts {testFilePath}",
            ProposedPlan: "Add test method to existing file",
            ImpactedFilePaths: new[] { testFilePath },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(testFilePath, "Modify", "Add regression test") });

        // Act
        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        // Assert: Succeeds on FIRST call without escalation
        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ModifiedFiles.Should().ContainSingle().Which.Should().Be(testFilePath);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1, "Concise response must succeed on the first provider call without escalation");

        var modifiedContent = await File.ReadAllTextAsync(fullTestPath);
        modifiedContent.Should().Contain("NewFocusedTest");
        modifiedContent.Should().Contain("ExistingTest");
    }

    [Fact]
    public async Task TokenLimitExceeded_Attempt1Fails_CompactRetryAttemptedOnceAndSucceeds()
    {
        var targetFile = "src/Services/OrderProcessor.cs";
        var fullPath = Path.Combine(_worktreeDir, targetFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "public class OrderProcessor { public int Process() => 1; }");

        // Attempt 1: Token limit exceeded
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result."
        });

        // Attempt 2 (Compact Retry): Succeeds with concise JSON
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            FinishReason = "stop",
            Content = $$"""
                {
                  "filePath": "{{targetFile}}",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public int Process() => 1;",
                      "replace": "public int Process() => 2;"
                    }
                  ]
                }
                """
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update OrderProcessor",
            TaskDescription: "Change process return value to 2",
            AcceptanceCriteria: "Process() returns 2",
            ImpactAnalysisSummary: $"Impacts {targetFile}",
            ProposedPlan: "Update Process() method",
            ImpactedFilePaths: new[] { targetFile },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(targetFile, "Modify", "Update Process()") });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2, "Attempt 1 failed with token limit -> compact retry attempted once and succeeded");

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("public int Process() => 2;");
    }

    [Fact]
    public async Task TokenLimitExceeded_CompactRetryAlsoFails_FailsImmediatelyWithZeroDiskMutationAndZeroBuild()
    {
        var targetFile = "src/Services/OrderProcessor.cs";
        var fullPath = Path.Combine(_worktreeDir, targetFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        const string originalContent = "public class OrderProcessor { public int Process() => 1; }";
        await File.WriteAllTextAsync(fullPath, originalContent);

        // Attempt 1: Token limit exceeded
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result."
        });

        // Attempt 2 (Compact Retry): Also token limit exceeded
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result."
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update OrderProcessor",
            TaskDescription: "Change process return value to 2",
            AcceptanceCriteria: "Process() returns 2",
            ImpactAnalysisSummary: $"Impacts {targetFile}",
            ProposedPlan: "Update Process() method",
            ImpactedFilePaths: new[] { targetFile },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(targetFile, "Modify", "Update Process()") });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exhausted the configured output token limit");
        result.ErrorMessage.Should().Contain(targetFile);

        // Assert: EXACTLY 2 provider calls, NO 3rd call
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2, "Exactly 2 provider calls (1 initial + 1 compact retry), no 3rd call");

        // Assert: Zero disk mutation
        var onDisk = await File.ReadAllTextAsync(fullPath);
        onDisk.Should().Be(originalContent, "Disk content must remain completely untouched on failure");
    }

    [Fact]
    public async Task TokenLimitExceeded_DoesNotEnterTransient503TimeoutRecovery()
    {
        var targetFile = "src/Services/OrderProcessor.cs";
        var fullPath = Path.Combine(_worktreeDir, targetFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "public class OrderProcessor {}");

        // Both attempts return TokenLimitExceeded
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { targetFile },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(targetFile, "Modify", "Edit") });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        // If it had entered transient recovery, it would have made a 3rd call. It must NOT enter transient recovery.
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public async Task TokenLimitRetry_DoesNotRegenerateAlreadyCompletedEarlierFiles()
    {
        var file1 = "src/Contracts/IOrderService.cs";
        var file2 = "src/Services/OrderService.cs";
        var full1 = Path.Combine(_worktreeDir, file1);
        var full2 = Path.Combine(_worktreeDir, file2);
        Directory.CreateDirectory(Path.GetDirectoryName(full1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(full2)!);
        await File.WriteAllTextAsync(full1, "public interface IOrderService {}");
        await File.WriteAllTextAsync(full2, "public class OrderService : IOrderService {}");

        // File 1 (Interface - Layer 10): Success on 1st call
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            FinishReason = "stop",
            Content = $$"""
                {
                  "filePath": "{{file1}}",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public interface IOrderService {}",
                      "replace": "public interface IOrderService { void Execute(); }"
                    }
                  ]
                }
                """
        });

        // File 2 (Service - Layer 30): Call 1 hits token limit
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });

        // File 2: Compact retry succeeds
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            FinishReason = "stop",
            Content = $$"""
                {
                  "filePath": "{{file2}}",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public class OrderService : IOrderService {}",
                      "replace": "public class OrderService : IOrderService { public void Execute() {} }"
                    }
                  ]
                }
                """
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update Service Contract",
            TaskDescription: "Add Execute method",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { file1, file2 },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail(file1, "Modify", "Update interface"),
                new ImpactedFileDetail(file2, "Modify", "Update implementation")
            });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().HaveCount(2);

        // 1 call for file1 + 2 calls for file2 = 3 total calls. File 1 was NOT regenerated!
        _fakeAiProvider.SendAsyncCallCount.Should().Be(3);
        _fakeAiProvider.ReceivedRequests[0].UserPrompt.Should().Contain(file1);
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain(file2);
        _fakeAiProvider.ReceivedRequests[2].UserPrompt.Should().Contain(file2);
    }

    [Fact]
    public async Task CompactRetryPrompt_RemovesDemonstrablyRedundantContext_WhileRetainingTargetSourceAndContracts()
    {
        var targetFile = "src/Services/PaymentService.cs";
        var fullPath = Path.Combine(_worktreeDir, targetFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        const string targetContent = "public class PaymentService { public bool Pay() => false; }";
        await File.WriteAllTextAsync(fullPath, targetContent);

        var refPath = Path.Combine(_worktreeDir, "src/Services/ReferencePaymentService.cs");
        await File.WriteAllTextAsync(refPath, "public class ReferencePaymentService { public void Validate() { } }");

        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            FinishReason = "stop",
            Content = $$"""
                {
                  "filePath": "{{targetFile}}",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public bool Pay() => false;",
                      "replace": "public bool Pay() => true;"
                    }
                  ]
                }
                """
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix Payment Service",
            TaskDescription: "Payment should return true",
            AcceptanceCriteria: "Pay returns true",
            ImpactAnalysisSummary: "Comprehensive architectural analysis of the entire payment pipeline and billing subsystem",
            ProposedPlan: "Plan for PaymentService:\nStep 1: Inspect legacy payment gateway\nStep 2: Redesign payment contracts and interfaces\nStep 3: Analyze upstream transactions\nStep 4: Modify PaymentService.cs to return true\nStep 5: Add unit tests for payment edge cases\nStep 6: Update integration documentation\nStep 7: Verify database consistency",
            ImpactedFilePaths: new[] { targetFile },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(targetFile, "Modify", "Fix pay") });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.ReceivedRequests.Should().HaveCount(2);

        var initialPrompt = _fakeAiProvider.ReceivedRequests[0].UserPrompt;
        var compactPrompt = _fakeAiProvider.ReceivedRequests[1].UserPrompt;

        // Compact prompt must be smaller than initial prompt
        compactPrompt.Length.Should().BeLessThan(initialPrompt.Length, "Compact retry prompt must remove redundant context");

        // Compact prompt retains required elements
        compactPrompt.Should().Contain(targetContent, "Target source must be retained for safe Modify grounding");
        compactPrompt.Should().Contain("Fix Payment Service", "Task title must be retained");
        compactPrompt.Should().Contain("Pay returns true", "Acceptance criteria must be retained");
        compactPrompt.Should().Contain("COMPACT RETRY (TOKEN LIMIT DISCIPLINE)", "Must contain strict compact retry directive");

        // Compact prompt removes redundant elements
        compactPrompt.Should().NotContain("Step 1: Inspect payment architecture", "Broad proposed plan steps must be removed");
    }

    [Fact]
    public void OutputDiscipline_DuplicateIdenticalSearchBlocks_RejectedByValidator()
    {
        var spec = new FileEditSpec(
            "Service.cs",
            FileEditAction.Modify,
            null,
            new List<SearchReplaceEdit>
            {
                new("int a = 1;", "int a = 2;"),
                new("int a = 1;", "int a = 3;") // Duplicate search pattern
            });

        var entry = new ManifestFileEntry("Service.cs", FileEditAction.Modify);

        var act = () => DeveloperAgent.ValidateSingleFileEditSpec(spec, entry, "int a = 1;\nint a = 1;");
        act.Should().Throw<FormatException>().WithMessage("*duplicate SearchReplaceEdit blocks*");
    }

    [Fact]
    public void OutputDiscipline_ExcessiveFileReproduction_RejectedByValidator()
    {
        const string largeFileContent = """
            namespace MyNamespace;
            public class LargeService
            {
                public void MethodA() { }
                public void MethodB() { }
                public void MethodC() { }
                public void MethodD() { }
                public void MethodE() { }
                public void MethodF() { }
                public void MethodG() { }
                public void MethodH() { }
            }
            """;

        // Model disguised full file replacement as a single search block covering >90% of the file
        var spec = new FileEditSpec(
            "LargeService.cs",
            FileEditAction.Modify,
            null,
            new List<SearchReplaceEdit>
            {
                new(largeFileContent.Trim(), largeFileContent.Trim() + "\n// modified")
            });

        var entry = new ManifestFileEntry("LargeService.cs", FileEditAction.Modify);

        var act = () => DeveloperAgent.ValidateSingleFileEditSpec(spec, entry, largeFileContent);
        act.Should().Throw<FormatException>().WithMessage("*effectively reproduces the entire file*");
    }

    [Fact]
    public void OutputDiscipline_ConciseOneFileEditManifest_AcceptedByValidator()
    {
        const string targetContent = """
            public class MyService
            {
                public int GetValue() => 10;
            }
            """;

        var spec = new FileEditSpec(
            "MyService.cs",
            FileEditAction.Modify,
            null,
            new List<SearchReplaceEdit>
            {
                new("public int GetValue() => 10;", "public int GetValue() => 20;")
            });

        var entry = new ManifestFileEntry("MyService.cs", FileEditAction.Modify);

        var act = () => DeveloperAgent.ValidateSingleFileEditSpec(spec, entry, targetContent);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Telemetry_RecordsRequestedOutputBudgetAndFinishReason()
    {
        var targetFile = "src/Services/TelemetryTest.cs";
        var fullPath = Path.Combine(_worktreeDir, targetFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "public class TelemetryTest {}");

        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            FinishReason = "stop",
            OutputTokens = 150,
            InputTokens = 350,
            Content = $$"""
                {
                  "filePath": "{{targetFile}}",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public class TelemetryTest {}",
                      "replace": "public class TelemetryTest { int X = 1; }"
                    }
                  ]
                }
                """
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Telemetry test",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { targetFile },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(targetFile, "Modify", "Edit") });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().Be(6144, "Modify category budget must be requested");

        // Verify activities were safely recorded
        _activityRecorder.RecordedActivities.Should().NotBeEmpty();
        _activityRecorder.RecordedActivities.Should().Contain(a => a.Message.Contains("Generating edit"));
    }

    [Fact]
    public async Task CompactRetry_RetainsRequiredContractFromEarlierGeneratedFile_WhenDependentFileHitsTokenLimit()
    {
        // Arrange
        // File A: Contract export (Layer 10)
        var fileA = "src/Contracts/IUserProfileService.cs";
        // File B: Service implementation (Layer 30, name does NOT resemble UserProfile)
        var fileB = "src/Services/OrderService.cs";

        var fullA = Path.Combine(_worktreeDir, fileA);
        var fullB = Path.Combine(_worktreeDir, fileB);
        Directory.CreateDirectory(Path.GetDirectoryName(fullA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(fullB)!);

        await File.WriteAllTextAsync(fullA, "namespace Contracts;\npublic interface IUserProfileService {}");
        await File.WriteAllTextAsync(fullB, "namespace Services;\nusing Contracts;\npublic class OrderService {\n    public string Check(IUserProfileService profile) => \"ok\";\n}");

        // Call 1 (File A): Generated and locked in Wave 1
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            FinishReason = "stop",
            Content = $$"""
                {
                  "filePath": "{{fileA}}",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public interface IUserProfileService {}",
                      "replace": "public interface IUserProfileService { string GetRole(); }"
                    }
                  ]
                }
                """
        });

        // Call 2 (File B Attempt 1): Token limit exceeded
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });

        // Call 3 (File B Compact Retry): Succeeds
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = true,
            StatusCode = 200,
            FinishReason = "stop",
            Content = $$"""
                {
                  "filePath": "{{fileB}}",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "public string Check(IUserProfileService profile) => \"ok\";",
                      "replace": "public string Check(IUserProfileService profile) => profile.GetRole();"
                    }
                  ]
                }
                """
        });

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Integrate user profile into order service",
            TaskDescription: "Use IUserProfileService.GetRole() in OrderService.cs",
            AcceptanceCriteria: "OrderService.Check returns role",
            ImpactAnalysisSummary: "Impacts both files",
            ProposedPlan: "Step 1: Update contract\nStep 2: Update service",
            ImpactedFilePaths: new[] { fileA, fileB },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail(fileA, "Modify", "Update IUserProfileService"),
                new ImpactedFileDetail(fileB, "Modify", "Update OrderService")
            });

        // Act
        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().HaveCount(2);

        // Total 3 calls: File A (call 1), File B initial (call 2), File B compact retry (call 3)
        _fakeAiProvider.SendAsyncCallCount.Should().Be(3);

        // Prove: File B's compact retry (Call 3) received File A's locked contract
        var fileBCompactPrompt = _fakeAiProvider.ReceivedRequests[2].UserPrompt;
        fileBCompactPrompt.Should().Contain("COMPACT RETRY (TOKEN LIMIT DISCIPLINE)");
        fileBCompactPrompt.Should().Contain("IUserProfileService", "Required upstream contract from File A must be retained in File B's compact retry");
        fileBCompactPrompt.Should().Contain("GetRole", "Locked method signature must be present for grounding");
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test@example.com");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private sealed class TestExecutionActivityRecorder : IExecutionActivityRecorder
    {
        public List<(Guid ExecutionId, ExecutionStage Stage, ExecutionActivityStatus Status, string Message)> RecordedActivities { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            RecordedActivities.Add((executionId, stage, status, message));
            return Task.CompletedTask;
        }
    }
}
