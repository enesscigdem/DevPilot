using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class ModifyAndTestVerificationTests : IDisposable
{
    private readonly string _workspace;

    public ModifyAndTestVerificationTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "DevPilotModifyVerify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        InitGitRepo(_workspace);
        RunGit(_workspace, "checkout", "-b", "devpilot/modify-verify");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void SmallModify_DoesNotAutomaticallyRequireCompleteFileNewContent()
    {
        var smallContent = """
            public class ApplicationDbContext
            {
                public int Value => 1;
            }
            """;
        WorktreeEditApplier.IsSmallTextFile(smallContent).Should().BeTrue();
        DeveloperAgent.ShouldUseFullFileReplacement(FileEditAction.Modify, smallContent).Should().BeFalse();

        var spec = new FileEditSpec(
            "ApplicationDbContext.cs",
            FileEditAction.Modify,
            "public class ApplicationDbContext { public int Value => 2; }",
            null);
        var entry = new ManifestFileEntry("ApplicationDbContext.cs", FileEditAction.Modify);

        var act = () => DeveloperAgent.ValidateSingleFileEditSpec(spec, entry, smallContent, useFullFileReplacement: false);
        act.Should().Throw<FormatException>().WithMessage("*must use surgical 'searchReplaceEdits'*");
    }

    [Fact]
    public void SmallModify_AcceptsCompactSearchReplace()
    {
        var smallContent = "public class IssueRepository { public int Value => 1; }";
        var spec = new FileEditSpec(
            "issueRepository.ts",
            FileEditAction.Modify,
            null,
            new List<SearchReplaceEdit> { new("public int Value => 1;", "public int Value => 2;") });
        var entry = new ManifestFileEntry("issueRepository.ts", FileEditAction.Modify);

        var act = () => DeveloperAgent.ValidateSingleFileEditSpec(spec, entry, smallContent);
        act.Should().NotThrow();
    }

    [Fact]
    public void NearEmptyModify_IsTheOnlyFullReplacementNiche()
    {
        var nearEmpty = "public class Stub {}";
        nearEmpty.Trim().Length.Should().BeLessThanOrEqualTo(DeveloperAgent.NearEmptyModifyReplacementChars);
        DeveloperAgent.ShouldUseFullFileReplacement(FileEditAction.Modify, nearEmpty).Should().BeTrue();
        DeveloperAgent.ShouldUseFullFileReplacement(FileEditAction.Modify, "public class ApplicationDbContext { public int Value => 1; }").Should().BeFalse();
        DeveloperAgent.ShouldUseFullFileReplacement(FileEditAction.Create, nearEmpty).Should().BeFalse();

        var spec = new FileEditSpec("Stub.cs", FileEditAction.Modify, "public class Stub { public int X => 1; }", null);
        var entry = new ManifestFileEntry("Stub.cs", FileEditAction.Modify);
        var act = () => DeveloperAgent.ValidateSingleFileEditSpec(spec, entry, nearEmpty, useFullFileReplacement: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void ModifyBudgets_StayOnPatchContract_WithOneRetryAndNo16K()
    {
        var agent = CreateAgent();
        var small = "public class IssueService { public int Value => 1; }";
        var large = string.Join('\n', Enumerable.Range(1, 120).Select(i => $"// line {i}"));
        var entry = new ManifestFileEntry("issueService.ts", FileEditAction.Modify);

        agent.DetermineInitialBudget("issueService.ts", FileEditAction.Modify, small).Should().Be(4096);
        agent.DetermineInitialBudget("Program.cs", FileEditAction.Modify, large).Should().Be(4096);
        agent.DetermineFocusedRepairBudget().Should().Be(4096);

        var retry = agent.DetermineCompactRetryBudget(4096, small, entry);
        retry.Should().Be(8192);
        retry.Should().BeLessThanOrEqualTo(8192);
        retry.Should().BeLessThan(16384);

        var capped = agent.DetermineCompactRetryBudget(8192, large, entry);
        capped.Should().Be(8192);

        var inflated = CreateAgent(new Dictionary<string, string?>
        {
            ["DeveloperAgent:MaxOutputTokens"] = "32768",
            ["DeveloperAgent:MaxCompactRetryOutputTokens"] = "24576",
            ["DeveloperAgent:TokenBudgets:ModifyPatch"] = "15000"
        });
        inflated.DetermineInitialBudget("Program.cs", FileEditAction.Modify, small).Should().Be(8192);
        inflated.DetermineCompactRetryBudget(8192, small, entry).Should().Be(8192);
    }

    [Fact]
    public void CreateCategoryBudgets_RemainUnchanged()
    {
        var agent = CreateAgent();
        agent.DetermineInitialBudget("src/Orders/OrderDto.cs", FileEditAction.Create).Should().Be(3276);
        agent.DetermineInitialBudget("src/Orders/IOrderService.cs", FileEditAction.Create).Should().Be(3276);
        agent.DetermineInitialBudget("src/Orders/GetOrderQuery.cs", FileEditAction.Create).Should().Be(3276);
        agent.DetermineInitialBudget("src/Orders/OrderService.cs", FileEditAction.Create).Should().Be(6553);
        agent.DetermineInitialBudget("src/Orders/OrdersController.cs", FileEditAction.Create).Should().Be(6553);
        agent.DetermineInitialBudget("tests/Orders/ApplicationDbContextTests.cs", FileEditAction.Create).Should().Be(6553);

        var testEntry = new ManifestFileEntry("tests/Orders/ApplicationDbContextTests.cs", FileEditAction.Create);
        agent.DetermineCompactRetryBudget(6553, null, testEntry).Should().Be(8192);
    }

    [Fact]
    public async Task SmallModify_CompletesWithSearchReplaceInOneCall()
    {
        const string relativePath = "ApplicationDbContext.cs";
        var target = Path.Combine(_workspace, relativePath);
        await File.WriteAllTextAsync(target, "public class ApplicationDbContext { public int Value => 1; }");

        var provider = new FakeAiProvider();
        provider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "ApplicationDbContext.cs",
              "action": "Modify",
              "searchReplaceEdits": [
                { "search": "public int Value => 1;", "replace": "public int Value => 2;" }
              ]
            }
            """);

        var agent = new DeveloperAgent(provider, new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance), NullLogger<DeveloperAgent>.Instance);
        var result = await agent.GenerateAndApplyEditsAsync(CreateModifyRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(1);
        provider.ReceivedRequests[0].MaxTokens.Should().Be(4096);
        provider.ReceivedRequests[0].SystemPrompt.Should().Contain("searchReplaceEdits");
        provider.ReceivedRequests[0].SystemPrompt.Should().NotContain("complete resulting file");
        provider.ReceivedRequests[0].UserPrompt.Should().NotContain("hash-guarded small-file replacement");
        (await File.ReadAllTextAsync(target)).Should().Contain("Value => 2");
    }

    [Fact]
    public async Task ModifyTokenTruncation_RetriesOnce_Without16KEscalation()
    {
        const string relativePath = "issueService.ts";
        var target = Path.Combine(_workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "export const value = 1;");

        var provider = new FakeAiProvider();
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = """
                {
                  "filePath": "issueService.ts",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    { "search": "export const value = 1;", "replace": "export const value = 2;" }
                  ]
                }
                """
        });
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = """
                {
                  "filePath": "issueService.ts",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    { "search": "export const value = 2;", "replace": "export const value = 3;" }
                  ]
                }
                """
        });

        var agent = new DeveloperAgent(provider, new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance), NullLogger<DeveloperAgent>.Instance);
        var result = await agent.GenerateAndApplyEditsAsync(CreateModifyRequest(relativePath));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(2, "token truncation may retry once, then stop");
        provider.ReceivedRequests.Select(request => request.MaxTokens).Should().Equal(4096, 8192);
        provider.ReceivedRequests.Should().OnlyContain(request => request.MaxTokens <= 8192);
        provider.ReceivedRequests.Should().OnlyContain(request => request.MaxTokens < 16384);
    }

    [Fact]
    public void PatchFirstPrompt_KeepsApplicabilitySafetyLanguage()
    {
        var entry = new ManifestFileEntry("ApplicationDbContext.cs", FileEditAction.Modify);
        var prompt = DeveloperAgent.BuildSingleFileSystemPrompt(entry, useFullFileReplacement: false);

        prompt.Should().Contain("searchReplaceEdits");
        prompt.Should().Contain("each small exact search anchor must match once");
        prompt.Should().NotContain("complete resulting file");
        prompt.Should().NotContain("small-file Modify");
    }

    private DeveloperAgentRequest CreateModifyRequest(string relativePath) => new(
        TaskId: Guid.NewGuid(),
        ExecutionId: Guid.Empty,
        TaskTitle: "Update file",
        TaskDescription: "Apply a compact modify",
        AcceptanceCriteria: "File is updated",
        ImpactAnalysisSummary: "Modify target",
        ProposedPlan: $"Modify {relativePath}",
        ImpactedFilePaths: new[] { relativePath },
        WorkspacePath: _workspace,
        BranchName: "devpilot/modify-verify",
        ImpactedFiles: new[] { new ImpactedFileDetail(relativePath, "Modify", "Update") });

    private static DeveloperAgent CreateAgent(Dictionary<string, string?>? values = null)
    {
        IConfiguration? config = values == null
            ? null
            : new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new DeveloperAgent(
            new FakeAiProvider(),
            new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
            NullLogger<DeveloperAgent>.Instance,
            config);
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(path, "README.md"), "# fixture");
        RunGit(path, "add", ".");
        RunGit(path, "commit", "-m", "init");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }
}
