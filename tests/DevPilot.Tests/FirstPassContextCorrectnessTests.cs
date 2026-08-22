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
}
