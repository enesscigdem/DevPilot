using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Domain.Constants;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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

        // File 1 response (ICalculator.cs - Interface layer 10 is generated first)
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "ICalculator.cs",
              "action": "Create",
              "newContent": "public interface ICalculator { int Add(int a, int b); }"
            }
            """);

        // File 2 response (Calculator.cs - Implementation layer 35 is generated second)
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
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
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix Add method in Calculator",
            TaskDescription: "Add method should perform addition, not subtraction.",
            AcceptanceCriteria: "Calculator.Add returns a + b",
            ImpactAnalysisSummary: "Impacts Calculator.cs",
            ProposedPlan: "Change - to + in Calculator.cs and add ICalculator.cs interface",
            ImpactedFilePaths: new[] { "Calculator.cs", "ICalculator.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ModifiedFiles.Should().HaveCount(2);

        // Zero manifest calls + 2 per-file edit calls = 2 total calls
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);

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
    public async Task GenerateAndApplyEditsAsync_MultiFileTask_DerivesManifestFromAnalysis_GeneratesSeparateFileEditsWithoutManifestAiCall()
    {
        // Setup 8 files
        var filePaths = new List<string>();
        var impactedDetails = new List<ImpactedFileDetail>();
        for (int i = 1; i <= 8; i++)
        {
            var relPath = $"src/App/File{i}.cs";
            filePaths.Add(relPath);
            impactedDetails.Add(new ImpactedFileDetail(relPath, "Modify", $"Update value in File{i}"));
            var fullPath = Path.Combine(_worktreeDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, $"public class File{i} {{ int V = 0; }}");
        }

        // Mock 8 separate per-file responses (NO manifest AI response needed!)
        for (int i = 1; i <= 8; i++)
        {
            var relPath = $"src/App/File{i}.cs";
            _fakeAiProvider.ResponsesToReturn.Enqueue($$"""
                {
                  "filePath": "{{relPath}}",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "int V = 0;",
                      "replace": "int V = {{i}};"
                    }
                  ]
                }
                """);
        }

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "8 File Refactor",
            TaskDescription: "Update values across 8 files",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: filePaths,
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: impactedDetails);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().HaveCount(8);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(8, "Zero manifest AI calls + 8 individual file generation calls");

        for (int i = 1; i <= 8; i++)
        {
            var content = await File.ReadAllTextAsync(Path.Combine(_worktreeDir, $"src/App/File{i}.cs"));
            content.Should().Contain($"int V = {i};");
        }
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_OnlyRelevantContextPassedToIndividualFileCall()
    {
        var fileA = Path.Combine(_worktreeDir, "src/Contracts/IService.cs");
        var fileB = Path.Combine(_worktreeDir, "src/App/Service.cs");
        var fileUnrelated = Path.Combine(_worktreeDir, "src/App/Unrelated.cs");

        Directory.CreateDirectory(Path.GetDirectoryName(fileA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(fileB)!);

        await File.WriteAllTextAsync(fileA, "public interface IService {}");
        await File.WriteAllTextAsync(fileB, "public class Service {}");
        await File.WriteAllTextAsync(fileUnrelated, "public class UnrelatedHugeContext {}");

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/App/Service.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "public class Service {}",
                  "replace": "public class Service : IService {}"
                }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Implement Service",
            TaskDescription: "Implement IService in Service.cs",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/Contracts/IService.cs", "src/App/Service.cs", "src/App/Unrelated.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("src/App/Service.cs", "Modify", "Implement IService")
            });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.ReceivedRequests.Should().HaveCount(1);

        // Single-file call for Service.cs
        var singleFileUserPrompt = _fakeAiProvider.ReceivedRequests[0].UserPrompt;
        singleFileUserPrompt.Should().Contain("src/App/Service.cs");
        singleFileUserPrompt.Should().NotContain("UnrelatedHugeContext", "Unrelated file content should NOT be included in single-file call");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_UnsafeManifestPath_RejectedBeforeEditGeneration()
    {
        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Unsafe Path",
            TaskDescription: "Escape workspace",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "../outside.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("outside the allowed execution workspace");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(0, "Path validation should fail before any AI calls are made");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_FailureOnFileN_NeverLeavesPartialEditsOnDisk()
    {
        var file1 = Path.Combine(_worktreeDir, "File1.cs");
        var file2 = Path.Combine(_worktreeDir, "File2.cs");
        const string initial1 = "public class File1 { int V = 0; }";
        const string initial2 = "public class File2 { int V = 0; }";

        await File.WriteAllTextAsync(file1, initial1);
        await File.WriteAllTextAsync(file2, initial2);

        // File 1 succeeds
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "File1.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "int V = 0;",
                  "replace": "int V = 100;"
                }
              ]
            }
            """);

        // File 2 returns malformed non-JSON that also fails repair
        _fakeAiProvider.ResponsesToReturn.Enqueue("MALFORMED_NON_JSON");
        _fakeAiProvider.ResponsesToReturn.Enqueue("STILL_MALFORMED");

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Atomic Task",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "File1.cs", "File2.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();

        // Disk must be completely untouched! File1 must NOT have been changed.
        (await File.ReadAllTextAsync(file1)).Should().Be(initial1, "File1 must remain untouched when File2 fails");
        (await File.ReadAllTextAsync(file2)).Should().Be(initial2, "File2 must remain untouched");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_FinishReasonLengthOnTargetFile_IdentifiesFailingFilePrecisely()
    {
        var file1 = Path.Combine(_worktreeDir, "LargeFile.cs");
        await File.WriteAllTextAsync(file1, "public class LargeFile {}");

        var customAiProvider = new FakeCustomAiProvider();
        customAiProvider.ResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            ErrorMessage = "AI response exhausted the configured output token limit before producing a complete result."
        });

        var agent = new DeveloperAgent(
            customAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "LargeFile.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("LargeFile.cs", "Error message must identify the specific file that hit length limit");
        result.ErrorMessage.Should().Contain("exhausted the configured output token limit");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_MaxManifestFilesExceeded_FailsFast()
    {
        var fileList = Enumerable.Range(1, 25).Select(i => $"File{i}.cs").ToList();

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Too many files",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: fileList,
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain($"{ExecutionCapacityPolicy.MaxImpactedFiles}");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(0, "Should fail fast before making any AI calls");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_IncompleteAnalysisWithoutFiles_ReturnsControlledDiagnostic()
    {
        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Empty Analysis Task",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: Array.Empty<string>(),
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("contains no impacted files");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_MaxGenerationCallsExceeded_ReturnsControlledFailure()
    {
        var customConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxGenerationCalls"] = "1" // Only allows 1 file call
            })
            .Build();

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            customConfig);

        var f1 = Path.Combine(_worktreeDir, "F1.cs");
        var f2 = Path.Combine(_worktreeDir, "F2.cs");
        await File.WriteAllTextAsync(f1, "class F1 {}");
        await File.WriteAllTextAsync(f2, "class F2 {}");

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "F1.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "class F1 {}", "replace": "class F1 { int X = 1; }" }]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Title",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "F1.cs", "F2.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeded maximum generation call limit");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_DeterministicDependencySorting_ProcessesContractsBeforeControllers()
    {
        var controllerPath = Path.Combine(_worktreeDir, "src/Api/Controllers/MyController.cs");
        var dtoPath = Path.Combine(_worktreeDir, "src/Application/Dtos/MyDto.cs");

        Directory.CreateDirectory(Path.GetDirectoryName(controllerPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dtoPath)!);

        await File.WriteAllTextAsync(controllerPath, "public class MyController {}");
        await File.WriteAllTextAsync(dtoPath, "public class MyDto {}");

        // DTO response (processed first due to sorting)
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/Application/Dtos/MyDto.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "public class MyDto {}", "replace": "public class MyDto { public int Id { get; set; } }" }]
            }
            """);

        // Controller response (processed second)
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/Api/Controllers/MyController.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "public class MyController {}", "replace": "public class MyController { public MyDto Get() => new(); }" }]
            }
            """);

        // Provide controller first in the input list
        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add DTO and Controller",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/Api/Controllers/MyController.cs", "src/Application/Dtos/MyDto.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.ReceivedRequests.Should().HaveCount(2);

        // Call 1 must be MyDto.cs because DTOs have lower layer score than Controllers
        _fakeAiProvider.ReceivedRequests[0].UserPrompt.Should().Contain("src/Application/Dtos/MyDto.cs");
        _fakeAiProvider.ReceivedRequests[1].UserPrompt.Should().Contain("src/Api/Controllers/MyController.cs");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_SingleFileExecution_WorksWithExactlyOneAiCall()
    {
        var targetFile = Path.Combine(_worktreeDir, "Service.cs");
        await File.WriteAllTextAsync(targetFile, "public class Service { public string Status = \"idle\"; }");

        _fakeAiProvider.ResponseToReturn = """
            {
              "filePath": "Service.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "\"idle\"",
                  "replace": "\"running\""
                }
              ]
            }
            """;

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update Status",
            TaskDescription: "Change idle to running",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Impacts Service.cs",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Service.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1, "Single-file task requires exactly 1 generation AI call");
        (await File.ReadAllTextAsync(targetFile)).Should().Contain("\"running\"");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_FileRepairUsesConfiguredFileBudget()
    {
        var targetFile = Path.Combine(_worktreeDir, "Service.cs");
        await File.WriteAllTextAsync(targetFile, "public class Service { public string Status = \"idle\"; }");

        // 1. File edit malformed
        _fakeAiProvider.ResponsesToReturn.Enqueue("INVALID_FILE_EDIT");
        // 2. File edit repaired
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Service.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "\"idle\"",
                  "replace": "\"running\""
                }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update Status",
            TaskDescription: "Change idle to running",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Impacts Service.cs",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Service.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.ReceivedRequests.Should().HaveCount(2);
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().Be(2048, "small-file Modify uses an expected full-file output budget");
        _fakeAiProvider.ReceivedRequests[1].MaxTokens.Should().Be(2048, "small-file applicability recovery keeps the same bounded budget");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_LargeConfiguredCeilings_DoNotInflateSmallModifyBudget()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxOutputTokens"] = "10000",
                ["DeveloperAgent:TokenBudgets:ModifyPatch"] = "15000"
            })
            .Build();

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
            NullLogger<DeveloperAgent>.Instance,
            config);

        var targetFile = Path.Combine(_worktreeDir, "Service.cs");
        await File.WriteAllTextAsync(targetFile, "public class Service { public string Status = \"idle\"; }");

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Service.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "\"idle\"",
                  "replace": "\"running\""
                }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update Status",
            TaskDescription: "Change idle to running",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Impacts Service.cs",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Service.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.ReceivedRequests.Should().HaveCount(1);
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().Be(2048, "small-file Modify budget is based on expected output, not the global ceiling");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_MissingSearchAnchor_DetectedImmediately_RepairsSuccessfullyWithOneCall()
    {
        var targetFile = Path.Combine(_worktreeDir, "Service.cs");
        await File.WriteAllTextAsync(targetFile, "public class Service { public int Counter = 0; }");

        // 1. Initial generation returns non-matching search anchor (wrong variable name)
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Service.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "public int Count = 0;",
                  "replace": "public int Count = 10;"
                }
              ]
            }
            """);

        // 2. Repair call returns corrected search anchor matching exact target file content
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Service.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "public int Counter = 0;",
                  "replace": "public int Counter = 10;"
                }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update Counter",
            TaskDescription: "Set Counter to 10",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Impacts Service.cs",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Service.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().HaveCount(1);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2, "1 initial generation call + 1 repair call");

        // Verify repair request prompt contained target content and exact validation error
        var repairUserPrompt = _fakeAiProvider.ReceivedRequests[1].UserPrompt;
        repairUserPrompt.Should().Contain("Missing search match in 'Service.cs'");
        repairUserPrompt.Should().Contain("public class Service { public int Counter = 0; }");

        var updated = await File.ReadAllTextAsync(targetFile);
        updated.Should().Be("public class Service { public int Counter = 10; }");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_MissingSearchAnchor_RepairFails_FailsImmediatelyWithoutGeneratingLaterFiles()
    {
        var f1 = Path.Combine(_worktreeDir, "src/Dtos/F1Dto.cs");
        var f2 = Path.Combine(_worktreeDir, "src/Controllers/F2Controller.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(f1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(f2)!);
        await File.WriteAllTextAsync(f1, "public class F1Dto { public int A = 1; }");
        await File.WriteAllTextAsync(f2, "public class F2Controller { public int B = 1; }");

        // F1 Call 1: invalid search anchor
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/Dtos/F1Dto.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "public int NonExistent = 1;", "replace": "public int A = 2;" }]
            }
            """);

        // F1 Call 2 (repair): still invalid search anchor
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/Dtos/F1Dto.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "public int StillWrong = 1;", "replace": "public int A = 2;" }]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Multi File Task",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "src/Dtos/F1Dto.cs", "src/Controllers/F2Controller.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("src/Dtos/F1Dto.cs", "Modify", "Update F1Dto"),
                new ImpactedFileDetail("src/Controllers/F2Controller.cs", "Modify", "Update F2Controller")
            });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Missing search match in 'src/Dtos/F1Dto.cs'");

        // F2 was NEVER generated because F1 failed early!
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2, "F1 initial + F1 repair; F2 must not be generated");

        // Disk must remain untouched
        (await File.ReadAllTextAsync(f1)).Should().Be("public class F1Dto { public int A = 1; }");
        (await File.ReadAllTextAsync(f2)).Should().Be("public class F2Controller { public int B = 1; }");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_AmbiguousDuplicateSearchAnchors_TriggersRepairAndFailsIfUnresolved()
    {
        var targetFile = Path.Combine(_worktreeDir, "Dup.cs");
        await File.WriteAllTextAsync(targetFile, "var x = 1;\nvar x = 1;");

        // Initial response with ambiguous search anchor
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Dup.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "var x = 1;", "replace": "var x = 2;" }]
            }
            """);

        // Repair response still ambiguous
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Dup.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "var x = 1;", "replace": "var x = 2;" }]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Ambiguous Task",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Dup.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Ambiguous multiple search matches (2)");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_RepairCountsTowardMaxGenerationCalls()
    {
        var customConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxGenerationCalls"] = "2" // Total 2 calls allowed
            })
            .Build();

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            customConfig);

        var f1 = Path.Combine(_worktreeDir, "F1.cs");
        var f2 = Path.Combine(_worktreeDir, "F2.cs");
        await File.WriteAllTextAsync(f1, "class F1 { int X = 1; }");
        await File.WriteAllTextAsync(f2, "class F2 { int Y = 1; }");

        // F1 Call 1: invalid search anchor
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "F1.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "int Wrong = 1;", "replace": "int X = 2;" }]
            }
            """);

        // F1 Call 2 (repair): valid search anchor (consumes call #2)
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "F1.cs",
              "action": "Modify",
              "searchReplaceEdits": [{ "search": "int X = 1;", "replace": "int X = 2;" }]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Max Calls Task",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "F1.cs", "F2.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("F1.cs", "Modify", "Update F1"),
                new ImpactedFileDetail("F2.cs", "Modify", "Update F2")
            });

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeded maximum generation call limit (2)");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_CrLfTarget_LfGeneratedSearch_SucceedsDeterministically()
    {
        var targetFile = Path.Combine(_worktreeDir, "Repo.cs");
        await File.WriteAllTextAsync(targetFile, "public class Repo\r\n{\r\n    public int Count = 0;\r\n}\r\n");

        // AI emits standard \n (LF) in JSON string for multiline search block
        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "Repo.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                {
                  "search": "{\n    public int Count = 0;\n}",
                  "replace": "{\n    public int Count = 100;\n}"
                }
              ]
            }
            """);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "CRLF / LF Compatibility",
            TaskDescription: "Update Count in Repo.cs",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Impacts Repo.cs",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Repo.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1, "Should succeed immediately without needing repair");

        var updated = await File.ReadAllTextAsync(targetFile);
        updated.Should().Contain("\r\n", "Original CRLF line endings should be preserved");
        updated.Should().Be("public class Repo\r\n{\r\n    public int Count = 100;\r\n}\r\n");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_AiProviderReturnsClassifiedError_SurfacesTargetFileAndProviderDiagnostic()
    {
        var targetFile = Path.Combine(_worktreeDir, "WorkspaceTaskActivityItemDto.cs");
        await File.WriteAllTextAsync(targetFile, "public class WorkspaceTaskActivityItemDto {}");

        var customProvider = new FakeCustomAiProvider();
        customProvider.ResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            StatusCode = 503,
            AttemptCount = 4,
            RequestId = "req-prod-503",
            ErrorMessage = "Kimi HTTP 503 after 4 attempts (RequestId: req-prod-503)."
        });

        var agent = new DeveloperAgent(
            customProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Observability pass",
            TaskDescription: "Update DTO",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "WorkspaceTaskActivityItemDto.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Kimi HTTP 503 after 4 attempts (RequestId: req-prod-503) while generating 'WorkspaceTaskActivityItemDto.cs'.");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_SmallFileModify_UsesHashGuardedFullReplacementInOneCall()
    {
        const string relativePath = "SmallService.cs";
        var targetFile = Path.Combine(_worktreeDir, relativePath);
        await File.WriteAllTextAsync(targetFile, "public class SmallService { public int Value => 1; }");

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "SmallService.cs",
              "action": "Modify",
              "newContent": "public class SmallService { public int Value => 2; }"
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateModifyRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().Be(2048);
        _fakeAiProvider.ReceivedRequests[0].SystemPrompt.Should().Contain("small-file Modify");
        _fakeAiProvider.ReceivedRequests[0].SystemPrompt.Should().Contain("newContent");
        (await File.ReadAllTextAsync(targetFile)).Should().Contain("Value => 2");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_LargeFileModify_KeepsSurgicalPatchContract()
    {
        const string relativePath = "LargeService.cs";
        var targetFile = Path.Combine(_worktreeDir, relativePath);
        var largeContent = string.Join('\n', Enumerable.Range(1, 120).Select(i => $"// line {i}")) +
                           "\npublic class LargeService { public int Value => 1; }\n";
        await File.WriteAllTextAsync(targetFile, largeContent);

        _fakeAiProvider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "LargeService.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "public int Value => 1;", "replace": "public int Value => 2;" }
              ]
            }
            """);

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateModifyRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().Be(4096);
        _fakeAiProvider.ReceivedRequests[0].SystemPrompt.Should().Contain("large-file Modify");
        _fakeAiProvider.ReceivedRequests[0].SystemPrompt.Should().Contain("searchReplaceEdits");
        (await File.ReadAllTextAsync(targetFile)).Should().Contain("Value => 2");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_ModifyCompactRetry_IsCappedBelow32768()
    {
        const string relativePath = "LargeRetryService.cs";
        var targetFile = Path.Combine(_worktreeDir, relativePath);
        var largeContent = string.Join('\n', Enumerable.Range(1, 120).Select(i => $"// line {i}")) +
                           "\npublic class LargeRetryService { public int Value => 1; }\n";
        await File.WriteAllTextAsync(targetFile, largeContent);

        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = """
                {
                  "filePath": "LargeRetryService.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    { "search": "public int Value => 1;", "replace": "public int Value => 2;" }
                  ]
                }
                """
        });

        var result = await _developerAgent.GenerateAndApplyEditsAsync(CreateModifyRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _fakeAiProvider.ReceivedRequests.Select(request => request.MaxTokens).Should().Equal(4096, 8192);
        _fakeAiProvider.ReceivedRequests.Should().OnlyContain(request => request.MaxTokens < 32768);
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_ApplicabilityRecovery_UsesCurrentWorktreeSource()
    {
        const string relativePath = "CurrentSourceService.cs";
        var targetFile = Path.Combine(_worktreeDir, relativePath);
        await File.WriteAllTextAsync(targetFile, "public class CurrentSourceService { public int Value => 1; }");
        var provider = new MutatingRepairAiProvider(targetFile);
        var agent = new DeveloperAgent(provider, _editApplier, NullLogger<DeveloperAgent>.Instance);

        var result = await agent.GenerateAndApplyEditsAsync(CreateModifyRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].UserPrompt.Should().Contain("Value => 10", "repair evidence must come from the current worktree snapshot");
        (await File.ReadAllTextAsync(targetFile)).Should().Contain("Value => 20");
    }

    private DeveloperAgentRequest CreateModifyRequest(string relativePath) => new(
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

    private sealed class FakeCustomAiProvider : IAiProvider
    {
        public string ProviderName => "FakeCustomAiProvider";
        public Queue<AiResponse> ResponsesToReturn { get; } = new();

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            if (ResponsesToReturn.Count > 0)
            {
                return Task.FromResult(ResponsesToReturn.Dequeue());
            }

            return Task.FromResult(new AiResponse { IsSuccess = false, ErrorMessage = "No responses configured" });
        }
    }

    private sealed class MutatingRepairAiProvider : IAiProvider
    {
        private readonly string _targetFile;
        private int _callCount;

        public MutatingRepairAiProvider(string targetFile)
        {
            _targetFile = targetFile;
        }

        public string ProviderName => "MutatingRepairProvider";
        public List<AiRequest> Requests { get; } = new();

        public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            _callCount++;

            if (_callCount == 1)
            {
                await File.WriteAllTextAsync(
                    _targetFile,
                    "public class CurrentSourceService { public int Value => 10; }",
                    cancellationToken);
                return new AiResponse
                {
                    IsSuccess = true,
                    Content = """
                        {
                          "filePath": "CurrentSourceService.cs",
                          "action": "Modify",
                          "searchReplaceEdits": [
                            { "search": "Value => 999", "replace": "Value => 20" }
                          ]
                        }
                        """
                };
            }

            return new AiResponse
            {
                IsSuccess = true,
                Content = """
                    {
                      "filePath": "CurrentSourceService.cs",
                      "action": "Modify",
                      "newContent": "public class CurrentSourceService { public int Value => 20; }"
                    }
                    """
            };
        }
    }
}
