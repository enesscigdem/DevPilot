using System.Diagnostics;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class DeveloperAgentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly DeveloperAgent _developerAgent;

    public DeveloperAgentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotAgentTests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/agent-test-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        InitGitRepo(_originalRepoDir);

        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Original Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");

        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _fakeAiProvider = new FakeAiProvider();
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _developerAgent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance);
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
            // Ignore cleanup errors in temporary directory
        }
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_WithFakeAiProvider_AppliesEditsSuccessfully_NoRealAiNetworkCall()
    {
        var targetFile = Path.Combine(_worktreeDir, "Calculator.cs");
        await File.WriteAllTextAsync(targetFile, "public class Calculator { public int Add(int a, int b) => a - b; }");

        _fakeAiProvider.ResponseToReturn = """
            {
              "files": [
                {
                  "filePath": "Calculator.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "a - b",
                      "replace": "a + b"
                    }
                  ]
                },
                {
                  "filePath": "ICalculator.cs",
                  "action": "Create",
                  "newContent": "public interface ICalculator { int Add(int a, int b); }"
                }
              ]
            }
            """;

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix Add method in Calculator",
            TaskDescription: "Add method should perform addition, not subtraction.",
            AcceptanceCriteria: "Calculator.Add returns a + b",
            ImpactAnalysisSummary: "Impacts Calculator.cs",
            ProposedPlan: "Change - to + in Calculator.cs and add ICalculator.cs interface",
            ImpactedFilePaths: new[] { "Calculator.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ModifiedFiles.Should().HaveCount(2);

        // Verify Fake AI Provider was called instead of real network call
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);

        // Verify target file modified inside execution worktree
        var updatedContent = await File.ReadAllTextAsync(targetFile);
        updatedContent.Should().Be("public class Calculator { public int Add(int a, int b) => a + b; }");

        // Verify new file created inside execution worktree
        var newFileContent = await File.ReadAllTextAsync(Path.Combine(_worktreeDir, "ICalculator.cs"));
        newFileContent.Should().Be("public interface ICalculator { int Add(int a, int b); }");

        // Verify original repository remains completely unchanged
        File.Exists(Path.Combine(_originalRepoDir, "Calculator.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_OneMissingAndOneValidImpactedFile_SucceedsAndCallsAiProviderWithValidContext()
    {
        var targetFile = Path.Combine(_worktreeDir, "Calculator.cs");
        await File.WriteAllTextAsync(targetFile, "public class Calculator { public int Add(int a, int b) => a - b; }");

        _fakeAiProvider.ResponseToReturn = """
            {
              "files": [
                {
                  "filePath": "Calculator.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "a - b",
                      "replace": "a + b"
                    }
                  ]
                }
              ]
            }
            """;

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix Add method in Calculator",
            TaskDescription: "Add method should perform addition, not subtraction.",
            AcceptanceCriteria: "Calculator.Add returns a + b",
            ImpactAnalysisSummary: "Impacts Calculator.cs and a missing file",
            ProposedPlan: "Change - to + in Calculator.cs",
            ImpactedFilePaths: new[] { "NonExistentPlausibleFile.cs", "Calculator.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
        (await File.ReadAllTextAsync(targetFile)).Should().Be("public class Calculator { public int Add(int a, int b) => a + b; }");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_AllImpactedFilesMissing_ContinuesWithEmptyContextAndCallsAiProvider()
    {
        _fakeAiProvider.ResponseToReturn = """
            {
              "files": [
                {
                  "filePath": "NewApp.cs",
                  "action": "Create",
                  "newContent": "public class NewApp {}"
                }
              ]
            }
            """;

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix NonExistent Task",
            TaskDescription: "Task description",
            AcceptanceCriteria: "Criteria",
            ImpactAnalysisSummary: "Impacts non-existent files",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "NonExistent1.cs", "NonExistent2.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_AiProviderThrowsException_ReturnsControlledErrorWithoutExMessage()
    {
        var targetFile = Path.Combine(_worktreeDir, "App.cs");
        await File.WriteAllTextAsync(targetFile, "public class App {}");

        _fakeAiProvider.ExceptionToThrow = new InvalidOperationException("SECRET_API_KEY_EXPOSED_HTTP_500_INTERNAL_SERVER_ERROR");

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "App.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("AI provider request failed.");
        result.ErrorMessage.Should().NotContain("SECRET_API_KEY_EXPOSED_HTTP_500_INTERNAL_SERVER_ERROR");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_MalformedAiResponse_ReturnsControlledErrorWithoutExMessage()
    {
        var targetFile = Path.Combine(_worktreeDir, "App.cs");
        await File.WriteAllTextAsync(targetFile, "public class App {}");

        _fakeAiProvider.ResponseToReturn = "THIS IS NOT VALID JSON: { secret_key: '12345' }";

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "App.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("AI provider returned an invalid structured edit response.");
        result.ErrorMessage.Should().NotContain("secret_key");
        result.ErrorMessage.Should().NotContain("THIS IS NOT VALID JSON");
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
}
