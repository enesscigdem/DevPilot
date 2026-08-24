using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class RepairConvergenceGapTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly RecordingActivityRecorder _activityRecorder;
    private readonly DeveloperAgent _agent;

    public RepairConvergenceGapTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotRepairGap_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/repair-gap";

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
    public void MaxCompileRepairAttempts_DefaultsToTwelve()
    {
        var options = new ExecutionReliabilityOptions();
        options.MaxCompileRepairAttempts.Should().Be(12);
        options.MaxCompileRepairRounds.Should().Be(12);
    }

    [Fact]
    public async Task MissingSearchMatch_TriggersOneAnchorRefreshRepairThatRereadsCurrentDiskContent()
    {
        const string current = "export const CURRENT_MARKER = 1;";
        WriteWorktree("src/services/issueService.ts", current);
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson(
            "src/services/issueService.ts",
            "export const STALE_ANCHOR = 99;",
            "export const CURRENT_MARKER = 2;"));
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson(
            "src/services/issueService.ts",
            current,
            "export const CURRENT_MARKER = 2;"));

        var result = await _agent.ExecuteFocusedRepairAsync(RepairRequest("src/services/issueService.ts"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
        _activityRecorder.ProviderKinds.Should().Equal("FocusedVerificationRepair", "AnchorRefreshRepair");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("Fresh Current Content");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain(current);
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("src/services/issueService.ts(1,1): error TS2322");
        _fakeAiProvider.ReceivedRequests[1].ReasoningEffort.Should().Be("low");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "services", "issueService.ts"))
            .Should().Contain("CURRENT_MARKER = 2");
    }

    [Fact]
    public async Task SecondAnchorFailure_StopsSafelyWithoutThirdCall()
    {
        WriteWorktree("src/app.ts", "export const value = 1;");
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", "export const staleA = 1;", "export const value = 2;"));
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", "export const staleB = 1;", "export const value = 2;"));
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", "export const value = 1;", "export const value = 2;"));

        var result = await _agent.ExecuteFocusedRepairAsync(RepairRequest("src/app.ts"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Missing search match");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
        _activityRecorder.ProviderKinds.Should().Equal("FocusedVerificationRepair", "AnchorRefreshRepair");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "app.ts")).Should().Be("export const value = 1;");
    }

    [Fact]
    public async Task FocusedVerificationRepair_TokenLimitExceeded_TriggersExactlyOneMicroDiagnosticRepair()
    {
        WriteWorktree("src/routes/issueRoutes.ts", "export const value = 1;");
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/routes/issueRoutes.ts", "export const value = 1;", "export const value = 2;"));

        var result = await _agent.ExecuteFocusedRepairAsync(RepairRequest("src/routes/issueRoutes.ts"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
        _activityRecorder.ProviderKinds.Should().Equal("FocusedVerificationRepair", "MicroDiagnosticRepair");
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().BeLessThanOrEqualTo(_fakeAiProvider.ReceivedRequests[0].MaxTokens ?? int.MaxValue);
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().Be(4096);
        _fakeAiProvider.ReceivedRequests[1].ReasoningEffort.Should().Be("low");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("Tightly Bounded Current Source Window");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("src/routes/issueRoutes.ts(1,1): error TS2322");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().NotContain("Peer File");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "routes", "issueRoutes.ts")).Should().Contain("value = 2");
    }

    [Fact]
    public async Task FinalDiagnosticTokenLimit_TriggersOneMicroDiagnosticRepairWithSameOrSmallerBudget()
    {
        WriteWorktree("src/app.ts", "export const value = 1;");
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", "export const value = 1;", "export const value = 2;"));

        var result = await _agent.ExecuteFocusedRepairAsync(RepairRequest("src/app.ts", isFinal: true));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
        _activityRecorder.ProviderKinds.Should().Equal("FinalDiagnosticRepair", "MicroDiagnosticRepair");
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().BeLessThanOrEqualTo(_fakeAiProvider.ReceivedRequests[0].MaxTokens ?? int.MaxValue);
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().Be(4096);
        _fakeAiProvider.ReceivedRequests[1].ReasoningEffort.Should().Be("low");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("MICRO DIAGNOSTIC REPAIR");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().NotContain("Peer File");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "app.ts")).Should().Contain("value = 2");
    }

    [Fact]
    public async Task MicroDiagnosticRepair_DoesNotAttemptAThirdRetry()
    {
        WriteWorktree("src/app.ts", "export const value = 1;");
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", "export const value = 1;", "export const value = 2;"));

        var result = await _agent.ExecuteFocusedRepairAsync(RepairRequest("src/app.ts", isFinal: true));

        result.Success.Should().BeFalse();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
        _activityRecorder.ProviderKinds.Should().Equal("FinalDiagnosticRepair", "MicroDiagnosticRepair");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "app.ts")).Should().Be("export const value = 1;");
    }

    [Fact]
    public void OrdinarySourceCreate_InitialAndRetryBudgetsAreCappedAt4096()
    {
        var files = new[]
        {
            "src/models/issue.ts",
            "src/services/issueService.ts",
            "src/repositories/issueRepository.ts",
            "src/controllers/issueController.ts",
            "src/routes/issueRoutes.ts"
        };

        foreach (var path in files)
        {
            var entry = new ManifestFileEntry(path, FileEditAction.Create);
            var initial = _agent.DetermineInitialBudget(path, FileEditAction.Create);
            initial.Should().Be(4096, path);
            _agent.DetermineCompactRetryBudget(initial, null, entry).Should().Be(4096, path);
            _agent.DetermineCompactRetryBudget(8192, null, entry).Should().Be(4096, path);
            DeveloperAgent.BuildSingleFileSystemPrompt(entry).Should().Contain("smallest compile-complete");
        }
    }

    [Fact]
    public void BoundFocusedRepair_ExcludesUnrelatedCompilerDiagnostics()
    {
        var request = MixedRepairRequest("src/app.ts");
        var bounded = DeveloperAgent.BoundFocusedRepairToSelectedFile(request, "src/app.ts");

        bounded.DiagnosticEvidence.Should().Contain("src/app.ts(12,3): error TS2322: Type 'string' is not assignable to type 'number'.");
        bounded.DiagnosticEvidence.Should().NotContain("issueController.ts");
        bounded.DiagnosticEvidence.Should().NotContain("issueRepository.ts");
        bounded.DiagnosticLocations.Should().ContainSingle(location => location.Contains("app.ts", StringComparison.OrdinalIgnoreCase));
        bounded.DiagnosticLocations.Should().NotContain(location => location.Contains("issueController.ts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FocusedVerificationRepair_UsesFileBoundedDiagnosticsOnly()
    {
        WriteWorktree("src/app.ts", "export const value = 1;");
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", "export const value = 1;", "export const value = 2;"));

        var result = await _agent.ExecuteFocusedRepairAsync(MixedRepairRequest("src/app.ts"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _activityRecorder.ProviderKinds.Should().Equal("FocusedVerificationRepair");
        AssertPromptIsFileBounded(_fakeAiProvider.ReceivedRequests[0].UserPrompt, "src/app.ts");
    }

    [Fact]
    public async Task FinalDiagnosticRepair_UsesFileBoundedDiagnosticsOnly()
    {
        WriteWorktree("src/app.ts", "export const value = 1;");
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", "export const value = 1;", "export const value = 2;"));

        var result = await _agent.ExecuteFocusedRepairAsync(MixedRepairRequest("src/app.ts", isFinal: true));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _activityRecorder.ProviderKinds.Should().Equal("FinalDiagnosticRepair");
        AssertPromptIsFileBounded(_fakeAiProvider.ReceivedRequests[0].UserPrompt, "src/app.ts");
        _fakeAiProvider.ReceivedRequests[0].UserPrompt.Should().Contain("FINAL DIAGNOSTIC-DIRECTED ATTEMPT");
    }

    [Fact]
    public async Task AnchorRefreshRepair_RemainsFileBounded()
    {
        const string current = "export const CURRENT_MARKER = 1;";
        WriteWorktree("src/app.ts", current);
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", "export const STALE_ANCHOR = 99;", "export const CURRENT_MARKER = 2;"));
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", current, "export const CURRENT_MARKER = 2;"));

        var result = await _agent.ExecuteFocusedRepairAsync(MixedRepairRequest("src/app.ts"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _activityRecorder.ProviderKinds.Should().Equal("FocusedVerificationRepair", "AnchorRefreshRepair");
        AssertPromptIsFileBounded(_fakeAiProvider.ReceivedRequests[0].UserPrompt, "src/app.ts");
        AssertPromptIsFileBounded(_fakeAiProvider.ReceivedRequests[1].UserPrompt, "src/app.ts");
    }

    [Fact]
    public async Task MicroDiagnosticRepair_RemainsFileBounded()
    {
        WriteWorktree("src/app.ts", "export const value = 1;");
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(TokenLimit());
        _fakeAiProvider.ResponsesToReturn.Enqueue(ModifyJson("src/app.ts", "export const value = 1;", "export const value = 2;"));

        var result = await _agent.ExecuteFocusedRepairAsync(MixedRepairRequest("src/app.ts"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _activityRecorder.ProviderKinds.Should().Equal("FocusedVerificationRepair", "MicroDiagnosticRepair");
        AssertPromptIsFileBounded(_fakeAiProvider.ReceivedRequests[0].UserPrompt, "src/app.ts");
        AssertPromptIsFileBounded(_fakeAiProvider.ReceivedRequests[1].UserPrompt, "src/app.ts");
    }

    [Fact]
    public void TestCreate_MayRetainBounded8192_AndModifyPatchFirstRemainsUnchanged()
    {
        var testEntry = new ManifestFileEntry("tests/issueService.test.ts", FileEditAction.Create);
        var modifyEntry = new ManifestFileEntry("src/services/issueService.ts", FileEditAction.Modify);

        _agent.DetermineInitialBudget(testEntry.FilePath, FileEditAction.Create).Should().Be(8192);
        _agent.DetermineCompactRetryBudget(8192, null, testEntry).Should().Be(8192);
        _agent.DetermineInitialBudget(modifyEntry.FilePath, FileEditAction.Modify, "export const value = 1;").Should().Be(4096);
        _agent.DetermineCompactRetryBudget(4096, "export const value = 1;", modifyEntry).Should().Be(4096);
        DeveloperAgent.BuildSingleFileSystemPrompt(modifyEntry).Should().Contain("searchReplaceEdits");
        DeveloperAgent.BuildSingleFileSystemPrompt(modifyEntry).Should().NotContain("complete resulting file once");
    }

    private static void AssertPromptIsFileBounded(string? prompt, string selectedFile)
    {
        prompt.Should().NotBeNull();
        prompt.Should().Contain($"{selectedFile}(12,3): error TS2322: Type 'string' is not assignable to type 'number'.");
        prompt.Should().NotContain("issueController.ts");
        prompt.Should().NotContain("issueRepository.ts");
        prompt.Should().NotContain("issueRoutes.ts");
        prompt.Should().NotContain("issueService.ts");
        prompt.Should().NotContain("jsonIssueStore.ts");
    }

    private FocusedRepairRequest MixedRepairRequest(string filePath, bool isFinal = false) => new(
        TaskId: Guid.NewGuid(),
        ExecutionId: Guid.NewGuid(),
        TaskTitle: "Fix file",
        AcceptanceCriteria: "Compile",
        WorkspacePath: _worktreeDir,
        BranchName: _branchName,
        RepairFiles: new[] { filePath },
        DiagnosticEvidence: """
            src/app.ts(12,3): error TS2322: Type 'string' is not assignable to type 'number'.
            src/controllers/issueController.ts(6,44): error TS2554: Expected 1 arguments, but got 2.
            src/repositories/issueRepository.ts(10,5): error TS2304: Cannot find name 'Issue'.
            src/routes/issueRoutes.ts(5,48): error TS2551: Property 'updateStatus' does not exist.
            src/services/issueService.ts(8,3): error TS2339: Property 'createIssue' does not exist.
            src/stores/jsonIssueStore.ts(4,1): error TS2307: Cannot find module './issue'.
            """,
        DiagnosticLocations: new[]
        {
            "src/app.ts:12:3",
            "src/controllers/issueController.ts:6:44",
            "src/repositories/issueRepository.ts:10:5",
            "src/routes/issueRoutes.ts:5:48",
            "src/services/issueService.ts:8:3",
            "src/stores/jsonIssueStore.ts:4:1"
        },
        IsFinalDiagnosticAttempt: isFinal);

    private FocusedRepairRequest RepairRequest(string filePath, bool isFinal = false) => new(
        TaskId: Guid.NewGuid(),
        ExecutionId: Guid.NewGuid(),
        TaskTitle: "Fix file",
        AcceptanceCriteria: "Compile",
        WorkspacePath: _worktreeDir,
        BranchName: _branchName,
        RepairFiles: new[] { filePath },
        DiagnosticEvidence: $"{filePath}(1,1): error TS2322: Type 'number' is not assignable to type 'string'.",
        DiagnosticLocations: new[] { $"{filePath}(1,1): error TS2322" },
        IsFinalDiagnosticAttempt: isFinal);

    private static string ModifyJson(string path, string search, string replace) =>
        $$"""{"filePath":"{{path}}","action":"Modify","searchReplaceEdits":[{"search":{{System.Text.Json.JsonSerializer.Serialize(search)}},"replace":{{System.Text.Json.JsonSerializer.Serialize(replace)}}}]}""";

    private static AiResponse TokenLimit() => new()
    {
        IsSuccess = false,
        FinishReason = "length",
        FailureKind = AiFailureKind.TokenLimitExceeded,
        ErrorMessage = "TokenLimitExceeded"
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
