using System.Diagnostics;
using System.Text;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class EditApplicabilityAndRepairTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly DeveloperAgent _developerAgent;

    public EditApplicabilityAndRepairTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotApplicabilityTests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/test-applicability-branch";

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
            // Ignore cleanup errors
        }
    }

    // 1. Valid exact search applies successfully
    [Fact]
    public void EditApplicability_ValidExactSearch_AppliesSuccessfully()
    {
        var original = "public class Calculator\n{\n    public int Add(int a, int b) => a - b;\n}";
        var edits = new[]
        {
            new SearchReplaceEdit("a - b", "a + b")
        };

        var result = WorktreeEditApplier.ValidateAndApplySearchReplaceEdits(original, edits, "Calculator.cs");

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ModifiedContent.Should().Be("public class Calculator\n{\n    public int Add(int a, int b) => a + b;\n}");
        result.TotalEdits.Should().Be(1);
    }

    // 2. Missing search is rejected before disk mutation
    [Fact]
    public async Task EditApplicability_MissingSearch_IsRejectedBeforeDiskMutation()
    {
        var targetFile = Path.Combine(_worktreeDir, "Handler.cs");
        var initialContent = "public class Handler { public void Execute() { DoWork(); } }";
        await File.WriteAllTextAsync(targetFile, initialContent);

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("Handler.cs", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("public void NonExistentMethod()", "public void UpdatedMethod()")
            })
        });

        var result = await _editApplier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Missing search match in 'Handler.cs'");
        result.ErrorMessage.Should().Contain("Edit block: 1/1");
        result.ErrorMessage.Should().Contain("Reason: search matched 0 times");
        result.ErrorMessage.Should().Contain("Failed SEARCH preview");

        // Verify disk file is completely unchanged
        (await File.ReadAllTextAsync(targetFile)).Should().Be(initialContent);
    }

    // 3. Ambiguous search (>1 match) remains rejected
    [Fact]
    public void EditApplicability_AmbiguousSearch_MoreThanOneMatch_RemainsRejected()
    {
        var content = "int count = 0;\nint count = 0;\n";
        var edits = new[]
        {
            new SearchReplaceEdit("int count = 0;", "int count = 10;")
        };

        var result = WorktreeEditApplier.ValidateAndApplySearchReplaceEdits(content, edits, "Counter.cs");

        result.Success.Should().BeFalse();
        result.MatchCount.Should().Be(2);
        result.FailedEditIndex.Should().Be(1);
        result.ErrorMessage.Should().Contain("Ambiguous multiple search matches (2) in 'Counter.cs'");
        result.ErrorMessage.Should().Contain("Reason: search matched 2 times");
    }

    // 4. Multiple search/replace blocks are evaluated sequentially against evolving in-memory content
    [Fact]
    public void EditApplicability_MultipleBlocks_EvaluatedSequentiallyAgainstEvolvingInMemoryContent()
    {
        var content = "step1();\nstep2();\nstep3();";
        var edits = new[]
        {
            new SearchReplaceEdit("step1();", "stepA();"),
            // Block 2 searches for text that was produced by Block 1
            new SearchReplaceEdit("stepA();\nstep2();", "stepAB();"),
            // Block 3 operates on resulting content
            new SearchReplaceEdit("step3();", "stepC();")
        };

        var result = WorktreeEditApplier.ValidateAndApplySearchReplaceEdits(content, edits, "Workflow.cs");

        result.Success.Should().BeTrue();
        result.ModifiedContent.Should().Be("stepAB();\nstepC();");
    }

    // 5. Failure in block N does not leak partial in-memory mutation into repair validation
    [Fact]
    public async Task EditApplicability_FailureInBlockN_DoesNotLeakPartialMutationIntoRepairValidation()
    {
        var targetFile = Path.Combine(_worktreeDir, "Pipeline.cs");
        var initialContent = "var x = 1;\nvar y = 2;\nvar z = 3;";
        await File.WriteAllTextAsync(targetFile, initialContent);

        // Initial response: block 1 matches "var x = 1;", but block 2 has nonexistent search "var y = 999;"
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Pipeline.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "var x = 1;", "replace": "var x = 10;" },
                { "search": "var y = 999;", "replace": "var y = 20;" }
              ]
            }
            """);

        // Repair response: block 1 matches "var x = 1;", block 2 matches original unmutated "var y = 2;"
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Pipeline.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "var x = 1;", "replace": "var x = 100;" },
                { "search": "var y = 2;", "replace": "var y = 200;" }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update Pipeline",
            TaskDescription: "Update x and y",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Impacts Pipeline.cs",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Pipeline.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);

        var updated = await File.ReadAllTextAsync(targetFile);
        updated.Should().Be("var x = 100;\nvar y = 200;\nvar z = 3;");
    }

    // 6. Repair request contains the exact current target source and failed search evidence
    [Fact]
    public async Task EditRepair_RequestContainsExactCurrentTargetSourceAndFailedSearchEvidence()
    {
        var targetFile = Path.Combine(_worktreeDir, "Service.cs");
        var initialContent = "public class Service\n{\n    public int Value = 42;\n}";
        await File.WriteAllTextAsync(targetFile, initialContent);

        // Initial generation: block 1 valid, block 2 fails (0 matches)
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Service.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "public class Service", "replace": "public sealed class Service" },
                { "search": "public int MissingField = 0;", "replace": "public int Value = 100;" }
              ]
            }
            """);

        // Repair generation: corrected edit
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Service.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "public class Service\n{\n    public int Value = 42;\n}", "replace": "public sealed class Service\n{\n    public int Value = 100;\n}" }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Repair Service",
            TaskDescription: "Update Service",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Service.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.ReceivedRequests.Should().HaveCount(2);

        var repairPrompt = _fakeAiProvider.ReceivedRequests[1].UserPrompt;
        repairPrompt.Should().Contain("Target File: Service.cs");
        repairPrompt.Should().Contain("Action: Modify");
        repairPrompt.Should().Contain("=== Applicability Failure Evidence ===");
        repairPrompt.Should().Contain("Failed Edit Block: 2 of 2");
        repairPrompt.Should().Contain("zero matches (the search text was not found in the current target file)");
        repairPrompt.Should().Contain("public int MissingField = 0;");
        repairPrompt.Should().Contain(
            "Edit Strategy: hash-guarded small-file replacement.");
        repairPrompt.Should().Contain(
            "Return complete resulting content once in newContent.");
        repairPrompt.Should().Contain(
            "Output ONLY the corrected small-file replacement JSON for 'Service.cs'.");
        repairPrompt.Should().Contain("=== Current Content of Target File ===");
        repairPrompt.Should().Contain("public class Service\n{\n    public int Value = 42;\n}");
    }

    // 7. Repaired manifest using an exact real search succeeds
    [Fact]
    public async Task EditRepair_RepairedManifestUsingExactRealSearch_Succeeds()
    {
        var targetFile = Path.Combine(_worktreeDir, "AppConfig.cs");
        await File.WriteAllTextAsync(targetFile, "public class AppConfig { public bool Enabled = false; }");

        // Initial fails
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "AppConfig.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "public bool IsEnabled = false;", "replace": "public bool Enabled = true;" }]
            }
            """);

        // Repair succeeds with verbatim copy
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "AppConfig.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "public bool Enabled = false;", "replace": "public bool Enabled = true;" }]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Enable AppConfig",
            TaskDescription: "Enable flag",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "AppConfig.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        (await File.ReadAllTextAsync(targetFile)).Should().Be("public class AppConfig { public bool Enabled = true; }");
    }

    // 8. Repaired manifest preserving a nonexistent/stale search fails immediately without build
    [Fact]
    public async Task EditRepair_RepairedManifestPreservingNonexistentSearch_FailsImmediatelyWithoutBuild()
    {
        var targetFile = Path.Combine(_worktreeDir, "GetWorkspaceOverviewQueryHandler.cs");
        var initialContent = """
            using DevPilot.Application.RepositoryWorkspaces.Ports;

            namespace DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceOverview;

            public sealed class GetWorkspaceOverviewQueryHandler
            {
                private readonly IWorkspaceOverviewReader _reader;
                public GetWorkspaceOverviewQueryHandler(IWorkspaceOverviewReader reader) => _reader = reader;
            }
            """;
        await File.WriteAllTextAsync(targetFile, initialContent);

        // Initial response with hallucinated method
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "GetWorkspaceOverviewQueryHandler.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "public async Task<OverviewDto> HandleAsync() { return await _db.Overview(); }", "replace": "public async Task<OverviewDto> HandleAsync() { return null; }" }
              ]
            }
            """);

        // Repair response repeats hallucinated search
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "GetWorkspaceOverviewQueryHandler.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "public async Task<OverviewDto> HandleAsync() { return await _db.Overview(); }", "replace": "public async Task<OverviewDto> HandleAsync() { return null; }" }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update Handler",
            TaskDescription: "Update Handler",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "GetWorkspaceOverviewQueryHandler.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("File edit validation failed for 'GetWorkspaceOverviewQueryHandler.cs' after repair:");
        result.ErrorMessage.Should().Contain("Missing search match in 'GetWorkspaceOverviewQueryHandler.cs'");
        result.ErrorMessage.Should().Contain("Edit block: 1/1");
        result.ErrorMessage.Should().Contain("Reason: search matched 0 times (zero matches)");

        // Exactly 2 AI calls (1 initial + 1 repair), no further retries
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);

        // Disk untouched
        (await File.ReadAllTextAsync(targetFile)).Should().Be(initialContent);
    }

    // 9. No disk files are changed if repaired applicability fails
    [Fact]
    public async Task EditRepair_FailedRepairedApplicability_NoDiskFilesAreChanged()
    {
        var f1 = Path.Combine(_worktreeDir, "src/F1.cs");
        var f2 = Path.Combine(_worktreeDir, "src/F2.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(f1)!);
        await File.WriteAllTextAsync(f1, "class F1 { public int A = 1; }");
        await File.WriteAllTextAsync(f2, "class F2 { public int B = 1; }");

        // F1 generated and valid
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/F1.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "int A = 1;", "replace": "int A = 2;" }]
            }
            """);

        // F2 initial invalid search
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/F2.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "int NonExistent = 1;", "replace": "int B = 2;" }]
            }
            """);

        // F2 repair still invalid
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/F2.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "int NonExistent2 = 1;", "replace": "int B = 2;" }]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Multi-file atomic safety",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/F1.cs", "src/F2.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("src/F1.cs", "Modify", "Update F1"),
                new ImpactedFileDetail("src/F2.cs", "Modify", "Update F2")
            });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        (await File.ReadAllTextAsync(f1)).Should().Be("class F1 { public int A = 1; }");
        (await File.ReadAllTextAsync(f2)).Should().Be("class F2 { public int B = 1; }");
    }

    // 10. CRLF target with LF search matches exactly and preserves CRLF output
    [Fact]
    public async Task EditApplicability_WindowsStyleCrlfSource_LfGeneratedSearch_ExactMatchingPreservesCrlf()
    {
        var targetFile = Path.Combine(_worktreeDir, "WindowsSource.cs");
        var crlfContent = "public class WindowsSource\r\n{\r\n    public int Counter = 0;\r\n}\r\n";
        await File.WriteAllTextAsync(targetFile, crlfContent);

        // AI returns \n in search and replace strings
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "WindowsSource.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "{\n    public int Counter = 0;\n}",
                  "replace": "{\n    public int Counter = 100;\n}"
                }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "CRLF Test",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "WindowsSource.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        var written = await File.ReadAllTextAsync(targetFile);
        written.Should().Contain("\r\n");
        written.Should().Be("public class WindowsSource\r\n{\r\n    public int Counter = 100;\r\n}\r\n");
    }

    // 11. Post-repair applicable output proceeds to worktree for compilation without pre-build semantic repair
    [Fact]
    public async Task EditRepair_PostRepairApplicableOutput_ProceedsToWorktreeForCompilation()
    {
        var targetFile = Path.Combine(_worktreeDir, "src/DevPilot.Application/MyQueryHandler.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

        var initialCode = """
            namespace DevPilot.Application;

            public sealed class MyQueryHandler
            {
                private readonly IMyService _service;
                public MyQueryHandler(IMyService service) => _service = service;
                public int Handle() => 1;
            }
            """;
        await File.WriteAllTextAsync(targetFile, initialCode);

        // Response 1: Creates IMyService contract with 2 parameters
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/DevPilot.Application/IMyService.cs",
              "action": "Create",
              "newContent": "namespace DevPilot.Application;\npublic interface IMyService { void Execute(int a, int b); }"
            }
            """);

        // Response 2: Initial attempt for MyQueryHandler fails applicability
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/DevPilot.Application/MyQueryHandler.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "public int NonExistent() => 0;", "replace": "public int Handle() => 2;" }
              ]
            }
            """);

        // Response 3: Repair response has valid search/replace applicability (code-correctness is deferred to build)
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/DevPilot.Application/MyQueryHandler.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "public int Handle() => 1;",
                  "replace": "public int Handle() { _service.Execute(1); return 1; }"
                }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Semantic Contract Guard",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/DevPilot.Application/IMyService.cs", "src/DevPilot.Application/MyQueryHandler.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().Contain("src/DevPilot.Application/MyQueryHandler.cs");

        // Target file receives the repaired applicable edits
        (await File.ReadAllTextAsync(targetFile)).Should().Contain("_service.Execute(1);");
    }

    // 12. Post-repair applicable output proceeds to worktree for compiler diagnostics
    [Fact]
    public async Task EditRepair_PostRepairApplicableOutput_ProceedsToWorktreeForCompilerDiagnostics()
    {
        var f1 = Path.Combine(_worktreeDir, "src/F1.cs");
        var f2 = Path.Combine(_worktreeDir, "src/F2.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(f1)!);
        await File.WriteAllTextAsync(f1, "namespace DevPilot.Application;\npublic class FirstClass {}");
        await File.WriteAllTextAsync(f2, "namespace DevPilot.Application;\npublic class SecondClass {}");

        // F1 creates a new type "SharedHelper"
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/F1.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "public class FirstClass {}",
                  "replace": "public class FirstClass {}\npublic class SharedHelper {}"
                }
              ]
            }
            """);

        // F2 initial fails search
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/F2.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "public class NonExistent {}", "replace": "public class SecondClass {}" }
              ]
            }
            """);

        // F2 repair applies cleanly
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/F2.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "public class SecondClass {}",
                  "replace": "public class SecondClass {}\npublic class SharedHelper {}"
                }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Duplicate Type Guard",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/F1.cs", "src/F2.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("src/F1.cs", "Modify", "Update F1"),
                new ImpactedFileDetail("src/F2.cs", "Modify", "Update F2")
            });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().Contain("src/F1.cs");
        result.ModifiedFiles.Should().Contain("src/F2.cs");
        (await File.ReadAllTextAsync(f2)).Should().Contain("public class SharedHelper {}");
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
