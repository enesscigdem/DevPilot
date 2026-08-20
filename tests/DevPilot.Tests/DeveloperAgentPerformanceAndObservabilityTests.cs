using System.Collections.Concurrent;
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

public class DeveloperAgentPerformanceAndObservabilityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly FakeExecutionActivityRecorder _activityRecorder;

    public DeveloperAgentPerformanceAndObservabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotPerfTests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/perf-test-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        InitGitRepo(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _fakeAiProvider = new FakeAiProvider();
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _activityRecorder = new FakeExecutionActivityRecorder();
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
    public async Task TokenBudget_DefaultConfig_UsesCategoryBudgetForFileEdit()
    {
        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance);

        // Verification through prompt execution
        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/Service.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        _fakeAiProvider.ResponseToReturn = """
            {
              "filePath": "src/Service.cs",
              "action": "Create",
              "newContent": "public class Service {}"
            }
            """;

        var result = await agent.GenerateAndApplyEditsAsync(request);
        result.Success.Should().BeTrue();
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().Be(8192);
    }

    [Fact]
    public void ContextReduction_TestFilePrompt_ContainsMinimalGuidance_ExcludesFullGraphProse()
    {
        var entry = new ManifestFileEntry("tests/DevPilot.Tests/MyTests.cs", FileEditAction.Create, "Add test", null);
        var sysPrompt = DeveloperAgent.BuildSingleFileSystemPrompt(entry);

        sysPrompt.Should().Contain("MINIMAL TESTS");
        sysPrompt.Should().Contain("existing test conventions");

        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            new DeveloperAgentRequest(
                TaskId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                TaskTitle: "Add test task",
                TaskDescription: "Desc",
                AcceptanceCriteria: "Must pass",
                ImpactAnalysisSummary: "Huge prose summary",
                ProposedPlan: "Step 1: Add DTO\nStep 2: Add test tests/DevPilot.Tests/MyTests.cs",
                ImpactedFilePaths: new[] { "tests/DevPilot.Tests/MyTests.cs" },
                WorkspacePath: _worktreeDir,
                BranchName: _branchName),
            entry,
            new Dictionary<string, string>(),
            new List<DiscoveredProjectNode>
            {
                new() { ProjectName = "DevPilot.Tests", ProjectPath = "tests/DevPilot.Tests/DevPilot.Tests.csproj", ProjectDirectory = "tests/DevPilot.Tests", IsTestProject = true }
            });

        userPrompt.Should().NotContain("=== Discovered .NET Project Graph ===");
        userPrompt.Should().NotContain("Huge prose summary");
        userPrompt.Should().Contain("Target Project: DevPilot.Tests");
        userPrompt.Should().Contain("Step 2: Add test tests/DevPilot.Tests/MyTests.cs");
    }

    [Fact]
    public void ContextReduction_DependencySnippetsIncludedOnlyWhenReferenced()
    {
        var entry = new ManifestFileEntry(
            "src/App/Handler.cs",
            FileEditAction.Create,
            "Add handler",
            new[] { "src/App/IDto.cs" });

        var contextFiles = new Dictionary<string, string>
        {
            ["src/App/IDto.cs"] = "public interface IDto { int Id { get; } }",
            ["src/App/Unrelated.cs"] = "public class UnrelatedHugeContext {}"
        };

        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            new DeveloperAgentRequest(
                TaskId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                TaskTitle: "Add handler",
                TaskDescription: "Desc",
                AcceptanceCriteria: null,
                ImpactAnalysisSummary: "Summary",
                ProposedPlan: "Plan",
                ImpactedFilePaths: new[] { "src/App/Handler.cs", "src/App/IDto.cs", "src/App/Unrelated.cs" },
                WorkspacePath: _worktreeDir,
                BranchName: _branchName),
            entry,
            contextFiles,
            new List<DiscoveredProjectNode>());

        userPrompt.Should().Contain("--- Dependency File: src/App/IDto.cs ---");
        userPrompt.Should().Contain("public interface IDto");
        userPrompt.Should().NotContain("UnrelatedHugeContext");
    }

    [Fact]
    public void BuildExecutionWaves_QueryScheduledBeforeQueryHandler_EvenWithEmptyDependencies()
    {
        var files = new List<ManifestFileEntry>
        {
            new("src/Application/Queries/GetTaskSummaryQueryHandler.cs", FileEditAction.Create, "Handler", null),
            new("src/Application/Queries/GetTaskSummaryQuery.cs", FileEditAction.Create, "Query", null)
        };

        var waves = DeveloperAgent.BuildExecutionWaves(files, new List<DiscoveredProjectNode>());

        waves.Should().HaveCount(2, "Query (Layer 25) must be in Wave 1 and Handler (Layer 30) must be in Wave 2");
        waves[0].Select(f => f.FilePath).Should().Equal("src/Application/Queries/GetTaskSummaryQuery.cs");
        waves[1].Select(f => f.FilePath).Should().Equal("src/Application/Queries/GetTaskSummaryQueryHandler.cs");
    }

    [Fact]
    public void BuildExecutionWaves_InterfaceScheduledBeforeInfrastructureImplementation()
    {
        var files = new List<ManifestFileEntry>
        {
            new("src/Infrastructure/Repositories/EfTaskRepository.cs", FileEditAction.Create, "Repo impl", null),
            new("src/Application/Ports/ITaskRepository.cs", FileEditAction.Create, "Interface", null)
        };

        var waves = DeveloperAgent.BuildExecutionWaves(files, new List<DiscoveredProjectNode>());

        waves.Should().HaveCount(2, "Interface (Layer 10) must be scheduled before Infrastructure (Layer 40)");
        waves[0].Select(f => f.FilePath).Should().Equal("src/Application/Ports/ITaskRepository.cs");
        waves[1].Select(f => f.FilePath).Should().Equal("src/Infrastructure/Repositories/EfTaskRepository.cs");
    }

    [Fact]
    public void BuildExecutionWaves_ControllerWaitsForApplicationContractAndHandler()
    {
        var files = new List<ManifestFileEntry>
        {
            new("src/Api/Controllers/TasksController.cs", FileEditAction.Create, "Controller", null),
            new("src/Application/Dtos/TaskSummaryDto.cs", FileEditAction.Create, "Dto", null),
            new("src/Application/Queries/GetTaskSummaryQuery.cs", FileEditAction.Create, "Query", null),
            new("src/Application/Queries/GetTaskSummaryQueryHandler.cs", FileEditAction.Create, "Handler", null)
        };

        var waves = DeveloperAgent.BuildExecutionWaves(files, new List<DiscoveredProjectNode>());

        waves.Should().HaveCount(4);
        waves[0].Select(f => f.FilePath).Should().Equal("src/Application/Dtos/TaskSummaryDto.cs");
        waves[1].Select(f => f.FilePath).Should().Equal("src/Application/Queries/GetTaskSummaryQuery.cs");
        waves[2].Select(f => f.FilePath).Should().Equal("src/Application/Queries/GetTaskSummaryQueryHandler.cs");
        waves[3].Select(f => f.FilePath).Should().Equal("src/Api/Controllers/TasksController.cs");
    }

    [Fact]
    public void BuildExecutionWaves_TestsWaitForAllProductionFiles()
    {
        var files = new List<ManifestFileEntry>
        {
            new("tests/DevPilot.Tests/TaskSummaryTests.cs", FileEditAction.Create, "Tests", null),
            new("src/Application/Dtos/TaskSummaryDto.cs", FileEditAction.Create, "Dto", null),
            new("src/Api/Controllers/TasksController.cs", FileEditAction.Create, "Controller", null)
        };

        var waves = DeveloperAgent.BuildExecutionWaves(files, new List<DiscoveredProjectNode>());

        waves.Should().HaveCount(3);
        waves[0].Select(f => f.FilePath).Should().Equal("src/Application/Dtos/TaskSummaryDto.cs");
        waves[1].Select(f => f.FilePath).Should().Equal("src/Api/Controllers/TasksController.cs");
        waves[2].Select(f => f.FilePath).Should().Equal("tests/DevPilot.Tests/TaskSummaryTests.cs");
    }

    [Fact]
    public void BuildExecutionWaves_IndependentDtosRunInSameWave()
    {
        var files = new List<ManifestFileEntry>
        {
            new("src/Application/Dtos/UserDto.cs", FileEditAction.Create, "User Dto", null),
            new("src/Application/Dtos/OrderDto.cs", FileEditAction.Create, "Order Dto", null)
        };

        var waves = DeveloperAgent.BuildExecutionWaves(files, new List<DiscoveredProjectNode>());

        waves.Should().HaveCount(1, "Independent DTOs in Layer 5 should run concurrently");
        waves[0].Should().HaveCount(2);
    }

    [Fact]
    public void BuildExecutionWaves_IntraGroupPotentialDependency_PartitionsSequentially()
    {
        var files = new List<ManifestFileEntry>
        {
            new("src/Application/Dtos/TaskDto.cs", FileEditAction.Create, "Task Dto", null),
            new("src/Application/Dtos/TaskDtoBase.cs", FileEditAction.Create, "Task Dto Base", null)
        };

        var waves = DeveloperAgent.BuildExecutionWaves(files, new List<DiscoveredProjectNode>());

        waves.Should().HaveCount(2, "When name matching suggests base/derived relationship, partition into sequential sub-waves");
    }

    [Fact]
    public void ContextReduction_ReceivesInMemoryGeneratedDependencySnippetsFromEarlierWaves()
    {
        var handlerEntry = new ManifestFileEntry(
            "src/Application/Queries/GetTaskSummaryQueryHandler.cs",
            FileEditAction.Create,
            "Handler",
            null);

        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["src/Application/Queries/GetTaskSummaryQuery.cs"] = new(
                "src/Application/Queries/GetTaskSummaryQuery.cs",
                FileEditAction.Create,
                "public record GetTaskSummaryQuery(Guid TaskId);",
                null),
            ["src/Application/Unrelated/UnrelatedService.cs"] = new(
                "src/Application/Unrelated/UnrelatedService.cs",
                FileEditAction.Create,
                "public class UnrelatedService {}",
                null)
        };

        var userPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            new DeveloperAgentRequest(
                TaskId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                TaskTitle: "Add summary",
                TaskDescription: "Desc",
                AcceptanceCriteria: null,
                ImpactAnalysisSummary: "Summary",
                ProposedPlan: "Plan",
                ImpactedFilePaths: new[] { "src/Application/Queries/GetTaskSummaryQueryHandler.cs" },
                WorkspacePath: _worktreeDir,
                BranchName: _branchName),
            handlerEntry,
            new Dictionary<string, string>(),
            completedEdits,
            new List<DiscoveredProjectNode>());

        userPrompt.Should().Contain("=== In-Memory Generated Dependency Snippets ===");
        userPrompt.Should().Contain("--- Generated Dependency: src/Application/Queries/GetTaskSummaryQuery.cs ---");
        userPrompt.Should().Contain("public record GetTaskSummaryQuery(Guid TaskId);");
        userPrompt.Should().NotContain("UnrelatedService");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_ParallelGeneration_BoundedByMaxConcurrency()
    {
        var concurrencyTracker = new ConcurrencyTrackerAiProvider(maxSimulatedDelayMs: 60);

        var agent = new DeveloperAgent(
            concurrencyTracker,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxConcurrentFileGenerations"] = "2"
            }).Build(),
            _activityRecorder);

        var files = new[]
        {
            "src/Dtos/Dto1.cs",
            "src/Dtos/Dto2.cs",
            "src/Dtos/Dto3.cs",
            "src/Dtos/Dto4.cs"
        };

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "4 Independent Dtos",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: files,
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().HaveCount(4);
        concurrencyTracker.MaxObservedConcurrency.Should().BeInRange(2, 2);
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_AtomicFailure_LeavesWorktreeUntouched()
    {
        var f1 = Path.Combine(_worktreeDir, "src/F1.cs");
        var f2 = Path.Combine(_worktreeDir, "src/F2.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(f1)!);

        const string initialF1 = "public class F1 {}";
        await File.WriteAllTextAsync(f1, initialF1);

        var provider = new FailingAiProvider(failingTargetFilePath: "src/F2.cs");

        var agent = new DeveloperAgent(
            provider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            activityRecorder: _activityRecorder);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Atomic Failure Test",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/F1.cs", "src/F2.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("src/F2.cs");

        // F1 must remain untouched!
        (await File.ReadAllTextAsync(f1)).Should().Be(initialF1);
        File.Exists(f2).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_ActivityRecording_LogsTruthfulProgression()
    {
        var executionId = Guid.NewGuid();
        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            activityRecorder: _activityRecorder);

        var f1 = "src/Models/Model1.cs";
        var f2 = "src/Models/Model2.cs";

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/Models/Model1.cs",
              "action": "Create",
              "newContent": "public class Model1 {}"
            }
            """);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/Models/Model2.cs",
              "action": "Create",
              "newContent": "public class Model2 {}"
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: executionId,
            TaskTitle: "Activity Log Test",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { f1, f2 },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();

        var messages = _activityRecorder.RecordedMessages;
        messages.Should().Contain("Preparing 2 file edits.");
        messages.Should().Contain(m => m.StartsWith("Generating edit") && m.Contains("Model1.cs"));
        messages.Should().Contain(m => m.StartsWith("Generated edit") && m.Contains("Model1.cs"));
        messages.Should().Contain(m => m.StartsWith("Generating edit") && m.Contains("Model2.cs"));
        messages.Should().Contain(m => m.StartsWith("Generated edit") && m.Contains("Model2.cs"));
        messages.Should().Contain("Validating 2 generated edits.");
        messages.Should().Contain("Applying generated edits.");

        var providerCalls = _activityRecorder.RecordedMetadata
            .Where(metadata => metadata?.LogicalProviderCallCount == 1)
            .ToList();
        providerCalls.Should().HaveCount(2);
        providerCalls.Should().OnlyContain(metadata =>
            metadata != null &&
            metadata.ProviderCallKind == "Generation");
        providerCalls.Select(metadata => metadata!.TargetFile).Should().BeEquivalentTo(f1, f2);

        var summary = _activityRecorder.RecordedMetadata
            .Single(metadata => metadata?.EventKind == "GenerationSummary");
        summary!.LogicalProviderCallCount.Should().Be(2);
        summary.CompactRetryCount.Should().Be(0);
        summary.ApplicabilityRepairCount.Should().Be(0);
        summary.TotalGenerationTimeMs.Should().NotBeNull();

        // Ensure no prompt content or raw source code is leaked into messages
        foreach (var msg in messages)
        {
            msg.Should().NotContain("public class Model1");
            msg.Should().NotContain("SystemPrompt");
            msg.Should().NotContain("UserPrompt");
        }
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

    private sealed class FakeExecutionActivityRecorder : IExecutionActivityRecorder
    {
        public ConcurrentQueue<string> RecordedMessages { get; } = new();
        public ConcurrentQueue<ExecutionActivityMetadata?> RecordedMetadata { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            RecordedMessages.Enqueue(message);
            RecordedMetadata.Enqueue(metadata);
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrencyTrackerAiProvider : IAiProvider
    {
        public string ProviderName => "ConcurrencyTrackerAiProvider";
        private int _activeCalls;
        public int MaxObservedConcurrency { get; private set; }
        private readonly int _maxSimulatedDelayMs;

        public ConcurrencyTrackerAiProvider(int maxSimulatedDelayMs)
        {
            _maxSimulatedDelayMs = maxSimulatedDelayMs;
        }

        public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _activeCalls);
            lock (this)
            {
                if (current > MaxObservedConcurrency)
                {
                    MaxObservedConcurrency = current;
                }
            }

            await Task.Delay(_maxSimulatedDelayMs, cancellationToken);
            Interlocked.Decrement(ref _activeCalls);

            // Extract file path from request if possible
            var targetMatch = System.Text.RegularExpressions.Regex.Match(request.UserPrompt ?? "", @"Target File: (.*)");
            var targetFile = targetMatch.Success ? targetMatch.Groups[1].Value.Trim() : "File.cs";
            var className = Path.GetFileNameWithoutExtension(targetFile);
            if (string.IsNullOrWhiteSpace(className)) className = "AutoGenerated";

            return new AiResponse
            {
                IsSuccess = true,
                Content = $$"""
                    {
                      "filePath": "{{targetFile}}",
                      "action": "Create",
                      "newContent": "public class {{className}} {}"
                    }
                    """
            };
        }
    }

    private sealed class FailingAiProvider : IAiProvider
    {
        public string ProviderName => "FailingAiProvider";
        private readonly string _failingTargetFilePath;

        public FailingAiProvider(string failingTargetFilePath)
        {
            _failingTargetFilePath = failingTargetFilePath;
        }

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var isFailing = request.UserPrompt?.Contains(_failingTargetFilePath) ?? false;
            if (isFailing)
            {
                return Task.FromResult(new AiResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"AI generation failed for '{_failingTargetFilePath}'."
                });
            }

            var targetMatch = System.Text.RegularExpressions.Regex.Match(request.UserPrompt ?? "", @"Target File: (.*)");
            var targetFile = targetMatch.Success ? targetMatch.Groups[1].Value.Trim() : "File.cs";

            return Task.FromResult(new AiResponse
            {
                IsSuccess = true,
                Content = $$"""
                    {
                      "filePath": "{{targetFile}}",
                      "action": "Modify",
                      "searchReplaceEdits": [
                        { "search": "public class F1 {}", "replace": "public class F1 { int X = 1; }" }
                      ]
                    }
                    """
            });
        }
    }
}
