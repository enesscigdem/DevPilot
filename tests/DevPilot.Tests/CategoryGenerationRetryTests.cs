using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class CategoryGenerationRetryTests
{
    [Fact]
    public void DetermineInitialBudget_Create_UsesCategoryBudgetsNotUniversal4096()
    {
        var agent = CreateAgent();

        var dto = agent.DetermineInitialBudget("src/Orders/OrderDto.cs", FileEditAction.Create);
        var service = agent.DetermineInitialBudget("src/Orders/OrderService.cs", FileEditAction.Create);
        var controller = agent.DetermineInitialBudget("src/Orders/OrdersController.cs", FileEditAction.Create);
        var test = agent.DetermineInitialBudget("tests/Orders/ApplicationDbContextTests.cs", FileEditAction.Create);

        dto.Should().Be(3276);
        service.Should().Be(6553);
        controller.Should().Be(6553);
        test.Should().Be(6553);
        new[] { dto, service, controller, test }.Should().OnlyContain(budget => budget != 4096);
        service.Should().NotBe(dto);
    }

    [Fact]
    public void DetermineCompactRetryBudget_Create_IsAtMostTwiceInitialAndNeverAbove8192()
    {
        var agent = CreateAgent();
        var testEntry = new ManifestFileEntry(
            "tests/Orders/ApplicationDbContextTests.cs",
            FileEditAction.Create,
            "Add context tests",
            null);
        var dtoEntry = new ManifestFileEntry("src/Orders/OrderDto.cs", FileEditAction.Create, "Add dto", null);

        var testInitial = agent.DetermineInitialBudget(testEntry.FilePath, FileEditAction.Create);
        var testRetry = agent.DetermineCompactRetryBudget(testInitial, targetContent: null, testEntry);

        var dtoInitial = agent.DetermineInitialBudget(dtoEntry.FilePath, FileEditAction.Create);
        var dtoRetry = agent.DetermineCompactRetryBudget(dtoInitial, targetContent: null, dtoEntry);

        testInitial.Should().Be(6553);
        testRetry.Should().Be(8192);
        testRetry.Should().BeLessThanOrEqualTo(testInitial * 2);
        testRetry.Should().BeLessThanOrEqualTo(8192);
        testRetry.Should().NotBe(testInitial);

        dtoRetry.Should().Be(6552);
        dtoRetry.Should().BeLessThanOrEqualTo(dtoInitial * 2);
        dtoRetry.Should().BeLessThanOrEqualTo(8192);

        var configuredEightK = CreateAgent(new Dictionary<string, string?>
        {
            ["DeveloperAgent:TokenBudgets:TestFile"] = "8192"
        });
        var configuredInitial = configuredEightK.DetermineInitialBudget(testEntry.FilePath, FileEditAction.Create);
        var configuredRetry = configuredEightK.DetermineCompactRetryBudget(configuredInitial, null, testEntry);
        configuredInitial.Should().Be(8192);
        configuredRetry.Should().Be(8192);
    }

    [Fact]
    public void DetermineCompactRetryBudget_Create_NeverEscalatesTo16KOr32K()
    {
        var agent = CreateAgent(new Dictionary<string, string?>
        {
            ["DeveloperAgent:MaxOutputTokens"] = "32768",
            ["DeveloperAgent:MaxCompactRetryOutputTokens"] = "24576",
            ["DeveloperAgent:TokenBudgets:TestFile"] = "8192",
            ["DeveloperAgent:TokenBudgets:HandlerOrService"] = "8192"
        });

        var testEntry = new ManifestFileEntry("tests/Orders/ApplicationDbContextTests.cs", FileEditAction.Create, "Add tests", null);
        var serviceEntry = new ManifestFileEntry("src/Orders/OrderService.cs", FileEditAction.Create, "Add service", null);
        var largeExisting = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"// line {i}"));

        agent.DetermineCompactRetryBudget(8192, largeExisting, testEntry).Should().Be(8192);
        agent.DetermineCompactRetryBudget(8192, largeExisting, serviceEntry).Should().Be(8192);
        agent.DetermineCompactRetryBudget(6553, null, testEntry).Should().Be(8192);
        agent.DetermineCompactRetryBudget(4096, null, serviceEntry).Should().Be(8192);
    }

    [Fact]
    public void CompactCreatePrompt_IsSmallerThanFirstPass_AndReusesExemplar()
    {
        var contextFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tests/Orders/OrderServiceTests.cs"] = """
                using Xunit;
                public class OrderServiceTests
                {
                    [Fact]
                    public void Existing() { }
                }
                """
        };
        var entry = new ManifestFileEntry(
            "tests/Orders/ApplicationDbContextTests.cs",
            FileEditAction.Create,
            "Add context tests",
            null);
        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.Empty,
            TaskTitle: "Add context tests",
            TaskDescription: "A long description of repository history, unrelated planning notes, and extra product context that should not be repeated on compact retry.",
            AcceptanceCriteria: "Tests compile",
            ImpactAnalysisSummary: "Create tests",
            ProposedPlan: "Step 1: Inspect architecture\nStep 2: Create tests/Orders/ApplicationDbContextTests.cs\nStep 3: Review everything",
            ImpactedFilePaths: new[] { entry.FilePath },
            WorkspacePath: "/tmp/unused",
            BranchName: "devpilot/budgets");

        var firstPass = DeveloperAgent.BuildSingleFileUserPrompt(request, entry, contextFiles, new List<DiscoveredProjectNode>());
        var compact = DeveloperAgent.BuildCompactSingleFileUserPrompt(
            request,
            entry,
            targetContent: null,
            lockedContracts: null,
            useFullFileReplacement: false,
            contextFiles: contextFiles);

        compact.Length.Should().BeLessThan(firstPass.Length);
        compact.Should().Contain("COMPACT RETRY (TOKEN LIMIT DISCIPLINE)");
        compact.Should().Contain("=== Same-Role Repository Exemplar ===");
        compact.Should().Contain("OrderServiceTests.cs");
        compact.Should().NotContain("A long description of repository history");
        compact.Should().NotContain("Step 1: Inspect architecture");
        firstPass.Should().Contain("Step 2: Create tests/Orders/ApplicationDbContextTests.cs");
    }

    [Fact]
    public async Task CreateTokenTruncation_RetriesOnce_WithRaisedButCappedBudget()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "DevPilotBudgetRetry_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            InitGitRepo(workspace);
            RunGit(workspace, "checkout", "-b", "devpilot/budgets");

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
                      "filePath": "src/Orders/OrderService.cs",
                      "action": "Create",
                      "newContent": "public class OrderService {}"
                    }
                    """
            });

            var agent = new DeveloperAgent(provider, new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance), NullLogger<DeveloperAgent>.Instance);
            var result = await agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
                TaskId: Guid.NewGuid(),
                ExecutionId: Guid.Empty,
                TaskTitle: "Add service",
                TaskDescription: "Create the order service",
                AcceptanceCriteria: "File exists",
                ImpactAnalysisSummary: "Create service",
                ProposedPlan: "Create src/Orders/OrderService.cs",
                ImpactedFilePaths: new[] { "src/Orders/OrderService.cs" },
                WorkspacePath: workspace,
                BranchName: "devpilot/budgets",
                ImpactedFiles: new[] { new ImpactedFileDetail("src/Orders/OrderService.cs", "Create", "Add service") }));

            result.Success.Should().BeTrue(result.ErrorMessage);
            provider.SendAsyncCallCount.Should().Be(2);
            provider.ReceivedRequests[0].MaxTokens.Should().Be(6553);
            provider.ReceivedRequests[1].MaxTokens.Should().Be(8192);
            provider.ReceivedRequests.Should().OnlyContain(request => request.MaxTokens <= 8192);
            provider.ReceivedRequests[1].UserPrompt.Should().Contain("COMPACT RETRY (TOKEN LIMIT DISCIPLINE)");
            provider.ReceivedRequests[1].UserPrompt.Should().NotContain("Create the order service");
        }
        finally
        {
            try
            {
                if (Directory.Exists(workspace))
                {
                    Directory.Delete(workspace, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

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
