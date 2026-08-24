using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class IncrementalConvergenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly RecordingActivityRecorder _activityRecorder;
    private readonly DeveloperAgent _agent;

    public IncrementalConvergenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotIncremental_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/incremental";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);
        InitGitRepo(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "init");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _fakeAiProvider = new FakeAiProvider();
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _activityRecorder = new RecordingActivityRecorder();
        _agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            activityRecorder: _activityRecorder);
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
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task MechanicalGeneration_SetsLowReasoningEffort()
    {
        WriteWorktree("Service.cs", "public class Service { public int Value => 1; }");
        EnqueueModify("Service.cs", "public int Value => 1;", "public int Value => 2;");

        var result = await _agent.GenerateAndApplyEditsAsync(ModifyRequest("Service.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.ReceivedRequests.Should().ContainSingle();
        _fakeAiProvider.ReceivedRequests[0].ReasoningEffort.Should().Be("low");
    }

    [Fact]
    public async Task SurgicalModifyRetry_SetsLowReasoningEffort()
    {
        WriteWorktree("Service.cs", "public class Service { public int Value => 1; }");
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessModify(
            "Service.cs", "public int Value => 1;", "public int Value => 2;"));

        var result = await _agent.GenerateAndApplyEditsAsync(ModifyRequest("Service.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.ReceivedRequests.Should().HaveCount(2);
        _fakeAiProvider.ReceivedRequests[1].ReasoningEffort.Should().Be("low");
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().Be(4096);
        _activityRecorder.ProviderKinds.Should().Contain("SurgicalModifyRetry");
    }

    [Fact]
    public async Task FocusedVerificationRepair_SetsLowReasoningEffort()
    {
        WriteWorktree("src/app.ts", "export const value = 1;");
        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/app.ts","action":"Modify","searchReplaceEdits":[{"search":"export const value = 1;","replace":"export const value = 2;"}]}""");

        var result = await _agent.ExecuteFocusedRepairAsync(new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix app",
            AcceptanceCriteria: "value is 2",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/app.ts" },
            DiagnosticEvidence: "src/app.ts(1,1): error TS2322",
            DiagnosticLocations: new[] { "src/app.ts(1,1): error TS2322" }),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.ReceivedRequests[0].ReasoningEffort.Should().Be("low");
        _activityRecorder.ProviderKinds.Should().Contain("FocusedVerificationRepair");
    }

    [Fact]
    public async Task ProviderCallTelemetry_RecordsReasoningTokensAndContentCharCountWithoutStoringContent()
    {
        WriteWorktree("Service.cs", "public class Service { public int Value => 1; }");
        var content = """
            {
              "filePath": "Service.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "public int Value => 1;", "replace": "public int Value => 2;" }
              ]
            }
            """;
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            FinishReason = "stop",
            Content = content,
            OutputTokens = 4096,
            ReasoningTokens = 3800
        });

        var result = await _agent.GenerateAndApplyEditsAsync(ModifyRequest("Service.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        var metadata = _activityRecorder.Activities
            .Select(a => a.Metadata)
            .First(m => m?.EventKind == "ProviderCall");
        metadata!.ReasoningTokens.Should().Be(3800);
        metadata.ResponseContentCharCount.Should().Be(content.Length);
        metadata.RequestedReasoningEffort.Should().Be("low");
        metadata.OutputTokens.Should().Be(4096);

        foreach (var activity in _activityRecorder.Activities)
        {
            activity.Message.Should().NotContain("public int Value");
            activity.Message.Should().NotContain(content);
            if (activity.Metadata != null)
            {
                activity.Metadata.ToString().Should().NotContain("public int Value => 2");
            }
        }
    }

    [Fact]
    public void CreateBudgets_StayBoundedAndRetryDoesNotDouble()
    {
        var service = new ManifestFileEntry("src/Services/IssueService.cs", FileEditAction.Create);
        var controller = new ManifestFileEntry("src/Controllers/IssueController.cs", FileEditAction.Create);
        var testFile = new ManifestFileEntry("tests/IssueServiceTests.cs", FileEditAction.Create);

        var serviceBudget = _agent.DetermineInitialBudget(service.FilePath, FileEditAction.Create);
        var controllerBudget = _agent.DetermineInitialBudget(controller.FilePath, FileEditAction.Create);
        var testBudget = _agent.DetermineInitialBudget(testFile.FilePath, FileEditAction.Create);

        serviceBudget.Should().BeLessThanOrEqualTo(8192);
        controllerBudget.Should().BeLessThanOrEqualTo(8192);
        testBudget.Should().BeLessThanOrEqualTo(8192);

        _agent.DetermineCompactRetryBudget(serviceBudget, null, service).Should().Be(serviceBudget);
        _agent.DetermineCompactRetryBudget(8192, null, service).Should().Be(8192);
        _agent.DetermineCompactRetryBudget(8192, null, service).Should().BeLessThan(16384);
        _agent.DetermineCompactRetryBudget(16384, null, service).Should().BeLessThanOrEqualTo(8192);
        _agent.DetermineCompactRetryBudget(testBudget, null, testFile).Should().BeLessThanOrEqualTo(8192);
        _agent.DetermineCompactRetryBudget(testBudget, null, testFile).Should().BeLessThan(16384);
    }

    [Fact]
    public async Task CreateRetry_UsesSameBudgetAndMinimalCompileCompletePrompt()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            FinishReason = "stop",
            Content = """
                {
                  "filePath": "CreatedService.cs",
                  "action": "Create",
                  "newContent": "public class CreatedService {}"
                }
                """
        });

        var result = await _agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Create service",
            TaskDescription: "Add CreatedService",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Create CreatedService.cs",
            ProposedPlan: "Create CreatedService.cs",
            ImpactedFilePaths: new[] { "CreatedService.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail("CreatedService.cs", "Create", "Add service") }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().BeLessThanOrEqualTo(8192);
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().Be(_fakeAiProvider.ReceivedRequests[0].MaxTokens);
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().BeLessThan(16384);
        _fakeAiProvider.ReceivedRequests[1].ReasoningEffort.Should().Be("low");
        _fakeAiProvider.ReceivedRequests[1].SystemPrompt.Should().Contain("smallest compile-complete");
        _fakeAiProvider.ReceivedRequests[1].SystemPrompt.Should().Contain("newContent");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("MINIMAL CREATE RETRY");
        _activityRecorder.ProviderKinds.Should().Contain("MinimalCreateRetry");
        File.Exists(Path.Combine(_worktreeDir, "CreatedService.cs")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateSecondTokenLimit_StopsWithoutUnboundedRetryOrBudgetDoubling()
    {
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());

        var result = await _agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Create service",
            TaskDescription: "Add CreatedService",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Create CreatedService.cs",
            ProposedPlan: "Create CreatedService.cs",
            ImpactedFilePaths: new[] { "CreatedService.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[] { new ImpactedFileDetail("CreatedService.cs", "Create", "Add service") }));

        result.Success.Should().BeFalse();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().Be(_fakeAiProvider.ReceivedRequests[0].MaxTokens);
    }

    [Fact]
    public async Task ModifyMicroRetry_UsesBoundedWindowAfterSurgicalRetryStillTruncates()
    {
        WriteWorktree("Service.cs", "public class Service { public int Value => 1; }");
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessModify(
            "Service.cs", "public int Value => 1;", "public int Value => 2;"));

        var result = await _agent.GenerateAndApplyEditsAsync(ModifyRequest("Service.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(3);
        _fakeAiProvider.ReceivedRequests[2].ReasoningEffort.Should().Be("low");
        _fakeAiProvider.ReceivedRequests[2].MaxTokens.Should().Be(4096);
        _fakeAiProvider.ReceivedRequests[2].SystemPrompt.Should().Contain("MICRO repair");
        _fakeAiProvider.ReceivedRequests[2].SystemPrompt.Should().Contain("searchReplaceEdits");
        _fakeAiProvider.ReceivedRequests[2].SystemPrompt.Should().Contain("Omit 'newContent'");
        _activityRecorder.ProviderKinds.Should().Contain("MicroModifyRetry");
    }

    [Fact]
    public async Task TokenRecovery_DoesNotRegenerateCompletedFiles_AndKeepsVirtualWorkspace()
    {
        WriteWorktree("src/Contracts/IUserService.cs", "namespace Contracts;\npublic interface IUserService {}");
        WriteWorktree("src/Services/OrderService.cs", "namespace Services;\npublic class OrderService { public int Value => 1; }");

        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessModify(
            "src/Contracts/IUserService.cs",
            "public interface IUserService {}",
            "public interface IUserService { string GetRole(); }"));
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessModify(
            "src/Services/OrderService.cs",
            "public int Value => 1;",
            "public int Value => 2;"));

        var result = await _agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update contracts and service",
            TaskDescription: "Add GetRole and change Value",
            AcceptanceCriteria: "GetRole exists and Value is 2",
            ImpactAnalysisSummary: "Impacts both files",
            ProposedPlan: "Update IUserService then OrderService",
            ImpactedFilePaths: new[] { "src/Contracts/IUserService.cs", "src/Services/OrderService.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("src/Contracts/IUserService.cs", "Modify", "Add GetRole"),
                new ImpactedFileDetail("src/Services/OrderService.cs", "Modify", "Update Value")
            }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.ReceivedRequests.Count(r =>
            r.UserPrompt?.Contains("Target File: src/Contracts/IUserService.cs") == true).Should().Be(1);
        _fakeAiProvider.ReceivedRequests[2].UserPrompt.Should().Contain("GetRole");
        (await File.ReadAllTextAsync(Path.Combine(_worktreeDir, "src", "Contracts", "IUserService.cs")))
            .Should().Contain("GetRole");
    }

    [Fact]
    public async Task SequentialFocusedRepairs_ApplyFileAImmediately_AndKeepItWhenFileBFails()
    {
        WriteWorktree("src/ServiceA.cs", "public class ServiceA { public string Get() => \"old\"; }");
        WriteWorktree("src/ServiceB.cs", "public class ServiceB { public string Value => \"old\"; }");

        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/ServiceA.cs","action":"Modify","searchReplaceEdits":[{"search":"\"old\"","replace":"\"newA\""}]}""");
        var first = await _agent.ExecuteFocusedRepairAsync(new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix A",
            AcceptanceCriteria: "A returns newA",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/ServiceA.cs", "src/ServiceB.cs" },
            DiagnosticEvidence: "src/ServiceA.cs(1,1): error CS0103"),
            CancellationToken.None);

        first.Success.Should().BeTrue(first.ErrorMessage);
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "ServiceA.cs")).Should().Contain("newA");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "ServiceB.cs")).Should().Contain("old");

        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit("TokenLimitExceeded"));
        var second = await _agent.ExecuteFocusedRepairAsync(new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix B",
            AcceptanceCriteria: "B returns newB",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/ServiceB.cs" },
            DiagnosticEvidence: "src/ServiceB.cs(1,1): error CS0103"),
            CancellationToken.None);

        second.Success.Should().BeFalse();
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "ServiceA.cs")).Should().Contain("newA");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "ServiceB.cs")).Should().Contain("old");
    }

    [Fact]
    public void FinalDiagnosticAttemptPrompt_IsDiagnosticDirectedNotGenericTaskFix()
    {
        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Implement the whole feature",
            AcceptanceCriteria: "Do everything",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/app.ts" },
            DiagnosticEvidence: "src/app.ts(12,4): error TS2322: Type 'string' is not assignable to type 'number'.",
            DiagnosticLocations: new[] { "src/app.ts(12,4): error TS2322" },
            IsFinalDiagnosticAttempt: true);

        var system = DeveloperAgent.BuildFocusedDiagnosticRepairSystemPrompt("src/app.ts", isFinalDiagnosticAttempt: true);
        var user = DeveloperAgent.BuildFocusedDiagnosticRepairUserPrompt("src/app.ts", "export const n = 1;", request);

        system.Should().Contain("FINAL diagnostic-directed");
        system.Should().Contain("exact current compiler diagnostic");
        system.Should().Contain("Do not treat this as a generic fix-the-task request");
        user.Should().Contain("FINAL DIAGNOSTIC-DIRECTED ATTEMPT");
        user.Should().Contain("src/app.ts(12,4): error TS2322");
        user.Should().NotContain("Implement the whole feature");
        user.Should().NotContain("Do everything");
    }

    private DeveloperAgentRequest ModifyRequest(string relativePath) => new(
        TaskId: Guid.NewGuid(),
        ExecutionId: Guid.NewGuid(),
        TaskTitle: "Focused modify",
        TaskDescription: "Change Value from 1 to 2",
        AcceptanceCriteria: "Value returns 2",
        ImpactAnalysisSummary: $"Modify {relativePath}",
        ProposedPlan: $"Update {relativePath}",
        ImpactedFilePaths: new[] { relativePath },
        WorkspacePath: _worktreeDir,
        BranchName: _branchName,
        ImpactedFiles: new[] { new ImpactedFileDetail(relativePath, "Modify", "Update Value") });

    private void EnqueueModify(string path, string search, string replace) =>
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessModify(path, search, replace));

    private static AiResponse SuccessModify(string path, string search, string replace) => new()
    {
        IsSuccess = true,
        FinishReason = "stop",
        Content = $$"""
            {
              "filePath": "{{path}}",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": {{System.Text.Json.JsonSerializer.Serialize(search)}}, "replace": {{System.Text.Json.JsonSerializer.Serialize(replace)}} }
              ]
            }
            """
    };

    private static AiResponse TokenLimit(string? error = null) => new()
    {
        IsSuccess = false,
        FinishReason = "length",
        FailureKind = AiFailureKind.TokenLimitExceeded,
        ErrorMessage = error ?? "TokenLimitExceeded"
    };

    private void WriteWorktree(string relativePath, string content)
    {
        var full = Path.Combine(_worktreeDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void InitGitRepo(string path)
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
        public List<(string Message, ExecutionActivityMetadata? Metadata)> Activities { get; } = new();

        public IEnumerable<string?> ProviderKinds =>
            Activities.Where(a => a.Metadata?.EventKind == "ProviderCall").Select(a => a.Metadata!.ProviderCallKind);

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Activities.Add((message, metadata));
            return Task.CompletedTask;
        }
    }
}
