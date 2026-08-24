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

public sealed class CompletionFirstReliabilityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly DeveloperAgent _agent;

    public CompletionFirstReliabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotReliability_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/reliability";
        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);
        InitGit(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "init");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _fakeAiProvider = new FakeAiProvider();
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxConcurrentFileGenerations"] = "4"
            }).Build());
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
    public async Task CreateDoubleTruncation_FailsOnlyThatFile_WithTwoCalls()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());

        var result = await _agent.GenerateAndApplyEditsAsync(CreateRequest(["src/services/BrokenService.cs"]));

        result.Success.Should().BeFalse();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().Be(4096);
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().Be(2048);
    }

    [Fact]
    public async Task IndependentFiles_ContinueAfterOneFileFails()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessCreate("src/Dtos/Good.cs", "public class Good {}"));
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());

        var result = await _agent.GenerateAndApplyEditsAsync(CreateRequest(["src/Dtos/Good.cs", "src/Dtos/Bad.cs"]));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.IsGenerationComplete.Should().BeFalse();
        result.GenerationSummary!.SuccessCount.Should().Be(1);
        result.GenerationSummary.FailedCount.Should().Be(1);
        File.Exists(Path.Combine(_worktreeDir, "src", "Dtos", "Good.cs")).Should().BeTrue();
        File.Exists(Path.Combine(_worktreeDir, "src", "Dtos", "Bad.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task DirectDependent_BecomesBlockedByDependency_WhenPrerequisiteFails()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());

        var result = await _agent.GenerateAndApplyEditsAsync(CreateRequest([
            "src/models/issue.ts",
            "src/services/issueService.ts"
        ]));

        result.GenerationSummary!.BlockedByDependencyCount.Should().Be(1);
        result.GenerationSummary.FileOutcomes!
            .Single(outcome => outcome.FilePath == "src/services/issueService.ts")
            .Status.Should().Be(PlannedFileGenerationStatus.BlockedByDependency);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public async Task TransitiveDependent_BecomesBlockedByDependency()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());

        var result = await _agent.GenerateAndApplyEditsAsync(CreateRequest([
            "src/models/issue.ts",
            "src/services/issueService.ts",
            "src/controllers/issueController.ts"
        ]));

        result.GenerationSummary!.FailedCount.Should().Be(1);
        result.GenerationSummary.BlockedByDependencyCount.Should().Be(2);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public async Task SuccessfulEdits_AreRetainedInPartialResult()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessCreate("src/Dtos/A.cs", "public class A {}"));
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());

        var result = await _agent.GenerateAndApplyEditsAsync(CreateRequest(["src/Dtos/A.cs", "src/Dtos/B.cs"]));

        result.ModifiedFiles.Should().ContainSingle("src/Dtos/A.cs");
        result.GenerationSummary!.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task IncompleteManifest_IsNotGenerationComplete_EvenWhenWorktreeBuilds()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessCreate("src/Dtos/Only.cs", "public class Only {}"));

        var result = await _agent.GenerateAndApplyEditsAsync(CreateRequest(["src/Dtos/Only.cs", "src/Dtos/Missing.cs"]));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.IsGenerationComplete.Should().BeFalse();
        result.GenerationSummary!.PlannedFileCount.Should().Be(2);
        result.GenerationSummary.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task IncompleteUsefulWork_ReturnsIncompleteSuccess()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessCreate("src/Dtos/Useful.cs", "public class Useful {}"));
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());

        var result = await _agent.GenerateAndApplyEditsAsync(CreateRequest(["src/Dtos/Useful.cs", "src/Dtos/Missing.cs"]));

        result.Success.Should().BeTrue();
        result.HasUsefulWork.Should().BeTrue();
        result.IsGenerationComplete.Should().BeFalse();
    }

    [Fact]
    public async Task ZeroUsefulOutput_ReturnsFailed()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());

        var result = await _agent.GenerateAndApplyEditsAsync(CreateRequest(["src/Dtos/Nothing.cs"]));

        result.Success.Should().BeFalse();
        result.HasUsefulWork.Should().BeFalse();
    }

    [Fact]
    public void MinimalCreateRetry_IncludesSameRoleExemplarAndSizeHint()
    {
        SeedIssueLayer();
        var request = CreateRequest(["src/services/projectService.ts"]);
        var prompt = DeveloperAgent.BuildMinimalCreateRetryUserPrompt(
            request,
            new ManifestFileEntry("src/services/projectService.ts", FileEditAction.Create),
            contextFiles: new Dictionary<string, string>(),
            virtualWorkspace: new Dictionary<string, string>(),
            completedEdits: new Dictionary<string, FileEditSpec>());

        prompt.Should().Contain("Same-Role Repository Exemplar");
        prompt.Should().Contain("approximately");
        prompt.Should().Contain("source lines");
        prompt.Should().NotContain("ProposedPlan");
        prompt.Should().NotContain("Acceptance Criteria");
        prompt.Should().NotContain("Task Description");
    }

    [Fact]
    public void CreateBudgets_StayAt4096_AndRetryAt2048()
    {
        var entry = new ManifestFileEntry("src/Services/IssueService.cs", FileEditAction.Create);
        _agent.DetermineInitialBudget(entry.FilePath, FileEditAction.Create).Should().Be(4096);
        _agent.DetermineCompactRetryBudget(4096, null, entry).Should().Be(2048);
        _agent.DetermineCompactRetryBudget(8192, null, entry).Should().Be(2048);
        _agent.DetermineCompactRetryBudget(16384, null, entry).Should().Be(2048);
    }

    [Fact]
    public async Task TransientFailure_WithSingleProviderAttempt_GetsOneAgentRetry()
    {
        _fakeAiProvider.CustomHandler = (_, _) =>
        {
            if (_fakeAiProvider.SendAsyncCallCount == 1)
            {
                return Task.FromResult(new AiResponse
                {
                    IsSuccess = false,
                    FailureKind = AiFailureKind.TransientServiceUnavailable,
                    StatusCode = 503,
                    AttemptCount = 1
                });
            }

            return Task.FromResult(new AiResponse
            {
                IsSuccess = true,
                Content = """
                    {"filePath":"src/Dtos/Retry.cs","action":"Create","newContent":"public class Retry {}"}
                    """
            });
        };

        var result = await _agent.GenerateAndApplyEditsAsync(CreateRequest(["src/Dtos/Retry.cs"]));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public async Task DeterministicTruncation_DoesNotReceiveTransientRetry()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());

        await _agent.GenerateAndApplyEditsAsync(CreateRequest(["src/Dtos/Trunc.cs"]));

        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Cancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await _agent.GenerateAndApplyEditsAsync(CreateRequest(["src/Dtos/A.cs"]), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(0);
    }

    [Fact]
    public void ManifestGapRecovery_AddsAtMostTwoCreates()
    {
        var planned = new[] { new ManifestFileEntry("src/controllers/projectController.ts", FileEditAction.Create) };
        var generated = new Dictionary<string, string>
        {
            ["src/controllers/projectController.ts"] = """
                import { createProjectRouter } from '../routes/projectRoutes';
                export class ProjectController {}
                """
        };
        var diagnostics = new[]
        {
            "src/controllers/projectController.ts(1,39): error TS2307: Cannot find module '../routes/projectRoutes'."
        };

        var gaps = ManifestGapRecovery.DetectMissingLocalCreates(
            _worktreeDir,
            planned,
            generated.Keys.ToList(),
            generated,
            diagnostics);

        gaps.Should().HaveCount(1);
        gaps[0].FilePath.Should().Be("src/routes/projectRoutes.ts");
    }

    [Fact]
    public void ManifestGapRecovery_NeverCreatesExternalPackage()
    {
        var planned = new[] { new ManifestFileEntry("src/app.ts", FileEditAction.Create) };
        var generated = new Dictionary<string, string>
        {
            ["src/app.ts"] = "import express from 'express'; export default express();"
        };
        var diagnostics = new[] { "src/app.ts(1,21): error TS2307: Cannot find module 'express'." };

        ManifestGapRecovery.DetectMissingLocalCreates(
            _worktreeDir,
            planned,
            generated.Keys.ToList(),
            generated,
            diagnostics).Should().BeEmpty();
    }

    [Fact]
    public void ManifestGapRecovery_AmbiguousEvidence_DoesNotCreateFile()
    {
        var planned = new[] { new ManifestFileEntry("src/services/a.ts", FileEditAction.Create) };
        var generated = new Dictionary<string, string> { ["src/services/a.ts"] = "export const a = 1;" };
        ManifestGapRecovery.DetectMissingLocalCreates(
            _worktreeDir,
            planned,
            generated.Keys.ToList(),
            generated,
            ["some unrelated warning"]).Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionPath_RepositoryNativeConsumerWaitsForProducer()
    {
        SeedIssueLayer();
        var provider = new StartOrderProvider();
        var agent = new DeveloperAgent(
            provider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxConcurrentFileGenerations"] = "4"
            }).Build());

        var result = await agent.GenerateAndApplyEditsAsync(CreateTypedRequest(
            new ImpactedFileDetail("src/repositories/projectRepository.ts", "Create"),
            new ImpactedFileDetail("src/services/projectService.ts", "Create"),
            new ImpactedFileDetail("src/controllers/projectController.ts", "Create"),
            new ImpactedFileDetail("src/routes/projectRoutes.ts", "Create")));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.StartOrder.Should().ContainInOrder(
            "src/repositories/projectRepository.ts",
            "src/services/projectService.ts");
        provider.StartOrder.Should().ContainInOrder(
            "src/controllers/projectController.ts",
            "src/routes/projectRoutes.ts");
    }

    [Fact]
    public async Task IndependentChains_StillAllowConcurrencyAboveOne()
    {
        var tracker = new ConcurrencyTracker(30);
        var agent = new DeveloperAgent(
            tracker,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxConcurrentFileGenerations"] = "4"
            }).Build());

        await agent.GenerateAndApplyEditsAsync(CreateRequest([
            "src/Dtos/A.cs",
            "src/Dtos/B.cs",
            "src/Dtos/C.cs",
            "src/Dtos/D.cs"
        ]));

        tracker.MaxObservedConcurrency.Should().Be(4);
    }

    private void SeedIssueLayer()
    {
        WriteWorktree("src/services/issueService.ts", """
            import { IssueRepository } from '../repositories/issueRepository';
            export class IssueService { constructor(private repo: IssueRepository) {} }
            """);
        WriteWorktree("src/repositories/issueRepository.ts", "export class IssueRepository {}");
        WriteWorktree("src/routes/issueRoutes.ts", """
            import { IssueController } from '../controllers/issueController';
            export function registerIssueRoutes() {}
            """);
        WriteWorktree("src/controllers/issueController.ts", "export class IssueController {}");
    }

    private DeveloperAgentRequest CreateRequest(IReadOnlyList<string> files) => new(
        TaskId: Guid.NewGuid(),
        ExecutionId: Guid.NewGuid(),
        TaskTitle: "Generate",
        TaskDescription: "Desc",
        AcceptanceCriteria: null,
        ImpactAnalysisSummary: "Summary",
        ProposedPlan: "Plan",
        ImpactedFilePaths: files,
        WorkspacePath: _worktreeDir,
        BranchName: _branchName,
        ImpactedFiles: files.Select(path => new ImpactedFileDetail(path, "Create")).ToList());

    private DeveloperAgentRequest CreateTypedRequest(params ImpactedFileDetail[] files) => new(
        TaskId: Guid.NewGuid(),
        ExecutionId: Guid.NewGuid(),
        TaskTitle: "Generate",
        TaskDescription: "Desc",
        AcceptanceCriteria: null,
        ImpactAnalysisSummary: "Summary",
        ProposedPlan: "Plan",
        ImpactedFilePaths: files.Select(file => file.FilePath).ToArray(),
        WorkspacePath: _worktreeDir,
        BranchName: _branchName,
        ImpactedFiles: files.ToList());

    private void WriteWorktree(string relativePath, string content)
    {
        var full = Path.Combine(_worktreeDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static AiResponse TokenLimit() => new()
    {
        IsSuccess = false,
        FailureKind = AiFailureKind.TokenLimitExceeded,
        FinishReason = "length"
    };

    private static AiResponse SuccessCreate(string path, string content) => new()
    {
        IsSuccess = true,
        Content = $$"""{"filePath":"{{path}}","action":"Create","newContent":{{System.Text.Json.JsonSerializer.Serialize(content)}}}"""
    };

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

    private sealed class StartOrderProvider : IAiProvider
    {
        public string ProviderName => "StartOrder";
        public List<string> StartOrder { get; } = new();

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var path = ExtractPath(request.UserPrompt);
            StartOrder.Add(path);
            var className = Path.GetFileNameWithoutExtension(path);
            return Task.FromResult(new AiResponse
            {
                IsSuccess = true,
                Content = $$"""{"filePath":"{{path}}","action":"Create","newContent":"export class {{className}} {}"}"""
            });
        }

        private static string ExtractPath(string prompt)
        {
            const string marker = "Target File:";
            var idx = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return "unknown.ts";
            }

            return prompt[(idx + marker.Length)..].Trim().Split('\n')[0].Trim();
        }
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
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, current);
            }

            await Task.Delay(_delayMs, cancellationToken);
            Interlocked.Decrement(ref _active);
            var path = request.UserPrompt.Contains("Target File:", StringComparison.Ordinal)
                ? request.UserPrompt.Split('\n').First(line => line.Contains("Target File:", StringComparison.Ordinal))
                    .Replace("Target File:", string.Empty).Trim()
                : "src/Dtos/Item.cs";
            return SuccessCreate(path, "public class Item {}");
        }
    }
}
