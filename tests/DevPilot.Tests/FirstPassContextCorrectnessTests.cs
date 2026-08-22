using System.Collections.Concurrent;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests;

public sealed class FirstPassContextCorrectnessTests
{
    private readonly string _workspacePath = "C:/fake/repo";
    private readonly string _branchName = "feature/test";

    [Fact]
    public void DownstreamFile_ReceivesSynthesizedFinalContent_OfPreviouslyGeneratedModifyDependency()
    {
        // Arrange: Service is a dependency of Controller
        var controllerEntry = new ManifestFileEntry(
            "src/Api/TodoController.cs",
            FileEditAction.Create,
            "Create controller",
            new[] { "src/Application/TodoService.cs" });

        var contextFiles = new Dictionary<string, string>
        {
            ["src/Application/TodoService.cs"] = "public class TodoService { /* STALE ORIGINAL DISK CONTENT */ }"
        };

        // virtualWorkspace has the authoritative synthesized post-edit content
        var virtualWorkspace = new Dictionary<string, string>
        {
            ["src/Application/TodoService.cs"] = "public class TodoService { public async Task<int> ClearCompletedAsync() { return 5; } }"
        };

        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["src/Application/TodoService.cs"] = new(
                "src/Application/TodoService.cs",
                FileEditAction.Modify,
                null,
                new[] { new SearchReplaceEdit("/* STALE ORIGINAL DISK CONTENT */", "public async Task<int> ClearCompletedAsync() { return 5; }") })
        };

