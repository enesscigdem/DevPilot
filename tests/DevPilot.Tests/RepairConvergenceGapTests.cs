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
    public void MaxCompileRepairRounds_DefaultsToFive()
    {
        new ExecutionReliabilityOptions().MaxCompileRepairRounds.Should().Be(5);
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
