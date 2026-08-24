using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
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

public sealed class RepositoryNativeGenerationPatternTests : IDisposable
{
    private const string FullBodyMarker = "UNIQUE_FULL_BODY_MARKER_SHOULD_NOT_APPEAR";
    private const string ProducerContract = "UNIQUE_SYNTHESIZED_PRODUCER_CONTRACT_v2";

    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly WorktreeEditApplier _editApplier;
    private readonly RecordingActivityRecorder _recorder;

    public RepositoryNativeGenerationPatternTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotRepoNative_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/repo-native";
        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);
        InitGit(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "init");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _recorder = new RecordingActivityRecorder();
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
            // best-effort
        }
    }

    [Fact]
    public void AnalogousRoutesDependOnController_FromExistingRepositoryEdge()
    {
        var files = FeaturePair("src/routes/featureBRoutes.ts", "src/controllers/featureBController.ts");
        var evidence = ExistingPair(
            "src/routes/featureARoutes.ts",
            "import { FeatureAController } from '../controllers/featureAController';",
            "src/controllers/featureAController.ts",
            "export class FeatureAController {}");

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(files, evidence);

        map["src/routes/featureBRoutes.ts"].Should().Contain("src/controllers/featureBController.ts");
        map["src/controllers/featureBController.ts"].Should().NotContain("src/routes/featureBRoutes.ts");
        GenerationDependencyAnalyzer.HasAnalogousRepositoryDependency(files[0], files[1], files, evidence).Should().BeTrue();
    }

    [Fact]
    public void AnalogousControllerDependsOnService_FromExistingRepositoryEdge()
    {
        var files = FeaturePair("src/controllers/featureBController.ts", "src/services/featureBService.ts");
        var evidence = ExistingPair(
            "src/controllers/featureAController.ts",
            "import { FeatureAService } from '../services/featureAService';",
            "src/services/featureAService.ts",
            "export class FeatureAService {}");

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(files, evidence);

        map["src/controllers/featureBController.ts"].Should().Contain("src/services/featureBService.ts");
        map["src/services/featureBService.ts"].Should().NotContain("src/controllers/featureBController.ts");
        GenerationDependencyAnalyzer.HasAnalogousRepositoryDependency(files[0], files[1], files, evidence).Should().BeTrue();
    }

    [Fact]
    public void AnalogousServiceDependsOnRepository_FromExistingRepositoryEdge()
    {
        var files = FeaturePair("src/services/featureBService.ts", "src/repositories/featureBRepository.ts");
        var evidence = ExistingPair(
            "src/services/featureAService.ts",
            "import { FeatureARepository } from '../repositories/featureARepository';",
            "src/repositories/featureARepository.ts",
            "export class FeatureARepository {}");

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(files, evidence);

        map["src/services/featureBService.ts"].Should().Contain("src/repositories/featureBRepository.ts");
        map["src/repositories/featureBRepository.ts"].Should().NotContain("src/services/featureBService.ts");
        GenerationDependencyAnalyzer.DependsOn(files[0], files[1], files, evidence).Should().BeFalse();
        GenerationDependencyAnalyzer.HasAnalogousRepositoryDependency(files[0], files[1], files, evidence).Should().BeTrue();
    }

    [Fact]
    public void AnalogousRepositoryDependsOnStore_OnlyWhenEvidenceSupportsIt()
    {
        var files = FeaturePair("src/repositories/featureBRepository.ts", "src/models/featureB.ts");
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        GenerationDependencyAnalyzer.HasAnalogousRepositoryDependency(files[0], files[1], files, empty).Should().BeFalse();

        var evidence = ExistingPair(
            "src/repositories/featureARepository.ts",
            "import { FeatureA } from '../models/featureA';",
            "src/models/featureA.ts",
            "export interface FeatureA { id: string }");

        GenerationDependencyAnalyzer.HasAnalogousRepositoryDependency(files[0], files[1], files, evidence).Should().BeTrue();
        GenerationDependencyAnalyzer.HasAnalogousRepositoryDependency(files[1], files[0], files, evidence).Should().BeFalse();
    }

    [Fact]
    public void AmbiguousRepositoryEvidence_DoesNotInventAnEdge()
    {
        var files = FeaturePair("src/routes/featureBRoutes.ts", "src/controllers/featureBController.ts");
        var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/routes/featureARoutes.ts"] = "import { FeatureAController } from '../controllers/featureAController';",
            ["src/controllers/featureAController.ts"] = "import { registerFeatureARoutes } from '../routes/featureARoutes';"
        };

        GenerationDependencyAnalyzer.HasAnalogousRepositoryDependency(files[0], files[1], files, evidence).Should().BeFalse();
        GenerationDependencyAnalyzer.HasAnalogousRepositoryDependency(files[1], files[0], files, evidence).Should().BeFalse();

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(files, evidence);
        map["src/routes/featureBRoutes.ts"].Should().NotContain("src/controllers/featureBController.ts");
        HasPrerequisiteCycle(map).Should().BeFalse();
    }

    [Fact]
    public void CyclicInferredRelationships_AreConservativelyIgnored()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/one/beta.ts", FileEditAction.Create),
            new ManifestFileEntry("src/two/beta.ts", FileEditAction.Create),
            new ManifestFileEntry("src/three/beta.ts", FileEditAction.Create)
        };
        var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/one/alpha.ts"] = "import { alpha as two } from '../two/alpha';",
            ["src/two/alpha.ts"] = "import { alpha as three } from '../three/alpha';",
            ["src/three/alpha.ts"] = "import { alpha as one } from '../one/alpha';"
        };

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(files, evidence);
        HasPrerequisiteCycle(map).Should().BeFalse();
        var inferredCount = map.Values.Sum(producers => producers.Count);
        inferredCount.Should().BeLessThan(3);
    }

    [Fact]
    public void PythonImportEvidence_InfersAnalogousDependency()
    {
        var files = FeaturePair("src/routes/featureBRoutes.py", "src/controllers/featureBController.py");
        var evidence = ExistingPair(
            "src/routes/featureARoutes.py",
            "from controllers.featureAController import FeatureAController",
            "src/controllers/featureAController.py",
            "class FeatureAController:\n    pass");

        GenerationDependencyAnalyzer.HasAnalogousRepositoryDependency(files[0], files[1], files, evidence).Should().BeTrue();
    }

    [Fact]
    public async Task IndependentFeatures_StillReachConfiguredConcurrency()
    {
        SeedFeatureALayer();
        var tracker = new ConcurrencyTracker(70);
        var agent = CreateAgent(tracker, concurrency: 4);
        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(new[]
        {
            "src/controllers/featureBController.ts",
            "src/routes/featureBRoutes.ts",
            "src/controllers/featureCController.ts",
            "src/routes/featureCRoutes.ts",
            "src/Dtos/Alpha.cs",
            "src/Dtos/Beta.cs"
        }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        tracker.MaxObservedConcurrency.Should().BeGreaterThan(1);
        tracker.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public void DownstreamCreatePrompt_SeesCompletedProducerFromVirtualWorkspace()
    {
        var request = CreatePromptRequest("src/routes/featureBRoutes.ts", "src/controllers/featureBController.ts");
        var consumer = new ManifestFileEntry("src/routes/featureBRoutes.ts", FileEditAction.Create);
        var contextFiles = ExistingPair(
            "src/routes/featureARoutes.ts",
            "import { FeatureAController } from '../controllers/featureAController';",
            "src/controllers/featureAController.ts",
            "export class FeatureAController {}");
        var completedEdits = new Dictionary<string, FileEditSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/controllers/featureBController.ts"] = new(
                "src/controllers/featureBController.ts",
                FileEditAction.Create,
                $"export function createFeatureBController() {{ return {{ {ProducerContract}: true }}; }}")
        };
        var virtualWorkspace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/controllers/featureBController.ts"] =
                $"export function createFeatureBController() {{ return {{ {ProducerContract}: true }}; }}"
        };

        var prompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request,
            consumer,
            contextFiles,
            completedEdits,
            Array.Empty<DiscoveredProjectNode>(),
            virtualWorkspace: virtualWorkspace);

        prompt.Should().Contain(ProducerContract);
        prompt.Should().Contain("src/controllers/featureBController.ts");
        prompt.Should().Contain("=== In-Memory Generated Dependency Snippets ===");
    }

    [Fact]
    public void CreatePrompt_ReceivesBoundedSameRoleExemplar()
    {
        WriteWorktree("src/routes/featureARoutes.ts", LargeRouteFile());
        var request = CreatePromptRequest("src/routes/featureBRoutes.ts");
        var prompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request,
            new ManifestFileEntry("src/routes/featureBRoutes.ts", FileEditAction.Create),
            new Dictionary<string, string>(),
            Array.Empty<DiscoveredProjectNode>());

        prompt.Should().Contain("=== Same-Role Repository Exemplar ===");
        prompt.Should().Contain("src/routes/featureARoutes.ts");
        prompt.Should().Contain("export function registerFeatureARoutes");
        prompt.Should().NotContain(FullBodyMarker);
        prompt.Should().NotContain(LargeRouteFile());
    }

    [Fact]
    public void ExemplarSelection_IsDeterministicAndBounded()
    {
        var repositoryContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/routes/zzzRoutes.ts"] = LargeRouteFile("zzz"),
            ["src/routes/aaaRoutes.ts"] = LargeRouteFile("aaa")
        };

        var first = GenerationDependencyAnalyzer.SelectSameRoleExemplar("src/routes/featureBRoutes.ts", repositoryContents);
        var second = GenerationDependencyAnalyzer.SelectSameRoleExemplar("src/routes/featureBRoutes.ts", repositoryContents);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.FilePath.Should().Be("src/routes/aaaRoutes.ts");
        second!.FilePath.Should().Be(first.FilePath);
        first.BoundedExcerpt.Should().Be(second.BoundedExcerpt);
        first.BoundedExcerpt.Length.Should().BeLessThanOrEqualTo(GenerationDependencyAnalyzer.MaxExemplarChars);
        first.BoundedExcerpt.Split('\n').Length.Should().BeLessThanOrEqualTo(GenerationDependencyAnalyzer.MaxExemplarLines);
        first.BoundedExcerpt.Should().NotContain(FullBodyMarker);
    }

    [Fact]
    public void ExistingSourceBodies_AreNotBroadlyDumpedIntoCreatePrompt()
    {
        var huge = LargeRouteFile() + Environment.NewLine + string.Join('\n', Enumerable.Range(0, 80).Select(i => $"  app.get('/pad/{i}', () => '{FullBodyMarker}{i}');"));
        WriteWorktree("src/routes/featureARoutes.ts", huge);
        var request = CreatePromptRequest("src/routes/featureBRoutes.ts");
        var prompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request,
            new ManifestFileEntry("src/routes/featureBRoutes.ts", FileEditAction.Create),
            new Dictionary<string, string>
            {
                ["src/routes/featureARoutes.ts"] = huge
            },
            Array.Empty<DiscoveredProjectNode>());

        prompt.Should().Contain("=== Same-Role Repository Exemplar ===");
        prompt.Should().NotContain(FullBodyMarker);
        var exemplar = ExtractSection(prompt, "=== Same-Role Repository Exemplar ===", "--- End Exemplar ---");
        exemplar.Length.Should().BeLessThan(huge.Length);
        exemplar.Length.Should().BeLessThanOrEqualTo(GenerationDependencyAnalyzer.MaxExemplarChars + 400);
    }

    [Fact]
    public void FocusedRepair_ReceivesBoundedSameRoleEvidence_WithoutChangingRepairOrchestration()
    {
        WriteWorktree("src/controllers/featureAController.ts", FeatureAControllerSource());
        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix feature B controller",
            AcceptanceCriteria: "Normalize request params",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/controllers/featureBController.ts" },
            DiagnosticEvidence: "src/controllers/featureBController.ts(4,18): error TS2345: Argument of type 'string | string[]' is not assignable to parameter of type 'string'.",
            DiagnosticLocations: new[] { "src/controllers/featureBController.ts(4,18): error TS2345" });

        var focused = DeveloperAgent.BuildFocusedDiagnosticRepairUserPrompt(
            "src/controllers/featureBController.ts",
            "export function getId(req: any) { return req.params.id; }",
            request);
        var micro = DeveloperAgent.BuildMicroDiagnosticRepairUserPrompt(
            "src/controllers/featureBController.ts",
            "export function getId(req: any) { return req.params.id; }",
            request);
        var anchor = DeveloperAgent.BuildAnchorRefreshRepairUserPrompt(
            "src/controllers/featureBController.ts",
            "export function getId(req: any) { return req.params.id; }",
            request);

        focused.Should().Contain("=== Same-Role Repository Exemplar ===");
        focused.Should().Contain("src/controllers/featureAController.ts");
        focused.Should().Contain("String(req.params.id)");
        focused.Should().NotContain(FullBodyMarker);
        micro.Should().NotContain("Same-Role Repository Exemplar");
        anchor.Should().NotContain("Same-Role Repository Exemplar");

        var agent = CreateAgent(new CountingContractProvider(), concurrency: 4);
        agent.DetermineInitialBudget("src/controllers/featureBController.ts", FileEditAction.Modify)
            .Should().Be(4096);
        agent.DetermineFocusedRepairBudget().Should().Be(4096);
    }

    [Fact]
    public void DevelopmentConcurrency_RemainsFour()
    {
        var json = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "DevPilot.Api", "appsettings.Development.json"));
        using var document = JsonDocument.Parse(json);
        document.RootElement
            .GetProperty("DeveloperAgent")
            .GetProperty("MaxConcurrentFileGenerations")
            .GetInt32()
            .Should().Be(4);
    }

    [Fact]
    public async Task AnalogousDependencyGeneration_DoesNotAddProviderCalls()
    {
        SeedFeatureALayer();
        var provider = new CountingContractProvider();
        var agent = CreateAgent(provider, concurrency: 4);
        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(new[]
        {
            "src/controllers/featureBController.ts",
            "src/routes/featureBRoutes.ts"
        }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.CallCount.Should().Be(2);
        provider.StartOrder.Should().Equal(
            "src/controllers/featureBController.ts",
            "src/routes/featureBRoutes.ts");
        provider.RoutesPrompt.Should().Contain(ProducerContract);
        provider.RoutesPrompt.Should().Contain("=== Same-Role Repository Exemplar ===");
        provider.ControllerPrompt.Should().Contain("=== Same-Role Repository Exemplar ===");
    }

    [Fact]
    public void MinimalCreateRetryPrompt_RemainsUnchanged()
    {
        WriteWorktree("src/routes/featureARoutes.ts", LargeRouteFile());
        var request = CreatePromptRequest("src/routes/featureBRoutes.ts");
        var prompt = DeveloperAgent.BuildMinimalCreateRetryUserPrompt(
            request,
            new ManifestFileEntry("src/routes/featureBRoutes.ts", FileEditAction.Create));

        prompt.Should().Contain("=== MINIMAL CREATE RETRY ===");
        prompt.Should().NotContain("Same-Role Repository Exemplar");
        prompt.Should().NotContain(FullBodyMarker);
    }

    [Fact]
    public void AnalyzerSource_DoesNotHardCodeAcceptanceFilenames()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "DevPilot.Infrastructure",
            "DeveloperAgent",
            "GenerationDependencyAnalyzer.cs"));

        source.Should().NotContain("projectRoutes");
        source.Should().NotContain("issueRoutes");
        source.Should().NotContain("projectController");
        source.Should().NotContain("createProjectRouter");
    }

    private DeveloperAgent CreateAgent(IAiProvider provider, int concurrency) =>
        new(
            provider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxConcurrentFileGenerations"] = concurrency.ToString()
            }).Build(),
            _recorder);

    private DeveloperAgentRequest CreateRequest(IReadOnlyList<string> files) => new(
        TaskId: Guid.NewGuid(),
        ExecutionId: Guid.NewGuid(),
        TaskTitle: "Generate files",
        TaskDescription: "Desc",
        AcceptanceCriteria: null,
        ImpactAnalysisSummary: "Summary",
        ProposedPlan: "Plan",
        ImpactedFilePaths: files,
        WorkspacePath: _worktreeDir,
        BranchName: _branchName);

    private DeveloperAgentRequest CreatePromptRequest(params string[] files) =>
        CreateRequest(files.Length == 0 ? new[] { "src/routes/featureBRoutes.ts" } : files);

    private void SeedFeatureALayer()
    {
        WriteWorktree("src/models/featureA.ts", "export interface FeatureA { id: string }");
        WriteWorktree("src/repositories/featureARepository.ts", """
            import { FeatureA } from '../models/featureA';
            export class FeatureARepository { get(id: string): FeatureA { return { id }; } }
            """);
        WriteWorktree("src/services/featureAService.ts", """
            import { FeatureARepository } from '../repositories/featureARepository';
            export class FeatureAService { constructor(private readonly repo: FeatureARepository) {} }
            """);
        WriteWorktree("src/controllers/featureAController.ts", FeatureAControllerSource());
        WriteWorktree("src/routes/featureARoutes.ts", """
            import { FeatureAController } from '../controllers/featureAController';
            export function registerFeatureARoutes(app: any, controller: FeatureAController) {
              app.get('/feature-a/:id', controller.get);
            }
            """);
    }

    private void WriteWorktree(string relativePath, string content)
    {
        var full = Path.Combine(_worktreeDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static ManifestFileEntry[] FeaturePair(string consumer, string producer) =>
    [
        new ManifestFileEntry(consumer, FileEditAction.Create),
        new ManifestFileEntry(producer, FileEditAction.Create)
    ];

    private static Dictionary<string, string> ExistingPair(
        string consumerPath,
        string consumerContent,
        string producerPath,
        string producerContent) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [consumerPath] = consumerContent,
            [producerPath] = producerContent
        };

    private static string FeatureAControllerSource() => """
        import { Request } from 'express';
        export class FeatureAController {
          constructor(private readonly service: { get(id: string): unknown }) {}
          get = (req: Request) => this.service.get(String(req.params.id));
        }
        """;

    private static string LargeRouteFile(string feature = "FeatureA") => $$"""
        import express from 'express';
        import { {{feature}}Controller } from '../controllers/{{feature}}Controller';
        export function register{{feature}}Routes(app: express.Express, controller: {{feature}}Controller) {
          app.get('/{{feature.ToLowerInvariant()}}/:id', (req, res) => {
            const id = String(req.params.id);
            res.json(controller);
            return id;
          });
        }
        const unusedPadding = [
        {{string.Join(",\n", Enumerable.Range(0, 40).Select(i => $"  '{FullBodyMarker}-{i}'"))}}
        ];
        """;

    private static string ExtractSection(string prompt, string start, string end)
    {
        var startIndex = prompt.IndexOf(start, StringComparison.Ordinal);
        var endIndex = prompt.IndexOf(end, StringComparison.Ordinal);
        startIndex.Should().BeGreaterThanOrEqualTo(0);
        endIndex.Should().BeGreaterThan(startIndex);
        return prompt[startIndex..endIndex];
    }

    private static bool HasPrerequisiteCycle(IReadOnlyDictionary<string, IReadOnlyList<string>> map)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool Visit(string node)
        {
            if (state.TryGetValue(node, out var current))
            {
                return current == 1;
            }

            state[node] = 1;
            if (map.TryGetValue(node, out var next))
            {
                foreach (var child in next)
                {
                    if (Visit(child))
                    {
                        return true;
                    }
                }
            }

            state[node] = 2;
            return false;
        }

        return map.Keys.Any(Visit);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DevPilot.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate DevPilot.sln");
    }

    private static void InitGit(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "Test");
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
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
    }

    private sealed class RecordingActivityRecorder : IExecutionActivityRecorder
    {
        public ConcurrentBag<ExecutionActivityMetadata?> Metadata { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ConcurrencyTracker : IAiProvider
    {
        public string ProviderName => "ConcurrencyTracker";
        private readonly int _delayMs;
        private int _active;
        public int MaxObservedConcurrency { get; private set; }

        public ConcurrencyTracker(int delayMs) => _delayMs = delayMs;

        public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _active);
            lock (this)
            {
                if (current > MaxObservedConcurrency)
                {
                    MaxObservedConcurrency = current;
                }
            }

            await Task.Delay(_delayMs, cancellationToken);
            Interlocked.Decrement(ref _active);
            var target = ExtractTarget(request.UserPrompt);
            var className = Path.GetFileNameWithoutExtension(target);
            return Success(target, $"export const {className} = 1;");
        }
    }

    private sealed class CountingContractProvider : IAiProvider
    {
        public string ProviderName => "CountingContract";
        public int CallCount { get; private set; }
        public List<string> StartOrder { get; } = new();
        public string? RoutesPrompt { get; private set; }
        public string? ControllerPrompt { get; private set; }

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var target = ExtractTarget(request.UserPrompt);
            StartOrder.Add(target);
            if (target.Contains("featureBRoutes", StringComparison.OrdinalIgnoreCase))
            {
                RoutesPrompt = request.UserPrompt;
            }

            if (target.Contains("featureBController", StringComparison.OrdinalIgnoreCase))
            {
                ControllerPrompt = request.UserPrompt;
            }

            if (target.Contains("Controller", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Success(target, $"export class FeatureBController {{ {ProducerContract} = true; get(id: string) {{ return id; }} }}"));
            }

            return Task.FromResult(Success(target, "export function registerFeatureBRoutes(app: any, controller: any) { app.get('/b/:id', controller.get); }"));
        }
    }

    private static string ExtractTarget(string? userPrompt)
    {
        var match = System.Text.RegularExpressions.Regex.Match(userPrompt ?? string.Empty, @"Target File: (.+)");
        return match.Success ? match.Groups[1].Value.Trim() : "File.ts";
    }

    private static AiResponse Success(string path, string content) => new()
    {
        IsSuccess = true,
        Content = $$"""{"filePath":{{System.Text.Json.JsonSerializer.Serialize(path)}},"action":"Create","newContent":{{System.Text.Json.JsonSerializer.Serialize(content)}}}"""
    };
}