        var request = new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Clear completed",
            "Desc",
            null,
            "Summary",
            "Plan",
            new[] { "src/Api/TodoController.cs", "src/Application/TodoService.cs" },
            _workspacePath,
            _branchName);

        // Act
        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request,
            controllerEntry,
            contextFiles,
            completedEdits,
            new List<DiscoveredProjectNode>(),
            lockedContracts: null,
            referencePattern: null,
            useFullFileReplacement: false,
            virtualWorkspace: virtualWorkspace);

        // Assert: Prompt MUST contain the synthesized post-edit content, NOT the stale original disk content
        userPrompt.Should().Contain("ClearCompletedAsync");
        userPrompt.Should().NotContain("/* STALE ORIGINAL DISK CONTENT */");
    }

    [Fact]
    public void DownstreamFile_DoesNotReceive_BothStaleOriginalContent_AndGeneratedReplacement()
    {
        // Arrange
        var controllerEntry = new ManifestFileEntry(
            "src/Api/TodoController.cs",
            FileEditAction.Create,
            "Create controller",
            new[] { "src/Application/TodoService.cs" });

        var contextFiles = new Dictionary<string, string>
        {
            ["src/Application/TodoService.cs"] = "public class TodoService { public void OldMethod() {} }"
        };

        var virtualWorkspace = new Dictionary<string, string>
        {
            ["src/Application/TodoService.cs"] = "public class TodoService { public void NewSynthesizedMethod() {} }"
        };

        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["src/Application/TodoService.cs"] = new(
                "src/Application/TodoService.cs",
                FileEditAction.Modify,
                null,
                new[] { new SearchReplaceEdit("public void OldMethod() {}", "public void NewSynthesizedMethod() {}") })
        };

        var request = new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Update method",
            "Desc",
            null,
            "Summary",
            "Plan",
            new[] { "src/Api/TodoController.cs", "src/Application/TodoService.cs" },
            _workspacePath,
            _branchName);

        // Act
        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request,
            controllerEntry,
            contextFiles,
            completedEdits,
            new List<DiscoveredProjectNode>(),
            lockedContracts: null,
            referencePattern: null,
            useFullFileReplacement: false,
            virtualWorkspace: virtualWorkspace);

        // Assert: Authoritative virtual workspace content is injected once, no duplicate or stale blocks
        userPrompt.Should().Contain("NewSynthesizedMethod");
        userPrompt.Should().NotContain("OldMethod");
        userPrompt.Should().NotContain("Replace:\npublic void OldMethod()");
    }

    [Fact]
    public void CreateDependency_IsExposedDownstream_AsGeneratedFinalContent()
    {
        // Arrange
        var handlerEntry = new ManifestFileEntry(
            "src/Application/Commands/CreateTodoCommandHandler.cs",
            FileEditAction.Create,
            "Create handler",
            new[] { "src/Application/Commands/CreateTodoCommand.cs" });

        var virtualWorkspace = new Dictionary<string, string>
        {
            ["src/Application/Commands/CreateTodoCommand.cs"] = "public record CreateTodoCommand(string Title) : IRequest<Guid>;"
        };

        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["src/Application/Commands/CreateTodoCommand.cs"] = new(
                "src/Application/Commands/CreateTodoCommand.cs",
                FileEditAction.Create,
                "public record CreateTodoCommand(string Title) : IRequest<Guid>;",
                null)
        };

        var request = new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Create todo",
            "Desc",
            null,
            "Summary",
            "Plan",
            new[] { "src/Application/Commands/CreateTodoCommandHandler.cs" },
            _workspacePath,
            _branchName);

        // Act
        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request,
            handlerEntry,
            new Dictionary<string, string>(),
            completedEdits,
            new List<DiscoveredProjectNode>(),
            lockedContracts: null,
            referencePattern: null,
            useFullFileReplacement: false,
            virtualWorkspace: virtualWorkspace);

        // Assert
        userPrompt.Should().Contain("CreateTodoCommand(string Title)");
    }

    [Fact]
    public void TestFileGeneration_ReceivesRelevantLatestProduction_ImplementationContext()
    {
        // Arrange: Test target testing TodoService
        var testEntry = new ManifestFileEntry(
            "tests/DevPilot.Tests/TodoServiceTests.cs",
            FileEditAction.Modify,
            "Add tests for delete completed",
            null);

        var serviceContent = @"namespace DevPilot.Services;
public class TodoService : ITodoService
{
    private readonly ITodoRepository _repo;
    public TodoService(ITodoRepository repo) => _repo = repo;

    public async Task<int> DeleteCompletedAsync(CancellationToken ct = default)
    {
        var completed = await _repo.GetCompletedAsync(ct);
        if (completed.Count == 0) return 0;
        await _repo.DeleteBatchAsync(completed.Select(t => t.Id), ct);
        return completed.Count;
    }
}";

        var virtualWorkspace = new Dictionary<string, string>
        {
            ["src/DevPilot.Application/TodoService.cs"] = serviceContent
        };

        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["src/DevPilot.Application/TodoService.cs"] = new(
                "src/DevPilot.Application/TodoService.cs",
                FileEditAction.Modify,
                null,
                new[] { new SearchReplaceEdit("// ...", serviceContent) })
        };

        // Act
        var relevant = DeveloperAgent.GetRelevantGeneratedEdits(testEntry, completedEdits, virtualWorkspace);

        // Assert: Test file must see the behavioral implementation body, not just empty signature
        relevant.Should().ContainKey("src/DevPilot.Application/TodoService.cs");
        relevant["src/DevPilot.Application/TodoService.cs"].Should().Contain("DeleteCompletedAsync");
        relevant["src/DevPilot.Application/TodoService.cs"].Should().Contain("GetCompletedAsync");
        relevant["src/DevPilot.Application/TodoService.cs"].Should().Contain("if (completed.Count == 0) return 0;");
    }

    [Fact]
    public void ExistingNonEmptyModifyTarget_DoesNotReceive_ReferenceArchitecturePattern()
    {
        // Arrange: Modify an existing non-empty file
        var entry = new ManifestFileEntry("src/Services/TodoService.cs", FileEditAction.Modify, "Update service", null);
        var contextFiles = new Dictionary<string, string>
        {
            ["src/Services/TodoService.cs"] = "public class TodoService {\n    // Existing service implementation with 10 lines\n    public void Existing() {}\n}"
        };

        var request = new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Update service",
            "Desc",
            null,
            "Summary",
            "Plan",
            new[] { "src/Services/TodoService.cs" },
            _workspacePath,
            _branchName);

        var foreignPattern = "public class ForeignReferencePattern : IForeignPattern {\n    // Some MediatR query pattern\n}";

        // Act
        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request,
            entry,
            contextFiles,
            completedEdits: null,
            new List<DiscoveredProjectNode>(),
            lockedContracts: null,
            referencePattern: foreignPattern,
            useFullFileReplacement: false,
            virtualWorkspace: null);

        // Assert: Should NOT contain reference pattern
        userPrompt.Should().NotContain("=== Reference Architecture Pattern (MANDATORY) ===");
        userPrompt.Should().NotContain("ForeignReferencePattern");
    }

    [Fact]
    public void CreateTarget_StillReceives_ReferenceArchitecturePattern()
    {
        // Arrange: Create a brand new file
        var entry = new ManifestFileEntry("src/Services/NewService.cs", FileEditAction.Create, "Create new service", null);
        var contextFiles = new Dictionary<string, string>();

        var request = new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Create service",
            "Desc",
            null,
            "Summary",
            "Plan",
            new[] { "src/Services/NewService.cs" },
            _workspacePath,
            _branchName);

        var referencePattern = "public class ExamplePatternService : IExampleService {\n    public void Execute() {}\n}";

        // Act
        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request,
            entry,
            contextFiles,
            completedEdits: null,
            new List<DiscoveredProjectNode>(),
            lockedContracts: null,
            referencePattern: referencePattern,
            useFullFileReplacement: false,
            virtualWorkspace: null);

        // Assert: Create target SHOULD receive reference pattern
        userPrompt.Should().Contain("=== Reference Architecture Pattern (MANDATORY) ===");
        userPrompt.Should().Contain("ExamplePatternService");
    }

    [Fact]
    public void VirtualWorkspace_ConcurrentWritesAndReads_AreThreadSafe()
    {
        // Arrange
        var virtualWorkspace = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Act & Assert: Multiple threads writing and reading concurrently
        Parallel.For(0, 100, i =>
        {
            var path = $"src/File{i}.cs";
            var content = $"public class File{i} {{ public int Value => {i}; }}";
            virtualWorkspace[path] = content;

            virtualWorkspace.TryGetValue(path, out var readBack).Should().BeTrue();
            readBack.Should().Be(content);
        });

        virtualWorkspace.Count.Should().Be(100);
    }

    [Fact]
    public void TestFileGeneration_WithLargeProductionDependency_ReceivesBothContractAndBehavioralImplementation()
    {
        // Arrange: Test target testing a large service (>4000 characters)
        var testEntry = new ManifestFileEntry(
            "tests/DevPilot.Tests/LargeTodoServiceTests.cs",
            FileEditAction.Modify,
            "Add tests for bulk operation",
            null);

        // Build a realistic C# class exceeding 4,000 characters (approx. 5,000 characters)
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("namespace DevPilot.Services;");
        sb.AppendLine("public class LargeTodoService : ILargeTodoService");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly ITodoRepository _repository;");
        sb.AppendLine("    public LargeTodoService(ITodoRepository repository) { _repository = repository; }");
        sb.AppendLine();

        // 10 filler methods to build up file size realistically
        for (int i = 1; i <= 10; i++)
        {
            sb.AppendLine($"    public async Task<TodoItemDto> GetItemById{i}Async(Guid id, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (id == Guid.Empty) throw new ArgumentException(\"Invalid id {i}\");");
            sb.AppendLine($"        var item = await _repository.FindByIdAsync(id, ct);");
            sb.AppendLine($"        if (item == null) throw new KeyNotFoundException(\"Item {i} not found\");");
            sb.AppendLine($"        return new TodoItemDto(item.Id, item.Title, item.IsCompleted, item.CreatedAt);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // The newly added/modified behavioral method
        sb.AppendLine("    public async Task<int> ExecuteBulkOperationAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (itemIds == null || itemIds.Count == 0) return 0;");
        sb.AppendLine("        var items = await _repository.GetBatchAsync(itemIds, ct);");
        sb.AppendLine("        if (items.Count == 0) return 0;");
        sb.AppendLine("        await _repository.ProcessBatchAsync(items, ct);");
        sb.AppendLine("        return items.Count;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        var largeServiceContent = sb.ToString();
        largeServiceContent.Length.Should().BeGreaterThan(4000);

        var virtualWorkspace = new Dictionary<string, string>
        {
            ["src/DevPilot.Application/LargeTodoService.cs"] = largeServiceContent
        };

        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["src/DevPilot.Application/LargeTodoService.cs"] = new(
                "src/DevPilot.Application/LargeTodoService.cs",
                FileEditAction.Create,
                largeServiceContent,
                null)
        };

        // Act
        var relevant = DeveloperAgent.GetRelevantGeneratedEdits(testEntry, completedEdits, virtualWorkspace);

        // Assert
        relevant.Should().ContainKey("src/DevPilot.Application/LargeTodoService.cs");
        var context = relevant["src/DevPilot.Application/LargeTodoService.cs"];

        // 1. Must contain the authoritative public contract / signatures
        context.Should().Contain("=== Authoritative Public Contract ===");
        context.Should().Contain("LargeTodoService");
        context.Should().Contain("ExecuteBulkOperationAsync");

        // 2. Must contain the bounded behavioral implementation body (not only empty signatures!)
        context.Should().Contain("=== Relevant Implementation Behavior ===");
        context.Should().Contain("if (id == Guid.Empty) throw new ArgumentException");
        context.Should().Contain("if (item == null) throw new KeyNotFoundException");

        // 3. Must be strictly bounded to prevent token explosion
        context.Length.Should().BeLessThanOrEqualTo(4000);
    }

    [Fact]
    public void ExistingTestFileModify_SystemPrompt_RequiresLocalizedSearchReplaceAndForbidsUnchangedTestReproduction()
    {
        var testEntry = new ManifestFileEntry("tests/DevPilot.Tests/TodoControllerTests.cs", FileEditAction.Modify, "Add delete completed tests", null);
        var sysPrompt = DeveloperAgent.BuildSingleFileSystemPrompt(testEntry, useFullFileReplacement: false);

        // 1. Explicitly requires localized SEARCH/REPLACE
        sysPrompt.Should().Contain("existing test-file Modify");
        sysPrompt.Should().Contain("compact 'searchReplaceEdits'");
        sysPrompt.Should().Contain("searchReplaceEdits");

        // 2. Explicitly forbids reproducing unchanged tests or full class
        sysPrompt.Should().Contain("DO NOT reproduce unchanged test methods or the entire test class");
        sysPrompt.Should().Contain("TEST MODIFY DISCIPLINE");
        sysPrompt.Should().Contain("DO NOT recreate or duplicate existing fixtures, fields, or unchanged tests");
    }

    [Fact]
    public void ExistingTestFileModify_UserPrompt_SetsSurgicalTestPatchStrategy_AndPreventsFullClassRewrite()
    {
        var testEntry = new ManifestFileEntry("tests/DevPilot.Tests/TodoControllerTests.cs", FileEditAction.Modify, "Add delete completed tests", null);
        var contextFiles = new Dictionary<string, string>
        {
            ["tests/DevPilot.Tests/TodoControllerTests.cs"] = "public class TodoControllerTests {\n    // 50 lines of existing tests\n}"
        };

        var request = new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Delete completed",
            "Desc",
            null,
            "Summary",
            "Plan",
            new[] { "tests/DevPilot.Tests/TodoControllerTests.cs" },
            _workspacePath,
            _branchName);

        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request,
            testEntry,
            contextFiles,
            completedEdits: null,
            new List<DiscoveredProjectNode>(),
            lockedContracts: null,
            referencePattern: null,
            useFullFileReplacement: false,
            virtualWorkspace: null);

        // Strategy explicitly guides surgical insertion and forbids repeating unchanged tests
        userPrompt.Should().Contain("Edit Strategy: surgical test patch");
        userPrompt.Should().Contain("NEVER repeat existing unchanged tests, fixtures, or the full test class");
    }

    [Fact]
    public void ExistingTestFileModify_CompactPrompt_EmphasizesMinimalTestInsertion()
    {
        var testEntry = new ManifestFileEntry("tests/DevPilot.Tests/TodoControllerTests.cs", FileEditAction.Modify, "Add delete completed tests", null);
        var sysPrompt = DeveloperAgent.BuildCompactSingleFileSystemPrompt(testEntry, useFullFileReplacement: false);
        var userPrompt = DeveloperAgent.BuildCompactSingleFileUserPrompt(
            new DeveloperAgentRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Delete completed",
                "Desc",
                null,
                "Summary",
                "Plan",
                new[] { "tests/DevPilot.Tests/TodoControllerTests.cs" },
                _workspacePath,
                _branchName),
            testEntry,
            "public class TodoControllerTests { }",
            lockedContracts: null,
            useFullFileReplacement: false);

        sysPrompt.Should().Contain("NEVER repeat existing tests or the test class");
        userPrompt.Should().Contain("CRITICAL TEST MODIFY DISCIPLINE: Emit ONLY the minimal searchReplaceEdit inserting the new test method(s)");
    }

    [Fact]
    public void CreateTestFile_StillUsesFullContentBehavior()
    {
        var createTestEntry = new ManifestFileEntry("tests/DevPilot.Tests/NewControllerTests.cs", FileEditAction.Create, "Create new tests", null);
        var sysPrompt = DeveloperAgent.BuildSingleFileSystemPrompt(createTestEntry, useFullFileReplacement: false);

        sysPrompt.Should().Contain("For 'Create' actions, specify 'newContent' containing the complete, valid file content");
        sysPrompt.Should().Contain("MINIMAL TESTS");
    }

    [Fact]
    public void NonTestModify_UsesStandardSurgicalPatchStrategy()
    {
        var serviceEntry = new ManifestFileEntry("src/Services/TodoService.cs", FileEditAction.Modify, "Modify service", null);
        var sysPrompt = DeveloperAgent.BuildSingleFileSystemPrompt(serviceEntry, useFullFileReplacement: false);
        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            new DeveloperAgentRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Modify service",
                "Desc",
                null,
                "Summary",
                "Plan",
                new[] { "src/Services/TodoService.cs" },
                _workspacePath,
                _branchName),
            serviceEntry,
            new Dictionary<string, string> { ["src/Services/TodoService.cs"] = "public class TodoService { }" },
            completedEdits: null,
            new List<DiscoveredProjectNode>(),
            lockedContracts: null,
            referencePattern: null,
            useFullFileReplacement: false,
            virtualWorkspace: null);

        sysPrompt.Should().Contain("This is a large-file Modify. Return only compact 'searchReplaceEdits'");
        sysPrompt.Should().NotContain("TEST MODIFY DISCIPLINE");
        userPrompt.Should().Contain("Edit Strategy: surgical patch. Return only minimal searchReplaceEdits");
    }
}
