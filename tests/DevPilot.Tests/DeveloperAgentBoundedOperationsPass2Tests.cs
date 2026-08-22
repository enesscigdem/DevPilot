using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class DeveloperAgentBoundedOperationsPass2Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly DeveloperAgent _developerAgent;

    public DeveloperAgentBoundedOperationsPass2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotPass2Tests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/pass2-test-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        InitGitRepo(_originalRepoDir);

        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Original Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");

        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _fakeAiProvider = new FakeAiProvider();
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _developerAgent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance);
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
            // Ignore cleanup errors in temporary directory
        }
    }

    private DeveloperAgentRequest CreateRequest(
        string relativePath,
        string action = "Modify",
        string taskTitle = "Pass 2 Task",
        string taskDesc = "Pass 2 Description",
        string? acceptanceCriteria = "Acceptance criteria") =>
        new(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: taskTitle,
            TaskDescription: taskDesc,
            AcceptanceCriteria: acceptanceCriteria,
            ImpactAnalysisSummary: $"Impacts {relativePath}",
            ProposedPlan: $"Apply {action} to {relativePath}",
            ImpactedFilePaths: new[] { relativePath },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(relativePath, action, taskDesc) });

    [Fact]
    public async Task Modify_MediumProductionFile_RoutesToBoundedOperationsByDefault()
    {
        // 50 lines (medium production file > 25 lines)
        var relativePath = "InvoiceService.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var lines = Enumerable.Range(1, 50).Select(i => $"    // line {i}").ToList();
        var content = "namespace Services;\n\npublic class InvoiceService\n{\n" +
                      string.Join("\n", lines) +
                      "\n    public decimal CalculateTax(decimal amount) => amount * 0.18m;\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "InvoiceService.cs",
              "operations": [
                {
                  "type": "replace",
                  "oldText": "public decimal CalculateTax(decimal amount) => amount * 0.18m;",
                  "newText": "public decimal CalculateTax(decimal amount) => amount * 0.20m;"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        var req = _fakeAiProvider.ReceivedRequests[0];
        req.SystemPrompt.Should().Contain("bounded operations");
        req.SystemPrompt.Should().Contain("operations");
        req.SystemPrompt.Should().NotContain("Return the complete resulting file once in 'newContent'");
        req.MaxTokens.Should().BeInRange(2048, 4096);

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("amount * 0.20m");
    }

    [Fact]
    public async Task Modify_TestFile_RoutesToBoundedOperationsWithNarrowContext()
    {
        // Repetitive test file with 100 lines
        var relativePath = "TodoServiceTests.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var testMethods = Enumerable.Range(1, 10)
            .Select(i => $"    [Fact]\n    public void Test_{i}() => Assert.True(true);")
            .ToList();
        var content = "using Xunit;\n\npublic class TodoServiceTests\n{\n" +
                      string.Join("\n\n", testMethods) +
                      "\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "TodoServiceTests.cs",
              "operations": [
                {
                  "type": "insertBefore",
                  "anchor": "    [Fact]\n    public void Test_1()",
                  "content": "    [Fact]\n    public void Test_NewFeature() => Assert.Equal(42, 42);\n\n"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        var req = _fakeAiProvider.ReceivedRequests[0];
        req.SystemPrompt.Should().Contain("MINIMAL TESTS");
        req.SystemPrompt.Should().Contain("bounded operations");
        req.MaxTokens.Should().BeInRange(2048, 4096);

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("Test_NewFeature");
        updated.Should().Contain("Test_1");
        updated.Should().Contain("Test_10");
    }

    [Fact]
    public async Task Create_PreservesFullContentGeneration()
    {
        var relativePath = "UserDto.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "UserDto.cs",
              "action": "Create",
              "newContent": "namespace Models;\n\npublic record UserDto(Guid Id, string Name);\n"
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath, action: "Create"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        var req = _fakeAiProvider.ReceivedRequests[0];
        req.SystemPrompt.Should().Contain("creating a new file");
        req.SystemPrompt.Should().Contain("newContent");

        var created = await File.ReadAllTextAsync(fullPath);
        created.Should().Contain("public record UserDto");
    }

    [Fact]
    public async Task Modify_TinyFile_PreservesSafeFullFileReplacement()
    {
        var relativePath = "Status.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        // 5 lines < 25 lines
        var content = "namespace Enums;\n\npublic enum Status\n{\n    Pending,\n    Completed\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Status.cs",
              "action": "Modify",
              "newContent": "namespace Enums;\n\npublic enum Status\n{\n    Pending,\n    InProgress,\n    Completed\n}\n"
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        var req = _fakeAiProvider.ReceivedRequests[0];
        req.SystemPrompt.Should().Contain("small-file Modify");
        req.SystemPrompt.Should().Contain("newContent");

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("InProgress");
    }

    [Fact]
    public async Task BoundedOperations_AllFourOperationTypes_ApplyDeterministically()
    {
        var relativePath = "CalculatorService.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var lines = Enumerable.Range(1, 30).Select(i => $"    // padding {i}").ToList();
        var content = "namespace Services;\n\npublic class CalculatorService\n{\n" +
                      string.Join("\n", lines) + "\n" +
                      "    public int ObsoleteMethod() => 0;\n" +
                      "    public int Add(int a, int b) => a - b; // bug\n" +
                      "    public int Multiply(int a, int b) => a * b;\n" +
                      "}\n";
        await File.WriteAllTextAsync(fullPath, content);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "CalculatorService.cs",
              "operations": [
                {
                  "type": "delete",
                  "oldText": "    public int ObsoleteMethod() => 0;\n"
                },
                {
                  "type": "replace",
                  "oldText": "public int Add(int a, int b) => a - b; // bug",
                  "newText": "public int Add(int a, int b) => a + b;"
                },
                {
                  "type": "insertBefore",
                  "anchor": "    public int Multiply",
                  "content": "    public int Subtract(int a, int b) => a - b;\n\n"
                },
                {
                  "type": "insertAfter",
                  "anchor": "    public int Multiply(int a, int b) => a * b;",
                  "content": "\n    public double Divide(int a, int b) => (double)a / b;"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().NotContain("ObsoleteMethod");
        updated.Should().Contain("public int Add(int a, int b) => a + b;");
        updated.Should().Contain("public int Subtract(int a, int b) => a - b;");
        updated.Should().Contain("public double Divide(int a, int b) => (double)a / b;");
    }

    [Fact]
    public async Task AnchorMismatch_TriggersSingleBoundedRecovery_WithFreshHashAndEvidence_AndSucceeds()
    {
        var relativePath = "PaymentService.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var lines = Enumerable.Range(1, 30).Select(i => $"    // setup line {i}").ToList();
        var content = "namespace Services;\n\npublic class PaymentService\n{\n" +
                      string.Join("\n", lines) + "\n" +
                      "    public bool Charge(string card) => true;\n" +
                      "}\n";
        await File.WriteAllTextAsync(fullPath, content);

        // Attempt 1: Anchor text does not exist in target
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "PaymentService.cs",
              "operations": [
                {
                  "type": "replace",
                  "oldText": "public bool NonExistentChargeMethod() => false;",
                  "newText": "public bool Charge(string card) => true;"
                }
              ]
            }
            """);

        // Attempt 2 (Bounded Repair): Corrected anchor
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "PaymentService.cs",
              "operations": [
                {
                  "type": "replace",
                  "oldText": "public bool Charge(string card) => true;",
                  "newText": "public bool Charge(string card, decimal amount) => amount > 0;"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);

        // Verify repair request included typed failure evidence
        var repairReq = _fakeAiProvider.ReceivedRequests[1];
        repairReq.UserPrompt.Should().Contain("AnchorNotFound");
        repairReq.UserPrompt.Should().Contain("NonExistentChargeMethod");
        repairReq.UserPrompt.Should().Contain("Expected File Hash");

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("decimal amount");
    }

    [Fact]
    public async Task HardModifyTokenCeiling_NeverExceeds8192Tokens()
    {
        var relativePath = "HugeService.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        // 500 lines
        var lines = Enumerable.Range(1, 500).Select(i => $"    public int Method_{i}() => {i};").ToList();
        var content = "namespace Services;\n\npublic class HugeService\n{\n" +
                      string.Join("\n", lines) +
                      "\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "HugeService.cs",
              "operations": [
                {
                  "type": "replace",
                  "oldText": "public int Method_1() => 1;",
                  "newText": "public int Method_1() => 100;"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().BeLessThanOrEqualTo(8192);
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().BeInRange(2048, 4096);
    }

    [Fact]
    public async Task AcceptanceScenario_LargeTestFile_GeneratesCompactPayloadWithoutDuplicatingFile()
    {
        var relativePath = "TodosControllerTests.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        // Simulate real 120-line test file
        var testBlocks = Enumerable.Range(1, 15).Select(i => $$"""
                [Fact]
                public async Task GetTodoById_{{i}}_ReturnsExpectedResult()
                {
                    // Arrange
                    var id = Guid.NewGuid();
                    // Act
                    var response = await _controller.GetById(id);
                    // Assert
                    response.Should().NotBeNull();
                }
            """).ToList();

        var originalContent = "using System;\nusing System.Threading.Tasks;\nusing Xunit;\nusing FluentAssertions;\n\n" +
                              "namespace DevPilot.Tests.Controllers;\n\n" +
                              "public class TodosControllerTests\n{\n" +
                              string.Join("\n\n", testBlocks) +
                              "\n}\n";
        await File.WriteAllTextAsync(fullPath, originalContent);

        var operationPayload = """
            {
              "filePath": "TodosControllerTests.cs",
              "operations": [
                {
                  "type": "insertAfter",
                  "anchor": "public class TodosControllerTests\n{",
                  "content": "\n    [Fact]\n    public async Task CreateTodo_WithValidTitle_ReturnsCreated()\n    {\n        var result = await _controller.Create(new(\"New Task\"));\n        result.Should().NotBeNull();\n    }\n"
                }
              ]
            }
            """;

        _fakeAiProvider.ResponsesToReturn.Enqueue(operationPayload);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(
            relativePath,
            taskTitle: "Add CreateTodo test",
            taskDesc: "Add focused test method for CreateTodo",
            acceptanceCriteria: "CreateTodo test passes"));

        result.Success.Should().BeTrue(result.ErrorMessage);

        // Generated payload size is tiny compared to full file
        operationPayload.Length.Should().BeLessThan(originalContent.Length / 4);

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("CreateTodo_WithValidTitle_ReturnsCreated");
        updated.Should().Contain("GetTodoById_1_ReturnsExpectedResult");
        updated.Should().Contain("GetTodoById_15_ReturnsExpectedResult");
    }

    [Fact]
    public async Task LegacySearchReplacePayload_AcceptedForCompatibility()
    {
        var relativePath = "LegacyService.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var lines = Enumerable.Range(1, 30).Select(i => $"    // line {i}").ToList();
        var content = "namespace Services;\n\npublic class LegacyService\n{\n" +
                      string.Join("\n", lines) +
                      "\n    public int LegacyValue => 1;\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "LegacyService.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "public int LegacyValue => 1;",
                  "replace": "public int LegacyValue => 99;"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("LegacyValue => 99");
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
}
