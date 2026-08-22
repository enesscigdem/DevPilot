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
    public async Task Modify_TinyFile_RoutesToEchoFreeTargetIdBoundedOperations()
    {
        var relativePath = "ITodoService.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        // 7 lines
        var content = "namespace Services;\n\npublic interface ITodoService\n{\n    Task<TodoDto> GetByIdAsync(Guid id);\n    Task<TodoDto> CreateAsync(CreateTodoDto dto);\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "ITodoService.cs",
              "operations": [
                {
                  "type": "insertAfter",
                  "targetId": "T6",
                  "content": "    Task ClearAsync();\n"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        var req = _fakeAiProvider.ReceivedRequests[0];
        req.SystemPrompt.Should().Contain("targetId");
        req.SystemPrompt.Should().NotContain("oldText");
        req.UserPrompt.Should().Contain("[T1]");
        req.UserPrompt.Should().Contain("[T6]");

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("Task ClearAsync();");
    }

    [Fact]
    public async Task Schema_OrdinaryModify_ContainsNoOldTextOrAnchorEcho()
    {
        var relativePath = "TodoAuditLogger.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var content = "namespace Services;\n\npublic class TodoAuditLogger : ITodoAuditLogger\n{\n    private readonly List<string> _logs = new();\n    public void Log(string action, string details) { _logs.Add(action); }\n    public IReadOnlyList<string> GetLogs() => _logs;\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "TodoAuditLogger.cs",
              "operations": [
                {
                  "type": "replace",
                  "targetId": "T5",
                  "content": "    private readonly Lock _lock = new();\n    private readonly List<string> _logs = new();"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateRequest(relativePath));
        result.Success.Should().BeTrue(result.ErrorMessage);

        var req = _fakeAiProvider.ReceivedRequests[0];
        req.SystemPrompt.Should().NotContain("\"oldText\"");
        req.SystemPrompt.Should().NotContain("\"anchor\"");
        req.SystemPrompt.Should().Contain("\"targetId\"");
        req.SystemPrompt.Should().Contain("\"content\"");
    }

    [Fact]
    public void TargetResolution_AllFourOperationTypes_ApplyDeterministicallyWithTargetId()
    {
        var originalContent = "namespace Services;\n\npublic class CalculatorService\n{\n    public int ObsoleteMethod() => 0;\n    public int Add(int a, int b) => a - b;\n    public int Multiply(int a, int b) => a * b;\n}\n";

        // T1: namespace Services;
        // T2: empty line
        // T3: public class CalculatorService
        // T4: {
        // T5:     public int ObsoleteMethod() => 0;
        // T6:     public int Add(int a, int b) => a - b;
        // T7:     public int Multiply(int a, int b) => a * b;
        // T8: }

        var ops = new[]
        {
            BoundedEditOperation.DeleteTarget("T5"),
            BoundedEditOperation.ReplaceTarget("T6", "    public int Add(int a, int b) => a + b;"),
            BoundedEditOperation.InsertBeforeTarget("T7", "    public int Subtract(int a, int b) => a - b;\n"),
            BoundedEditOperation.InsertAfterTarget("T7", "\n    public double Divide(int a, int b) => (double)a / b;")
        };

        var result = WorktreeEditApplier.ValidateAndApplyBoundedOperations(
            originalContent,
            ops,
            "CalculatorService.cs");

        result.Success.Should().BeTrue(result.ErrorMessage);
        var modified = result.ModifiedContent!;
        modified.Should().NotContain("ObsoleteMethod");
        modified.Should().Contain("public int Add(int a, int b) => a + b;");
        modified.Should().Contain("public int Subtract(int a, int b) => a - b;");
        modified.Should().Contain("public double Divide(int a, int b) => (double)a / b;");
    }

    [Fact]
    public void TargetResolution_InvalidTargetId_RejectsEntireEdit()
    {
        var originalContent = "public class Foo\n{\n    public int Value => 1;\n}\n";

        var ops = new[]
        {
            BoundedEditOperation.ReplaceTarget("T999", "    public int Value => 2;")
        };

        var result = WorktreeEditApplier.ValidateAndApplyBoundedOperations(
            originalContent,
            ops,
            "Foo.cs");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.AnchorNotFound);
        result.ErrorMessage.Should().Contain("T999");
    }

    [Fact]
    public void TargetResolution_UnauthorizedOperationType_Rejects()
    {
        var originalContent = "public class Foo\n{\n    public int Value => 1;\n}\n";

        var context = WorktreeEditApplier.BuildBoundedEditContext("Foo.cs", originalContent);
        context.Targets.Should().NotBeEmpty();

        var op = new BoundedEditOperation
        {
            Type = (BoundedEditOperationType)999,
            TargetId = "T3",
            Content = "test"
        };

        var result = WorktreeEditApplier.ValidateAndApplyBoundedOperations(
            originalContent,
            new[] { op },
            "Foo.cs");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.UnauthorizedTarget);
    }

    [Fact]
    public void AtomicApply_MultipleTargetIdOperations_ApplyAtomicallyWithoutOffsetDrift()
    {
        var originalContent = "line 1\nline 2\nline 3\nline 4\nline 5\n";

        var ops = new[]
        {
            BoundedEditOperation.ReplaceTarget("T2", "modified line 2 (longer than before)"),
            BoundedEditOperation.InsertAfterTarget("T3", "inserted line between 3 and 4\n"),
            BoundedEditOperation.DeleteTarget("T5")
        };

        var result = WorktreeEditApplier.ValidateAndApplyBoundedOperations(
            originalContent,
            ops,
            "file.txt");

        result.Success.Should().BeTrue();
        var lines = result.ModifiedContent!.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().Contain("modified line 2 (longer than before)");
        lines.Should().Contain("inserted line between 3 and 4");
        lines.Should().NotContain("line 5");
        lines.Should().Contain("line 1");
        lines.Should().Contain("line 3");
        lines.Should().Contain("line 4");
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

    [Fact]
    public async Task ForensicInspection_RepresentativeFiles_AssembledPromptsAreFocusedAndBounded()
    {
        var auditLoggerLines = Enumerable.Range(1, 35).Select(i => $"    // log field padding {i}").ToList();
        var auditLoggerContent = "namespace Services;\n\npublic class TodoAuditLogger : ITodoAuditLogger\n{\n" +
                                 string.Join("\n", auditLoggerLines) + "\n" +
                                 "    private readonly List<string> _logs = new();\n" +
                                 "    public void Log(string action, string details) { _logs.Add($\"{action}: {details}\"); }\n" +
                                 "    public IReadOnlyList<string> GetLogs() => _logs;\n}\n";

        var files = new (string Path, string Action, string Content, string Purpose)[]
        {
            ("ITodoService.cs", "Modify", "namespace Services;\n\npublic interface ITodoService\n{\n    Task<TodoDto> GetByIdAsync(Guid id);\n    Task<TodoDto> CreateAsync(CreateTodoDto dto);\n}\n", "Add logging contract"),
            ("ITodoAuditLogger.cs", "Modify", "namespace Services;\n\npublic interface ITodoAuditLogger\n{\n    void Log(string action, string details);\n    IReadOnlyList<string> GetLogs();\n}\n", "Thread-safe audit logger interface"),
            ("TodoAuditLogger.cs", "Modify", auditLoggerContent, "Make audit logger thread safe using lock"),
            ("TodoService.cs", "Modify", "namespace Services;\n\npublic class TodoService : ITodoService\n{\n    private readonly ITodoAuditLogger _logger;\n    public TodoService(ITodoAuditLogger logger) { _logger = logger; }\n    public Task<TodoDto> GetByIdAsync(Guid id) => Task.FromResult(new TodoDto(id, \"Test\"));\n    public Task<TodoDto> CreateAsync(CreateTodoDto dto) { _logger.Log(\"Create\", dto.Title); return Task.FromResult(new TodoDto(Guid.NewGuid(), dto.Title)); }\n}\n", "Ensure thread-safety across service calls"),
            ("TodoServiceTests.cs", "Modify", "using Xunit;\n\nnamespace Tests;\n\npublic class TodoServiceTests\n{\n    [Fact]\n    public void GetById_ReturnsTodo() => Assert.True(true);\n}\n", "Add thread safety test for TodoService"),
            ("TodosControllerTests.cs", "Modify", "using Xunit;\n\nnamespace Tests;\n\npublic class TodosControllerTests\n{\n    [Fact]\n    public void Create_ReturnsOk() => Assert.True(true);\n    [Fact]\n    public void Get_ReturnsOk() => Assert.True(true);\n}\n", "Add test for concurrent audit logging")
        };

        foreach (var (path, action, content, purpose) in files)
        {
            var fullPath = Path.Combine(_worktreeDir, path);
            await File.WriteAllTextAsync(fullPath, content);
        }

        // Test prompt construction for TodoAuditLogger.cs
        var targetFile = "TodoAuditLogger.cs";
        var fileEntry = new ManifestFileEntry(targetFile, FileEditAction.Modify, "Add thread safety locking", null);
        var req = CreateRequest(targetFile, "Modify", "Singleton Servislerin Thread-Safety Acisindan Dogrulanmasi", "Ensure TodoAuditLogger is thread safe");

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "TodoAuditLogger.cs",
              "operations": [
                {
                  "type": "replace",
                  "oldText": "private readonly List<string> _logs = new();",
                  "newText": "private readonly Lock _lock = new();\n    private readonly List<string> _logs = new();"
                },
                {
                  "type": "replace",
                  "oldText": "public void Log(string action, string details) { _logs.Add($\"{action}: {details}\"); }",
                  "newText": "public void Log(string action, string details) { lock (_lock) { _logs.Add($\"{action}: {details}\"); } }"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(req);
        result.Success.Should().BeTrue(result.ErrorMessage);

        var capturedReq = _fakeAiProvider.ReceivedRequests.Last();

        // Assert forensic prompt bounds
        capturedReq.SystemPrompt.Should().Contain("bounded operations");
        capturedReq.SystemPrompt.Should().Contain("target IDs");
        capturedReq.SystemPrompt.Should().Contain("PREFER 1 OPERATION for contiguous changes");
        capturedReq.UserPrompt.Should().NotContain("=== Reference Architecture Pattern (MANDATORY) ==="); // Omitted for Modify
        capturedReq.UserPrompt.Should().Contain("=== Current Content of Target File ===");
        capturedReq.MaxTokens.Should().BeInRange(2048, 4096);
    }

    [Fact]
    public void BoundedOperationValidator_RejectsPseudoFullFileReplace_ForLocalizedEdit()
    {
        var originalContent = "namespace Services;\n\npublic class TodoAuditLogger\n{\n" +
                              "    private readonly List<string> _logs = new();\n" +
                              "    public void Log(string action, string details) { _logs.Add(action); }\n" +
                              "    public IReadOnlyList<string> GetLogs() => _logs;\n" +
                              "    public void Clear() => _logs.Clear();\n" +
                              "}\n";

        // Pseudo-full-file replace where oldText covers the entire class
        var pseudoFullFileOp = BoundedEditOperation.Replace(
            originalContent.Trim(),
            originalContent.Replace("private readonly List<string> _logs = new();", "private readonly Lock _lock = new();\n    private readonly List<string> _logs = new();"));

        var result = WorktreeEditApplier.ValidateAndApplyBoundedOperations(
            originalContent,
            new[] { pseudoFullFileOp },
            "TodoAuditLogger.cs");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.InvalidOperation);
        result.ErrorMessage.Should().Contain("effectively reproduces the entire file/class");
    }

    [Fact]
    public void BoundedOperationValidator_AcceptsLegitimateLocalizedEdit()
    {
        var originalContent = "namespace Services;\n\npublic class TodoAuditLogger\n{\n" +
                              "    private readonly List<string> _logs = new();\n" +
                              "    public void Log(string action, string details) { _logs.Add(action); }\n" +
                              "    public IReadOnlyList<string> GetLogs() => _logs;\n" +
                              "    public void Clear() => _logs.Clear();\n" +
                              "}\n";

        var localizedOp = BoundedEditOperation.Replace(
            "private readonly List<string> _logs = new();",
            "private readonly Lock _lock = new();\n    private readonly List<string> _logs = new();");

        var result = WorktreeEditApplier.ValidateAndApplyBoundedOperations(
            originalContent,
            new[] { localizedOp },
            "TodoAuditLogger.cs");

        result.Success.Should().BeTrue();
        result.ModifiedContent.Should().Contain("private readonly Lock _lock = new();");
    }

    [Fact]
    public async Task InitialModifyPrompt_IsMateriallyFocused_AndRecoveryIsMateriallySmaller()
    {
        var relativePath = "AuditLogger.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var lines = Enumerable.Range(1, 40).Select(i => $"    public void Method_{i}() {{ }}").ToList();
        var content = "namespace Services;\n\npublic class AuditLogger\n{\n" +
                      string.Join("\n", lines) +
                      "\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        var req = CreateRequest(relativePath, "Modify", "Refactor logger", "Improve audit logger locking");

        // Attempt 1: anchor not found to trigger recovery
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "AuditLogger.cs",
              "operations": [
                {
                  "type": "replace",
                  "oldText": "public void NonExistentMethod() { }",
                  "newText": "public void Method_1() { /* updated */ }"
                }
              ]
            }
            """);

        // Attempt 2: valid bounded edit
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "AuditLogger.cs",
              "operations": [
                {
                  "type": "replace",
                  "oldText": "public void Method_1() { }",
                  "newText": "public void Method_1() { /* locked */ }"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(req);
        result.Success.Should().BeTrue(result.ErrorMessage);

        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);

        var initialReq = _fakeAiProvider.ReceivedRequests[0];
        var recoveryReq = _fakeAiProvider.ReceivedRequests[1];

        // Initial prompt is focused and does not contain pattern bloat
        initialReq.UserPrompt.Should().NotContain("=== Reference Architecture Pattern (MANDATORY) ===");

        // Recovery prompt has typed failure evidence and fresh expected hash
        recoveryReq.UserPrompt.Should().Contain("=== Bounded Edit Failure Evidence ===");
        recoveryReq.UserPrompt.Should().Contain("Expected File Hash");

        // Recovery count is strictly 1 (total calls = 2)
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public async Task CompileRepair_RoutesThroughEchoFreeTargetIdBoundedOperations_WithExactDiagnosticEvidence()
    {
        var relativePath = "TodoServiceTests.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var content = "using Xunit;\n\npublic class TodoServiceTests\n{\n    [Fact]\n    public void Test_1() => Assert.Equal(1, 2);\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        var repairRequest = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Repair Build failure for Todo Task (round 1)",
            TaskDescription: "Fix the following authoritative repository check failure (repair round 1/3):\nTodoServiceTests.cs(6,32): error CS0103: The name 'Assert' does not exist in the current context",
            AcceptanceCriteria: "Resolve the authoritative repository check failure in the focused files without weakening existing tests or checks.",
            ImpactAnalysisSummary: "Repository check repair",
            ProposedPlan: "Repair repository verification failure",
            ImpactedFilePaths: new[] { relativePath },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(relativePath, "Modify", "Fix repository verification failure") },
            IsVerificationRepair: true);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "TodoServiceTests.cs",
              "operations": [
                {
                  "type": "replace",
                  "targetId": "T6",
                  "content": "    public void Test_1() => Xunit.Assert.Equal(1, 1);"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(repairRequest);

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        var capturedReq = _fakeAiProvider.ReceivedRequests[0];
        capturedReq.MaxTokens.Should().Be(2048, "focused repair starts with a bounded 2048 budget");
        capturedReq.SystemPrompt.Should().Contain("targetId");
        capturedReq.SystemPrompt.Should().NotContain("\"oldText\"");
        capturedReq.SystemPrompt.Should().NotContain("\"anchor\"");
        capturedReq.UserPrompt.Should().Contain("=== Verification Failure Evidence ===");
        capturedReq.UserPrompt.Should().Contain("error CS0103");
        capturedReq.UserPrompt.Should().Contain("[T1]");
        capturedReq.UserPrompt.Should().Contain("[T6]");
        capturedReq.UserPrompt.Should().NotContain("=== Reference Architecture Pattern");
        capturedReq.UserPrompt.Should().NotContain("=== Plan Excerpt ===");

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("Xunit.Assert.Equal(1, 1);");
    }

    [Fact]
    public async Task TestRepair_RoutesThroughEchoFreeTargetIdBoundedOperations_WithExactTestFailureEvidence()
    {
        var relativePath = "TodoServiceTests.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var content = "using Xunit;\n\npublic class TodoServiceTests\n{\n    [Fact]\n    public void Test_ThreadSafety() => Assert.True(false);\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        var repairRequest = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Repair test failures for Todo Task (round 1)",
            TaskDescription: "Fix the authoritative failing test evidence (repair round 1/3):\nAssert.True() Failure: Expected True, but got False at TodoServiceTests.Test_ThreadSafety() in TodoServiceTests.cs:line 6",
            AcceptanceCriteria: "Resolve the failing test without weakening existing test assertions.",
            ImpactAnalysisSummary: "Test repair",
            ProposedPlan: "Repair test failure",
            ImpactedFilePaths: new[] { relativePath },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(relativePath, "Modify", "Fix test failure") },
            IsVerificationRepair: true);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "TodoServiceTests.cs",
              "operations": [
                {
                  "type": "replace",
                  "targetId": "T6",
                  "content": "    public void Test_ThreadSafety() => Assert.True(true);"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(repairRequest);

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        var capturedReq = _fakeAiProvider.ReceivedRequests[0];
        capturedReq.MaxTokens.Should().Be(2048);
        capturedReq.SystemPrompt.Should().Contain("targetId");
        capturedReq.SystemPrompt.Should().NotContain("\"oldText\"");
        capturedReq.UserPrompt.Should().Contain("=== Verification Failure Evidence ===");
        capturedReq.UserPrompt.Should().Contain("Assert.True() Failure");
        capturedReq.UserPrompt.Should().Contain("[T6]");

        var updated = await File.ReadAllTextAsync(fullPath);
        updated.Should().Contain("Assert.True(true);");
    }

    [Fact]
    public async Task FocusedRepair_DoesNotEscalateTo8192_OnOutputLimit()
    {
        var relativePath = "TodoServiceTests.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var content = "using Xunit;\n\npublic class TodoServiceTests\n{\n    [Fact]\n    public void Test_1() => Assert.True(false);\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        var repairRequest = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Repair test failures (round 1)",
            TaskDescription: "Fix test failure",
            AcceptanceCriteria: "Resolve test",
            ImpactAnalysisSummary: "Test repair",
            ProposedPlan: "Repair",
            ImpactedFilePaths: new[] { relativePath },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(relativePath, "Modify", "Fix") },
            IsVerificationRepair: true);

        // Model returns length limit exceeded
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            Provider = "Kimi",
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            Content = "{\"filePath\":\"TodoServiceTests.cs\",\"operations\":["
        });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(repairRequest);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exhausted the configured output token limit");

        // Exactly 1 call attempted, NO escalation to 8192
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
    }

    [Fact]
    public async Task OrdinaryRequest_WithRepairTitle_RemainsOrdinaryGeneration_WhenIsVerificationRepairIsFalse()
    {
        var relativePath = "CacheService.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        var content = "namespace Services;\n\npublic class CacheService\n{\n    public void Invalidate() {}\n}\n";
        await File.WriteAllTextAsync(fullPath, content);

        var ordinaryRequest = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Repair cache invalidation bug in CacheService",
            TaskDescription: "Fix cache key expiration so invalidated items are purged immediately.",
            AcceptanceCriteria: "Ensure cache invalidation purges key.",
            ImpactAnalysisSummary: "Repository check repair summary",
            ProposedPlan: "Plan for cache repair",
            ImpactedFilePaths: new[] { relativePath },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(relativePath, "Modify", "Fix bug") },
            IsVerificationRepair: false);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "CacheService.cs",
              "operations": [
                {
                  "type": "replace",
                  "targetId": "T5",
                  "content": "    public void Invalidate() { /* purged */ }"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(ordinaryRequest);

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        var capturedReq = _fakeAiProvider.ReceivedRequests[0];
        capturedReq.UserPrompt.Should().Contain("Task Title: Repair cache invalidation bug in CacheService");
        capturedReq.UserPrompt.Should().Contain("Task Description: Fix cache key expiration");
        capturedReq.UserPrompt.Should().Contain("=== Plan Excerpt ===");
        capturedReq.UserPrompt.Should().NotContain("=== Verification Failure Evidence ===");
    }

    [Fact]
    public async Task VerificationRepair_LargeTestFile_PreservesOriginalSourceTargetIdCoordinates()
    {
        var relativePath = "LargeTodoServiceTests.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);

        // Generate a 100-line test file
        var lines = new List<string>
        {
            "using Xunit;",
            "namespace Tests;",
            "",
            "public class LargeTodoServiceTests",
            "{"
        };

        for (int i = 6; i <= 95; i++)
        {
            lines.Add($"    [Fact] public void Test_{i}() => Assert.True(true);");
        }

        // Line 96 (1-indexed) is the failing test
        lines.Add("    [Fact] public void Test_96() => Assert.True(false);");
        lines.Add("    [Fact] public void Test_97() => Assert.True(true);");
        lines.Add("    [Fact] public void Test_98() => Assert.True(true);");
        lines.Add("    [Fact] public void Test_99() => Assert.True(true);");
        lines.Add("}"); // Line 100

        var originalContent = string.Join("\n", lines) + "\n";
        await File.WriteAllTextAsync(fullPath, originalContent);
        var expectedHash = WorktreeEditApplier.ComputeContentHash(originalContent);

        var repairRequest = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Repair test failures (round 1)",
            TaskDescription: "Fix the authoritative failing test evidence (repair round 1/3):\nAssert.True() Failure: Expected True, but got False at LargeTodoServiceTests.Test_96() in LargeTodoServiceTests.cs:line 96",
            AcceptanceCriteria: "Resolve failing test.",
            ImpactAnalysisSummary: "Test repair",
            ProposedPlan: "Repair",
            ImpactedFilePaths: new[] { relativePath },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail(relativePath, "Modify", "Fix test") },
            IsVerificationRepair: true);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "LargeTodoServiceTests.cs",
              "operations": [
                {
                  "type": "replace",
                  "targetId": "T96",
                  "content": "    [Fact] public void Test_96() => Assert.True(true);"
                }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(repairRequest);

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        var capturedReq = _fakeAiProvider.ReceivedRequests[0];
        capturedReq.MaxTokens.Should().Be(2048, "repair still uses <=2048 output tokens");
        capturedReq.UserPrompt.Should().Contain($"Expected File Hash: {expectedHash}", "full-file expected hash remains fresh current source hash");
        capturedReq.UserPrompt.Should().Contain("[T1] using Xunit;");
        capturedReq.UserPrompt.Should().Contain("[T96]     [Fact] public void Test_96() => Assert.True(false);", "real original target ID is preserved");
        capturedReq.UserPrompt.Should().Contain("[T100] }", "closing class bracket retains original line number T100");
        capturedReq.UserPrompt.Should().Contain("// ... [lines T", "narrowed context uses synthetic omission marker without assigning target ID");

        // Verify applied result
        var updatedContent = await File.ReadAllTextAsync(fullPath);
        updatedContent.Should().Contain("Test_96() => Assert.True(true);");
        var updatedLines = updatedContent.Replace("\r\n", "\n").Split('\n');
        updatedLines[95].Should().Be("    [Fact] public void Test_96() => Assert.True(true);");
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
