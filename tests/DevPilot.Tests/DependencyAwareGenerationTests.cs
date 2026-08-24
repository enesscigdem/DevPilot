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

public sealed class DependencyAwareGenerationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly WorktreeEditApplier _editApplier;
    private readonly RecordingActivityRecorder _recorder;

    public DependencyAwareGenerationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotDepGen_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/dep-gen";
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
    public async Task FourIndependentFiles_MayGenerateConcurrently_WhenMaxIsFour()
    {
        var tracker = new ConcurrencyTracker(80);
        var agent = CreateAgent(tracker, concurrency: 4);
        var files = new[] { "src/Dtos/A.cs", "src/Dtos/B.cs", "src/Dtos/C.cs", "src/Dtos/D.cs" };

        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(files));

        result.Success.Should().BeTrue(result.ErrorMessage);
        tracker.MaxObservedConcurrency.Should().Be(4);
        tracker.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task PeakConcurrency_NeverExceedsConfiguredMaximum()
    {
        var tracker = new ConcurrencyTracker(40);
        var agent = CreateAgent(tracker, concurrency: 3);
        var files = Enumerable.Range(1, 6).Select(i => $"src/Dtos/Dto{i}.cs").ToArray();

        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(files));

        result.Success.Should().BeTrue(result.ErrorMessage);
        tracker.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(3);
        tracker.MaxObservedConcurrency.Should().Be(3);
    }

    [Fact]
    public void DependentService_WaitsForGeneratedModel()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/models/issue.ts", FileEditAction.Create),
            new ManifestFileEntry("src/services/issueService.ts", FileEditAction.Create)
        };

        GenerationDependencyAnalyzer.DependsOn(files[1], files[0], files).Should().BeTrue();
        GenerationDependencyAnalyzer.DependsOn(files[0], files[1], files).Should().BeFalse();
    }

    [Fact]
    public void Controller_WaitsForServiceContract()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/services/issueService.ts", FileEditAction.Create),
            new ManifestFileEntry("src/controllers/issueController.ts", FileEditAction.Create)
        };

        GenerationDependencyAnalyzer.DependsOn(files[1], files[0], files).Should().BeTrue();
        GenerationDependencyAnalyzer.DependsOn(files[0], files[1], files).Should().BeFalse();
    }

    [Fact]
    public void CompositionRoot_WaitsForGeneratedRouteFromImportEvidence()
    {
        var app = new ManifestFileEntry("src/app.ts", FileEditAction.Modify);
        var route = new ManifestFileEntry("src/routes/healthRoutes.ts", FileEditAction.Create);
        var contents = new Dictionary<string, string>
        {
            ["src/app.ts"] = """
                import express from 'express';
                import { registerIssueRoutes } from './routes/issueRoutes';
                const app = express();
                registerIssueRoutes(app);
                export default app;
                """
        };

        GenerationDependencyAnalyzer.DependsOn(app, route, new[] { app, route }, contents).Should().BeTrue();
        GenerationDependencyAnalyzer.DependsOn(route, app, new[] { app, route }, contents).Should().BeFalse();
    }

    [Fact]
    public void CompositionRoot_WaitsForGeneratedMiddlewareFromImportEvidence()
    {
        var app = new ManifestFileEntry("src/app.ts", FileEditAction.Modify);
        var middleware = new ManifestFileEntry("src/middleware/requestLogger.ts", FileEditAction.Create);
        var contents = new Dictionary<string, string>
        {
            ["src/app.ts"] = """
                import express from 'express';
                import { existingLogger } from './middleware/existingLogger';
                const app = express();
                app.use(existingLogger);
                export default app;
                """
        };

        GenerationDependencyAnalyzer.DependsOn(app, middleware, new[] { app, middleware }, contents).Should().BeTrue();
    }

    [Fact]
    public async Task DownstreamPrompt_SeesLatestVirtualWorkspaceContent()
    {
        var provider = new SequentialContractProvider();
        var agent = CreateAgent(provider, concurrency: 4);
        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(
            new[] { "src/models/issue.ts", "src/services/issueService.ts" }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.ServicePrompt.Should().Contain("UNIQUE_SYNTHESIZED_CONTRACT_v1");
        provider.StartOrder.Should().Equal("src/models/issue.ts", "src/services/issueService.ts");
    }

    [Fact]
    public async Task CompletionOrder_CannotUseStaleContract()
    {
        var provider = new SlowThenFastProvider();
        var agent = CreateAgent(provider, concurrency: 4);
        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(
            new[] { "src/models/issue.ts", "src/services/issueService.ts" }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.ServiceSawLatest.Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentFileFailure_PreservesCompletionFirstWorktreeSafety()
    {
        WriteWorktree("src/Dtos/Keep.cs", "public class Keep {}");
        var provider = new FailingAfterFirstProvider();
        var agent = CreateAgent(provider, concurrency: 4);
        var result = await agent.GenerateAndApplyEditsAsync(CreateTypedRequest(
            new ImpactedFileDetail("src/Dtos/Keep.cs", "Modify"),
            new ImpactedFileDetail("src/Dtos/Fail.cs", "Create"),
            new ImpactedFileDetail("src/Dtos/Other.cs", "Create")));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.IsGenerationComplete.Should().BeFalse();
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "Dtos", "Keep.cs"))
            .Should().Be("public class Keep { public int Id { get; set; } }");
        File.Exists(Path.Combine(_worktreeDir, "src", "Dtos", "Fail.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task FileLocalTokenRecovery_StillUsesSameBudgetAndKinds()
    {
        WriteWorktree("Service.cs", "public class Service { public int Value => 1; }");
        var fake = new FakeAiProvider();
        fake.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FailureKind = AiFailureKind.TokenLimitExceeded,
            FinishReason = "length",
            Content = string.Empty
        });
        fake.ResponsesToReturn.Enqueue(
            """{"filePath":"Service.cs","action":"Modify","searchReplaceEdits":[{"search":"public int Value => 1;","replace":"public int Value => 2;"}]}""");
        var agent = CreateAgent(fake, concurrency: 4);

        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(new[] { "Service.cs" }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        fake.ReceivedRequests.Should().HaveCount(2);
        fake.ReceivedRequests[0].MaxTokens.Should().Be(4096);
        fake.ReceivedRequests[1].MaxTokens.Should().Be(4096);
        _recorder.ProviderKinds.Should().Contain("Generation");
        _recorder.ProviderKinds.Should().Contain("SurgicalModifyRetry");
    }

    [Fact]
    public async Task OnlyTwoDependencySafeFiles_DoNotStartUnrelatedWorkToFillSlots()
    {
        var tracker = new ConcurrencyTracker(80);
        var agent = CreateAgent(tracker, concurrency: 4);
        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(new[]
        {
            "src/models/issue.ts",
            "src/services/issueService.ts"
        }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        tracker.MaxObservedConcurrency.Should().Be(1);
        tracker.StartedFiles.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadySlot_IsReusedImmediately()
    {
        var tracker = new ConcurrencyTracker(120);
        var agent = CreateAgent(tracker, concurrency: 4);
        var files = Enumerable.Range(1, 5).Select(i => $"src/Dtos/Item{i}.cs").ToArray();

        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(files));

        result.Success.Should().BeTrue(result.ErrorMessage);
        tracker.MaxObservedConcurrency.Should().Be(4);
        tracker.Starts.Should().HaveCount(5);
        var fifthStart = tracker.Starts.OrderBy(item => item.Started).ElementAt(4).Started;
        var firstFourEnds = tracker.Starts.OrderBy(item => item.Started).Take(4).Select(item => item.Ended).ToList();
        var earliestFirstBatchEnd = tracker.Starts.OrderBy(item => item.Started).Take(4).Min(item => item.Ended);
        fifthStart.Should().BeOnOrAfter(earliestFirstBatchEnd.AddMilliseconds(-15));
        firstFourEnds.Count(ended => ended > fifthStart).Should().BeGreaterThan(0, "a freed slot must start another ready file while other in-flight files still run");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public async Task ConfiguredConcurrency_CapsObservedParallelism(int configured, int expectedPeak)
    {
        var tracker = new ConcurrencyTracker(50);
        var agent = CreateAgent(tracker, concurrency: configured);
        var files = new[] { "src/Dtos/A.cs", "src/Dtos/B.cs", "src/Dtos/C.cs", "src/Dtos/D.cs" };

        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(files));

        result.Success.Should().BeTrue(result.ErrorMessage);
        tracker.MaxObservedConcurrency.Should().Be(expectedPeak);
    }

    [Fact]
    public async Task RouteThenAppTs_GeneratesWiringAfterRouteContract()
    {
        WriteWorktree("src/app.ts", """
            import express from 'express';
            import { registerIssueRoutes } from './routes/issueRoutes';
            const app = express();
            registerIssueRoutes(app);
            export default app;
            """);
        var provider = new SequentialContractProvider();
        var agent = CreateAgent(provider, concurrency: 4);
        var result = await agent.GenerateAndApplyEditsAsync(CreateTypedRequest(
            new ImpactedFileDetail("src/routes/healthRoutes.ts", "Create"),
            new ImpactedFileDetail("src/app.ts", "Modify")));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.StartOrder[0].Should().Be("src/routes/healthRoutes.ts");
        provider.StartOrder[1].Should().Be("src/app.ts");
        provider.AppPrompt.Should().Contain("healthRoutes");
    }

    [Fact]
    public async Task MiddlewareThenAppTs_GeneratesWiringAfterMiddlewareContract()
    {
        WriteWorktree("src/app.ts", """
            import express from 'express';
            import { existingLogger } from './middleware/existingLogger';
            const app = express();
            app.use(existingLogger);
            export default app;
            """);
        var provider = new SequentialContractProvider();
        var agent = CreateAgent(provider, concurrency: 4);
        var result = await agent.GenerateAndApplyEditsAsync(CreateTypedRequest(
            new ImpactedFileDetail("src/middleware/requestLogger.ts", "Create"),
            new ImpactedFileDetail("src/app.ts", "Modify")));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.StartOrder[0].Should().Be("src/middleware/requestLogger.ts");
        provider.StartOrder[1].Should().Be("src/app.ts");
    }

    [Fact]
    public async Task PerformanceTelemetry_RecordsTimingAndConcurrencyWithoutBodies()
    {
        var tracker = new ConcurrencyTracker(30);
        var agent = CreateAgent(tracker, concurrency: 4);
        var result = await agent.GenerateAndApplyEditsAsync(CreateRequest(new[]
        {
            "src/Dtos/One.cs",
            "src/Dtos/Two.cs"
        }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        var providerMeta = _recorder.Metadata.Where(m => m?.EventKind == "ProviderCall").ToList();
        providerMeta.Should().NotBeEmpty();
        providerMeta.Should().OnlyContain(m =>
            m!.QueueWaitMs != null &&
            m.ProviderDurationMs != null &&
            m.ConfiguredConcurrency == 4 &&
            m.ActiveGenerationCount > 0 &&
            m.InputPromptCharCount > 0 &&
            m.RequestedReasoningEffort == "low");
        var summary = _recorder.Metadata.Last(m => m?.EventKind == "GenerationSummary");
        summary!.PeakConcurrentGenerationCount.Should().BeGreaterThan(0);
        summary.SumProviderDurationMs.Should().BeGreaterThan(0);
        summary.GenerationCallCount.Should().BeGreaterThan(0);
        summary.LongestGenerationCalls.Should().NotBeNull();
        _recorder.Metadata.Should().OnlyContain(m =>
            m == null ||
            (m.TargetFile == null || !m.TargetFile.Contains("public class")) &&
            (m.LongestGenerationCalls == null || m.LongestGenerationCalls.All(item => !item.Contains("public class"))));
    }

    [Fact]
    public void DiagnosticLines_AreBoundedAndSanitized()
    {
        var lines = GenerationDependencyAnalyzer.SanitizeDiagnosticLinesForActivity(
            new[]
            {
                @"C:\Users\enesc\repo\src\app.ts(12,3): error TS2322: Type 'string' is not assignable to type 'number'.",
                @"C:\Users\enesc\repo\src\controllers\issueController.ts(6,44): error TS2554: Expected 1 arguments, but got 2.",
                "not a compiler line",
                "C:\\Users\\enesc\\repo\\src\\a.ts(1,1): error TS2304: Cannot find name 'x'.",
                "C:\\Users\\enesc\\repo\\src\\b.ts(1,1): error TS2304: Cannot find name 'y'.",
                "C:\\Users\\enesc\\repo\\src\\c.ts(1,1): error TS2304: Cannot find name 'z'.",
                "C:\\Users\\enesc\\repo\\src\\d.ts(1,1): error TS2304: Cannot find name 'w'."
            },
            @"C:\Users\enesc\repo");

        lines.Should().HaveCount(5);
        lines.Should().OnlyContain(line => !line.Contains(@"C:\Users\enesc\repo"));
        lines[0].Should().Contain("src/app.ts(12,3)");
        lines[0].Should().Contain("TS2322");
        lines[0].Should().Contain("Type 'string' is not assignable to type 'number'.");
        lines.Should().NotContain(line => line.Contains("not a compiler line"));
        lines.Should().OnlyContain(line => line.Length <= 240);
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

    private DeveloperAgentRequest CreateTypedRequest(params ImpactedFileDetail[] files) => new(
        TaskId: Guid.NewGuid(),
        ExecutionId: Guid.NewGuid(),
        TaskTitle: "Generate files",
        TaskDescription: "Desc",
        AcceptanceCriteria: null,
        ImpactAnalysisSummary: "Summary",
        ProposedPlan: "Plan",
        ImpactedFilePaths: files.Select(f => f.FilePath).ToArray(),
        WorkspacePath: _worktreeDir,
        BranchName: _branchName,
        ImpactedFiles: files);

    private void WriteWorktree(string relativePath, string content)
    {
        var full = Path.Combine(_worktreeDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
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
        public List<string?> ProviderKinds =>
            Metadata.Where(m => m?.EventKind == "ProviderCall").Select(m => m!.ProviderCallKind).ToList();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Metadata.Add(metadata);
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrencyTracker : IAiProvider
    {
        public string ProviderName => "ConcurrencyTracker";
        private readonly int _delayMs;
        private int _active;
        private int _starts;
        public int MaxObservedConcurrency { get; private set; }
        public ConcurrentBag<string> StartedFiles { get; } = new();
        public ConcurrentBag<(DateTime Started, DateTime Ended, string File)> Starts { get; } = new();

        public ConcurrencyTracker(int delayMs) => _delayMs = delayMs;

        public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _active);
            var startNumber = Interlocked.Increment(ref _starts);
            lock (this)
            {
                if (current > MaxObservedConcurrency)
                {
                    MaxObservedConcurrency = current;
                }
            }

            var target = ExtractTarget(request.UserPrompt);
            StartedFiles.Add(target);
            var started = DateTime.UtcNow;
            await Task.Delay(startNumber == 1 ? Math.Max(20, _delayMs / 4) : _delayMs, cancellationToken);
            var ended = DateTime.UtcNow;
            Starts.Add((started, ended, target));
            Interlocked.Decrement(ref _active);
            var className = Path.GetFileNameWithoutExtension(target);
            return Success(target, $"public class {className} {{}}");
        }
    }

    private sealed class SequentialContractProvider : IAiProvider
    {
        public string ProviderName => "SequentialContract";
        public List<string> StartOrder { get; } = new();
        public string? ServicePrompt { get; private set; }
        public string? AppPrompt { get; private set; }
        private readonly object _gate = new();

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var target = ExtractTarget(request.UserPrompt);
            lock (_gate)
            {
                StartOrder.Add(target);
                if (target.Contains("issueService", StringComparison.OrdinalIgnoreCase))
                {
                    ServicePrompt = request.UserPrompt;
                }

                if (target.Contains("app.ts", StringComparison.OrdinalIgnoreCase))
                {
                    AppPrompt = request.UserPrompt;
                }
            }

            if (target.EndsWith("issue.ts", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Success(target, "export interface Issue { id: string; UNIQUE_SYNTHESIZED_CONTRACT_v1: true; }"));
            }

            if (target.Contains("healthRoutes", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Success(target, "export function registerHealthRoutes(app: any) { app.get('/health', () => 'ok'); }"));
            }

            if (target.Contains("requestLogger", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Success(target, "export function requestLogger(req: any, res: any, next: any) { next(); }"));
            }

            if (target.EndsWith("app.ts", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Modify(target, "export default app;", "export default app;"));
            }

            return Task.FromResult(Success(target, $"export const {Path.GetFileNameWithoutExtension(target)} = 1;"));
        }
    }

    private sealed class SlowThenFastProvider : IAiProvider
    {
        public string ProviderName => "SlowThenFast";
        public bool ServiceSawLatest { get; private set; }

        public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var target = ExtractTarget(request.UserPrompt);
            if (target.Contains("issueService", StringComparison.OrdinalIgnoreCase))
            {
                ServiceSawLatest = request.UserPrompt?.Contains("UNIQUE_SYNTHESIZED_CONTRACT_v1") == true;
                return Success(target, "export class IssueService {}");
            }

            await Task.Delay(80, cancellationToken);
            return Success(target, "export interface Issue { UNIQUE_SYNTHESIZED_CONTRACT_v1: true }");
        }
    }

    private sealed class FailingAfterFirstProvider : IAiProvider
    {
        public string ProviderName => "FailingAfterFirst";
        private int _calls;

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var target = ExtractTarget(request.UserPrompt);
            Interlocked.Increment(ref _calls);
            if (target.Contains("Fail", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("simulated provider failure");
            }

            if (target.Contains("Keep", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Modify(target, "public class Keep {}", "public class Keep { public int Id { get; set; } }"));
            }

            return Task.FromResult(Success(target, $"public class {Path.GetFileNameWithoutExtension(target)} {{}}"));
        }
    }

    private static string ExtractTarget(string? userPrompt)
    {
        var match = System.Text.RegularExpressions.Regex.Match(userPrompt ?? string.Empty, @"Target File: (.+)");
        return match.Success ? match.Groups[1].Value.Trim() : "File.cs";
    }

    private static AiResponse Success(string path, string content) => new()
    {
        IsSuccess = true,
        Content = $$"""{"filePath":{{System.Text.Json.JsonSerializer.Serialize(path)}},"action":"Create","newContent":{{System.Text.Json.JsonSerializer.Serialize(content)}}}"""
    };

    private static AiResponse Modify(string path, string search, string replace) => new()
    {
        IsSuccess = true,
        Content = $$"""{"filePath":{{System.Text.Json.JsonSerializer.Serialize(path)}},"action":"Modify","searchReplaceEdits":[{"search":{{System.Text.Json.JsonSerializer.Serialize(search)}},"replace":{{System.Text.Json.JsonSerializer.Serialize(replace)}}}]}"""
    };
}
