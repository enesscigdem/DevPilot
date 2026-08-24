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

public sealed class CompletionFirstModifyGenerationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly RecordingActivityRecorder _activityRecorder;
    private readonly DeveloperAgent _agent;

    public CompletionFirstModifyGenerationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotCompletionFirst_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/completion-first";

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
    public void SmallAndLargeModify_UseSearchReplace_NotFullFileMode()
    {
        var small = "public class Small { public int Value => 1; }";
        var large = string.Join('\n', Enumerable.Range(1, 120).Select(i => $"// line {i}")) +
                    "\npublic class Large { public int Value => 1; }\n";
        WorktreeEditApplier.IsSmallTextFile(small).Should().BeTrue();
        WorktreeEditApplier.IsSmallTextFile(large).Should().BeFalse();

        var smallEntry = new ManifestFileEntry("Small.cs", FileEditAction.Modify);
        var largeEntry = new ManifestFileEntry("Large.cs", FileEditAction.Modify);

        foreach (var entry in new[] { smallEntry, largeEntry })
        {
            var prompt = DeveloperAgent.BuildSingleFileSystemPrompt(entry, useFullFileReplacement: false);
            prompt.Should().Contain("searchReplaceEdits");
            prompt.Should().Contain("existing-file Modify");
            prompt.Should().NotContain("small-file Modify");
            prompt.Should().NotContain("complete resulting file once");
        }

        var createPrompt = DeveloperAgent.BuildSingleFileSystemPrompt(
            new ManifestFileEntry("Created.cs", FileEditAction.Create));
        createPrompt.Should().Contain("newContent");
        createPrompt.Should().Contain("smallest compile-complete source file");
    }

    [Fact]
    public void ModifyBudgets_AreBoundedAndDoNotEscalate()
    {
        var small = "public class Small { public int Value => 1; }";
        var large = new string('a', 8000);
        var entry = new ManifestFileEntry("Service.cs", FileEditAction.Modify);

        var smallBudget = _agent.DetermineInitialBudget("Small.cs", FileEditAction.Modify, small);
        var largeBudget = _agent.DetermineInitialBudget("Large.cs", FileEditAction.Modify, large);
        smallBudget.Should().Be(4096);
        largeBudget.Should().Be(4096);

        var retry = _agent.DetermineCompactRetryBudget(smallBudget, large, entry, isRepair: false);
        retry.Should().Be(4096);
        retry.Should().BeLessThan(16384);
        retry.Should().BeLessThan(32768);

        _agent.DetermineFocusedRepairBudget().Should().Be(4096);
        _agent.DetermineCompactRetryBudget(4096, large, entry, isRepair: true).Should().Be(4096);
    }

    [Fact]
    public async Task ExistingSmallModify_UsesSearchReplaceAndOneProviderCall()
    {
        WriteWorktree("SmallService.cs", "public class SmallService { public int Value => 1; }");
        EnqueueModify("SmallService.cs", "public int Value => 1;", "public int Value => 2;");

        var result = await _agent.GenerateAndApplyEditsAsync(ModifyRequest("SmallService.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().Be(4096);
        _fakeAiProvider.ReceivedRequests[0].SystemPrompt.Should().Contain("searchReplaceEdits");
        _fakeAiProvider.ReceivedRequests[0].SystemPrompt.Should().NotContain("small-file Modify");
        (await File.ReadAllTextAsync(Path.Combine(_worktreeDir, "SmallService.cs"))).Should().Contain("Value => 2");
    }

    [Fact]
    public async Task CreateStillUsesNewContent()
    {
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "CreatedService.cs",
              "action": "Create",
              "newContent": "public class CreatedService {}"
            }
            """);

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
        _fakeAiProvider.ReceivedRequests[0].SystemPrompt.Should().Contain("newContent");
        File.Exists(Path.Combine(_worktreeDir, "CreatedService.cs")).Should().BeTrue();
    }

    [Fact]
    public async Task TokenLimitOnOneModify_TriggersFileLocalSurgicalRetry_AndPreservesCompletedFiles()
    {
        WriteWorktree("src/Contracts/IUserService.cs", "namespace Contracts;\npublic interface IUserService {}");
        WriteWorktree("src/Services/OrderService.cs", "namespace Services;\npublic class OrderService { public int Value => 1; }");

        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessModify(
            "src/Contracts/IUserService.cs",
            "public interface IUserService {}",
            "public interface IUserService { string GetRole(); }"));
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            OutputTokens = 4096
        });
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
        _fakeAiProvider.SendAsyncCallCount.Should().Be(3);
        _fakeAiProvider.ReceivedRequests[0].UserPrompt.Should().Contain("IUserService.cs");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("OrderService.cs");
        _fakeAiProvider.ReceivedRequests[2].UserPrompt.Should().Contain("OrderService.cs");
        _fakeAiProvider.ReceivedRequests[2].MaxTokens.Should().Be(4096);
        _fakeAiProvider.ReceivedRequests[2].SystemPrompt.Should().Contain("searchReplaceEdits");
        _fakeAiProvider.ReceivedRequests[2].SystemPrompt.Should().Contain("NEVER reproduce unchanged methods");
        _fakeAiProvider.ReceivedRequests.Count(r =>
            (r.UserPrompt?.Contains("IUserService.cs") ?? false) &&
            (r.SystemPrompt?.Contains("IUserService.cs") ?? false)).Should().Be(1);

        var kinds = _activityRecorder.Activities
            .Where(a => a.Metadata?.EventKind == "ProviderCall")
            .Select(a => a.Metadata!.ProviderCallKind)
            .ToList();
        kinds.Should().Contain("Generation");
        kinds.Should().Contain("SurgicalModifyRetry");

        (await File.ReadAllTextAsync(Path.Combine(_worktreeDir, "src", "Contracts", "IUserService.cs")))
            .Should().Contain("GetRole");
        (await File.ReadAllTextAsync(Path.Combine(_worktreeDir, "src", "Services", "OrderService.cs")))
            .Should().Contain("Value => 2");
    }

    [Fact]
    public async Task SecondTokenLimitFailure_TerminatesWithoutUnboundedRetry()
    {
        WriteWorktree("Service.cs", "public class Service { public int Value => 1; }");
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });

        var result = await _agent.GenerateAndApplyEditsAsync(ModifyRequest("Service.cs"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exhausted the configured");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(3, "Modify allows surgical retry plus one micro retry, then stops");
        (await File.ReadAllTextAsync(Path.Combine(_worktreeDir, "Service.cs"))).Should().Contain("Value => 1");
    }

    [Fact]
    public void ApplicabilityAndFocusedRepair_RemainSearchReplace()
    {
        var entry = new ManifestFileEntry("src/app.ts", FileEditAction.Modify);
        DeveloperAgent.BuildSingleFileRepairSystemPrompt(entry).Should().Contain("searchReplaceEdits");
        DeveloperAgent.BuildSingleFileRepairSystemPrompt(entry).Should().NotContain("complete resulting small file");
        DeveloperAgent.BuildFocusedDiagnosticRepairSystemPrompt("src/app.ts").Should().Contain("NEVER return 'newContent'");
        _agent.DetermineFocusedRepairBudget().Should().Be(4096);
    }

    [Fact]
    public async Task SurgicalRetry_KeepsVirtualWorkspaceContracts()
    {
        WriteWorktree("src/Contracts/IUserProfileService.cs", "namespace Contracts;\npublic interface IUserProfileService {}");
        WriteWorktree("src/Services/OrderService.cs", "namespace Services;\nusing Contracts;\npublic class OrderService {\n    public string Check(IUserProfileService profile) => \"ok\";\n}");

        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessModify(
            "src/Contracts/IUserProfileService.cs",
            "public interface IUserProfileService {}",
            "public interface IUserProfileService { string GetRole(); }"));
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(SuccessModify(
            "src/Services/OrderService.cs",
            "=> \"ok\";",
            "=> profile.GetRole();"));

        var result = await _agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Propagate GetRole",
            TaskDescription: "Use the new contract",
            AcceptanceCriteria: "OrderService uses GetRole",
            ImpactAnalysisSummary: "Impacts both files",
            ProposedPlan: "Update contract then service",
            ImpactedFilePaths: new[] { "src/Contracts/IUserProfileService.cs", "src/Services/OrderService.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("src/Contracts/IUserProfileService.cs", "Modify", "Add GetRole"),
                new ImpactedFileDetail("src/Services/OrderService.cs", "Modify", "Use GetRole")
            }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        var retryPrompt = _fakeAiProvider.ReceivedRequests[2].UserPrompt;
        retryPrompt.Should().Contain("IUserProfileService");
        retryPrompt.Should().Contain("GetRole");
    }

    [Fact]
    public void PathIntegrity_AndSmallFilePredicate_RemainUnchanged()
    {
        var act = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, "../secret.txt");
        act.Should().Throw<InvalidOperationException>();
        WorktreeEditApplier.IsSmallTextFile("public class Small {}").Should().BeTrue();
    }

    [Fact]
    public void FocusedRepairWindow_KeepsSmallFilesIntact_AndWindowsLargeFiles()
    {
        var small = "export function createIssue(input: CreateIssueInput) {\n  return input;\n}\n";
        DeveloperAgent.BuildFocusedRepairTargetWindow(
            small,
            new[] { "src/controllers/issueController.ts(2,3): error TS2554" })
            .Should().Contain("createIssue");

        var large = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"const line{i} = {i};"));
        var window = DeveloperAgent.BuildFocusedRepairTargetWindow(
            large,
            new[] { "src/file.ts(150,1): error TS2322" });
        window.Should().Contain("line150");
        window.Should().Contain("unrelated methods omitted");
        window.Should().NotContain("const line1 = 1;");
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
