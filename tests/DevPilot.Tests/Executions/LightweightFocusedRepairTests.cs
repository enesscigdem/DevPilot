using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure.DeveloperAgent;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class LightweightFocusedRepairTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;

    public LightweightFocusedRepairTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotFocusedRepair_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/repair-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        InitGitRepo(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _fakeAiProvider = new FakeAiProvider();
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
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
        catch { }
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_SingleFile_AppliesFocusedSearchReplaceSurgically()
    {
        var targetFile = Path.Combine(_worktreeDir, "src", "Calculator.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        File.WriteAllText(targetFile, "public class Calculator { public int Add(int a, int b) => a - b; }");

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance);

        var aiResponse = """{"filePath":"src/Calculator.cs","action":"Modify","searchReplaceEdits":[{"search":"a - b","replace":"a + b"}]}""";
        _fakeAiProvider.ResponsesToReturn.Enqueue(aiResponse);

        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix Add method",
            AcceptanceCriteria: "Add should sum numbers",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/Calculator.cs" },
            DiagnosticEvidence: "CalculatorTests.AddTest FAILED: Expected 4 got 0",
            DiagnosticLocations: new[] { "src/Calculator.cs:1:50" },
            Model: "test-model");

        var result = await agent.ExecuteFocusedRepairAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().ContainSingle(f => f == "src/Calculator.cs");

        var updatedContent = File.ReadAllText(targetFile);
        updatedContent.Should().Contain("a + b");
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_TwoCorrelatedFiles_RepairsBothSurgically()
    {
        var fileA = Path.Combine(_worktreeDir, "src", "ServiceA.cs");
        var fileB = Path.Combine(_worktreeDir, "src", "ServiceB.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(fileA)!);
        File.WriteAllText(fileA, "public class ServiceA { public string Get() => \"old\"; }");
        File.WriteAllText(fileB, "public class ServiceB { public string Value => \"old\"; }");

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance);

        var responseA = """{"filePath":"src/ServiceA.cs","action":"Modify","searchReplaceEdits":[{"search":"\"old\"","replace":"\"new\""}]}""";
        var responseB = """{"filePath":"src/ServiceB.cs","action":"Modify","searchReplaceEdits":[{"search":"\"old\"","replace":"\"new\""}]}""";

        _fakeAiProvider.ResponsesToReturn.Enqueue(responseA);
        _fakeAiProvider.ResponsesToReturn.Enqueue(responseB);

        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix Services",
            AcceptanceCriteria: "Return new",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/ServiceA.cs", "src/ServiceB.cs" },
            DiagnosticEvidence: "CS0103 error across ServiceA and ServiceB",
            Model: "test-model");

        var result = await agent.ExecuteFocusedRepairAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().HaveCount(2);

        File.ReadAllText(fileA).Should().Contain("\"new\"");
        File.ReadAllText(fileB).Should().Contain("\"new\"");
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_MissingTargetFile_ReturnsFailureWithoutCrashing()
    {
        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance);

        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix Missing",
            AcceptanceCriteria: null,
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/NonExistent.cs" },
            DiagnosticEvidence: "Error",
            Model: "test-model");

        var result = await agent.ExecuteFocusedRepairAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not exist");
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "DevPilot Tests");
        RunGit(path, "config", "user.email", "tests@devpilot.local");
    }

    private static void RunGit(string workingDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {err}");
        }
    }
}
