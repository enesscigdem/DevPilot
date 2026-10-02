using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class ModifyVerificationStabilizationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;

    public ModifyVerificationStabilizationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotModifyStabilize_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/modify-stabilize";
        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);
        InitGitRepo(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");
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

    [Fact]
    public async Task PrimaryModifyPatch_SendsConfiguredModifyReasoningEffortLow()
    {
        WriteWorktreeFile("src/Services/OrderService.cs", "public class OrderService { public int Value => 1; }");
        var (agent, provider, _) = CreateAgent();
        provider.StructuredResponsesToReturn.Enqueue(ValidModify("src/Services/OrderService.cs", "public int Value => 1;", "public int Value => 2;"));

        var result = await agent.GenerateAndApplyEditsAsync(ModifyRequest("src/Services/OrderService.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.ReceivedRequests.Should().ContainSingle();
        provider.ReceivedRequests[0].ReasoningEffort.Should().Be("low");
        provider.ReceivedRequests[0].MaxTokens.Should().Be(4096);
    }

    [Fact]
    public async Task CompactModifyRetry_AlsoSendsModifyReasoningEffortLow()
    {
        WriteWorktreeFile("src/Services/OrderService.cs", "public class OrderService { public int Value => 1; }");
        var (agent, provider, _) = CreateAgent();
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            Content = "{ \"filePath\":\"src/Services/OrderService.cs\",\"action\":\"Modify\""
        });
        provider.StructuredResponsesToReturn.Enqueue(ValidModify("src/Services/OrderService.cs", "public int Value => 1;", "public int Value => 2;"));

        var result = await agent.GenerateAndApplyEditsAsync(ModifyRequest("src/Services/OrderService.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(2);
        provider.ReceivedRequests[0].ReasoningEffort.Should().Be("low");
        provider.ReceivedRequests[1].ReasoningEffort.Should().Be("low");
        provider.ReceivedRequests[1].MaxTokens.Should().Be(8192);
        provider.ReceivedRequests.Should().OnlyContain(request => request.MaxTokens <= 8192);
    }

    [Fact]
    public async Task CreateGeneration_DoesNotApplyModifyReasoningEffort()
    {
        var (agent, provider, _) = CreateAgent();
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = """{"filePath":"src/Orders/OrderDto.cs","action":"Create","newContent":"public class OrderDto {}"}"""
        });

        var result = await agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Add dto",
            "Create the order dto",
            "File exists",
            "Summary",
            "Create src/Orders/OrderDto.cs",
            new[] { "src/Orders/OrderDto.cs" },
            _worktreeDir,
            _branchName,
            new[] { new ImpactedFileDetail("src/Orders/OrderDto.cs", "Create", "Add dto") }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.ReceivedRequests.Should().ContainSingle();
        provider.ReceivedRequests[0].ReasoningEffort.Should().BeNull();
        agent.DetermineMechanicalReasoningEffort().Should().Be("low");
        agent.DetermineModifyReasoningEffort().Should().Be("low");
    }

    [Fact]
    public async Task ValidCompleteModify_WithFinishReasonLength_IsAcceptedWithoutRetry()
    {
        WriteWorktreeFile("src/Services/OrderService.cs", "public class OrderService { public int Value => 1; }");
        var (agent, provider, _) = CreateAgent();
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            Content = """{"filePath":"src/Services/OrderService.cs","action":"Modify","searchReplaceEdits":[{"search":"public int Value => 1;","replace":"public int Value => 2;"}]}"""
        });

        var result = await agent.GenerateAndApplyEditsAsync(ModifyRequest("src/Services/OrderService.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(1);
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "Services", "OrderService.cs")).Should().Contain("Value => 2");
    }

    [Fact]
    public async Task InvalidTruncatedLengthOutput_StillRetriesOnce()
    {
        WriteWorktreeFile("src/Services/OrderService.cs", "public class OrderService { public int Value => 1; }");
        var (agent, provider, _) = CreateAgent();
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            Content = "{ \"filePath\":\"src/Services/OrderService.cs\",\"action\":\"Modify\",\"searchReplaceEdits\":["
        });
        provider.StructuredResponsesToReturn.Enqueue(ValidModify("src/Services/OrderService.cs", "public int Value => 1;", "public int Value => 2;"));

        var result = await agent.GenerateAndApplyEditsAsync(ModifyRequest("src/Services/OrderService.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public void CompactModifyPrompt_ExcludesUnrelatedPlanAndContext()
    {
        var entry = new ManifestFileEntry("src/Services/PaymentService.cs", FileEditAction.Modify, "Fix pay", null);
        var compact = DeveloperAgent.BuildCompactSingleFileUserPrompt(
            new DeveloperAgentRequest(
                Guid.NewGuid(),
                Guid.Empty,
                "Fix Payment Service",
                "A long description of repository history and unrelated planning notes.",
                "Pay returns true",
                "Summary",
                "Step 1: Inspect architecture\nStep 2: Redesign everything\nStep 3: Modify PaymentService.cs",
                new[] { entry.FilePath },
                _worktreeDir,
                _branchName),
            entry,
            "public class PaymentService { public bool Pay() => false; }");

        compact.Should().Contain("src/Services/PaymentService.cs");
        compact.Should().Contain("Pay returns true");
        compact.Should().Contain("SEARCH/REPLACE");
        compact.Should().NotContain("A long description of repository history");
        compact.Should().NotContain("Step 1: Inspect architecture");
        compact.Should().NotContain("Redesign everything");
        compact.Should().NotContain("ProposedPlan");
    }

    [Fact]
    public async Task ApplicabilityRepair_UsesMechanicalLowReasoning_AndTokenLimitGetsOneMicroRepair()
    {
        WriteWorktreeFile("src/Services/OrderService.cs", "public class OrderService { public int Value => 1; }");
        var (agent, provider, recorder) = CreateAgent();
        provider.StructuredResponsesToReturn.Enqueue(ValidModify("src/Services/OrderService.cs", "public int Missing => 1;", "public int Missing => 2;"));
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            Content = new string('x', 500)
        });
        provider.StructuredResponsesToReturn.Enqueue(ValidModify("src/Services/OrderService.cs", "public int Value => 1;", "public int Value => 2;"));

        var result = await agent.GenerateAndApplyEditsAsync(ModifyRequest("src/Services/OrderService.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(3);
        provider.ReceivedRequests[1].ReasoningEffort.Should().Be("low");
        provider.ReceivedRequests[1].MaxTokens.Should().Be(4096);
        provider.ReceivedRequests[2].ReasoningEffort.Should().Be("low");
        provider.ReceivedRequests[2].MaxTokens.Should().Be(2048);
        provider.ReceivedRequests[2].UserPrompt.Should().Contain("Target File: src/Services/OrderService.cs");
        provider.ReceivedRequests[2].UserPrompt.Should().NotContain("ProposedPlan");
        provider.ReceivedRequests[2].UserPrompt.Should().NotContain("Task Description");
        recorder.Metadata.Any(metadata => metadata?.EventKind == "ApplicabilityRepair").Should().BeTrue();
        recorder.Metadata.Any(metadata => metadata?.EventKind == "MicroApplicabilityRepair" && metadata.RequestedOutputTokens == 2048).Should().BeTrue();
    }

    [Fact]
    public async Task SecondApplicabilityTruncation_StopsSafely_WithoutUnboundedRetry()
    {
        WriteWorktreeFile("src/Services/OrderService.cs", "public class OrderService { public int Value => 1; }");
        var (agent, provider, _) = CreateAgent();
        provider.StructuredResponsesToReturn.Enqueue(ValidModify("src/Services/OrderService.cs", "public int Missing => 1;", "public int Missing => 2;"));
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded
        });

        var result = await agent.GenerateAndApplyEditsAsync(ModifyRequest("src/Services/OrderService.cs"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("micro applicability repair");
        provider.SendAsyncCallCount.Should().Be(3);
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "Services", "OrderService.cs")).Should().Contain("Value => 1");
    }

    [Fact]
    public void DirectPrerequisite_SuppressesReverseHeuristic()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/controllers/issueController.ts", FileEditAction.Modify),
            new ManifestFileEntry("src/routes/issueRoutes.ts", FileEditAction.Modify)
        };
        var sources = new Dictionary<string, string>
        {
            ["src/controllers/issueController.ts"] = "export function createIssue() { return 1; }",
            ["src/routes/issueRoutes.ts"] = "import { createIssue } from '../controllers/issueController';\nexport const router = createIssue;"
        };

        var graph = DeveloperAgent.CollectGenerationPrerequisiteGraph(files, sources);

        graph.Prerequisites.Should().ContainSingle(item =>
            item.ProducerPath == "src/controllers/issueController.ts" &&
            item.ConsumerPath == "src/routes/issueRoutes.ts" &&
            item.Reason == GenerationPrerequisiteReason.DirectLocalReference);
        graph.Prerequisites.Should().NotContain(item =>
            item.ProducerPath == "src/routes/issueRoutes.ts" &&
            item.ConsumerPath == "src/controllers/issueController.ts");
        graph.SuppressedConflictCount.Should().BeGreaterThan(0);
        graph.DirectCount.Should().Be(1);
        graph.HeuristicCount.Should().Be(0);
    }

    [Fact]
    public void ManifestPrerequisite_SuppressesReverseHeuristic()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/Application/Dtos/TaskDto.cs", FileEditAction.Create),
            new ManifestFileEntry(
                "src/Api/Controllers/TaskController.cs",
                FileEditAction.Create,
                "controller",
                new[] { "src/Application/Dtos/TaskDto.cs" })
        };

        var graph = DeveloperAgent.CollectGenerationPrerequisiteGraph(files);

        graph.Prerequisites.Should().ContainSingle(item =>
            item.ProducerPath == "src/Application/Dtos/TaskDto.cs" &&
            item.ConsumerPath == "src/Api/Controllers/TaskController.cs" &&
            item.Reason == GenerationPrerequisiteReason.ManifestDependency);
        graph.Prerequisites.Should().NotContain(item =>
            item.ProducerPath == "src/Api/Controllers/TaskController.cs" &&
            item.ConsumerPath == "src/Application/Dtos/TaskDto.cs");
        graph.ManifestCount.Should().Be(1);
        graph.HeuristicCount.Should().Be(0);
    }

    [Fact]
    public void Heuristic_CannotAddImmediateCycle()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/controllers/issueController.ts", FileEditAction.Modify),
            new ManifestFileEntry("src/services/issueService.ts", FileEditAction.Modify),
            new ManifestFileEntry("src/models/issueModel.ts", FileEditAction.Modify)
        };
        var sources = new Dictionary<string, string>
        {
            ["src/controllers/issueController.ts"] = "export function handle() { return 1; }",
            ["src/services/issueService.ts"] = "import { handle } from '../controllers/issueController';\nexport const service = handle;",
            ["src/models/issueModel.ts"] = "import { service } from '../services/issueService';\nexport const model = service;"
        };

        var graph = DeveloperAgent.CollectGenerationPrerequisiteGraph(files, sources);

        graph.Prerequisites.Should().NotContain(item =>
            item.ProducerPath == "src/models/issueModel.ts" &&
            item.ConsumerPath == "src/controllers/issueController.ts");
        graph.Prerequisites.Should().NotContain(item =>
            graph.Prerequisites.Any(other =>
                other.ProducerPath == item.ConsumerPath &&
                other.ConsumerPath == item.ProducerPath));
        (graph.SuppressedCycleCount + graph.SuppressedConflictCount).Should().BeGreaterThan(0);
    }

    [Fact]
    public void HighSemanticScoreOrController_DoesNotCreateEveryLowerLayerEdge()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/Application/CurrentUserService.cs", FileEditAction.Create),
            new ManifestFileEntry("src/Application/ViewOrderRequirement.cs", FileEditAction.Create),
            new ManifestFileEntry("src/Infrastructure/ETagFilterAttribute.cs", FileEditAction.Create),
            new ManifestFileEntry("src/Api/ProblemDetailsMiddleware.cs", FileEditAction.Create),
            new ManifestFileEntry("src/Api/Controllers/OrdersController.cs", FileEditAction.Create)
        };

        var graph = DeveloperAgent.CollectGenerationPrerequisiteGraph(files);

        graph.Prerequisites.Should().NotContain(item =>
            item.ConsumerPath == "src/Api/Controllers/OrdersController.cs" &&
            item.ProducerPath == "src/Application/CurrentUserService.cs");
        graph.Prerequisites.Should().NotContain(item =>
            item.ConsumerPath == "src/Api/Controllers/OrdersController.cs" &&
            item.ProducerPath == "src/Infrastructure/ETagFilterAttribute.cs");
        graph.HeuristicCount.Should().Be(0);
    }

    [Fact]
    public void UnrelatedFiles_RemainConcurrent()
    {
        var files = new[]
        {
            new ManifestFileEntry("models/alpha.ts", FileEditAction.Modify),
            new ManifestFileEntry("models/beta.ts", FileEditAction.Modify)
        };
        var sources = new Dictionary<string, string>
        {
            ["models/alpha.ts"] = "export interface Alpha { id: string; }",
            ["models/beta.ts"] = "export interface Beta { id: string; }"
        };

        DeveloperAgent.CollectGenerationPrerequisiteGraph(files, sources).Prerequisites.Should().BeEmpty();
    }

    [Fact]
    public async Task PrerequisiteReason_IsVisibleInActivityTextAndMetadata()
    {
        WriteWorktreeFile("models/entity.ts", "export interface CreateEntityInput { title: string; }");
        WriteWorktreeFile(
            "repository/entityRepository.ts",
            "import { CreateEntityInput } from '../models/entity';\nexport function create(input: CreateEntityInput) { return input.title; }");
        var (agent, provider, recorder) = CreateAgent(concurrency: "2");
        provider.CustomHandler = (request, _) =>
        {
            var target = ExtractTarget(request.UserPrompt);
            var content = target.EndsWith("entity.ts", StringComparison.OrdinalIgnoreCase)
                ? """{"filePath":"models/entity.ts","action":"Modify","searchReplaceEdits":[{"search":"title: string;","replace":"title: string; extra: string;"}]}"""
                : """{"filePath":"repository/entityRepository.ts","action":"Modify","searchReplaceEdits":[{"search":"return input.title;","replace":"return input.title + input.extra;"}]}""";
            return Task.FromResult(new AiResponse { IsSuccess = true, Content = content });
        };

        var result = await agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Extend shared contract",
            "Add extra field",
            null,
            "Summary",
            "Plan",
            new[] { "models/entity.ts", "repository/entityRepository.ts" },
            _worktreeDir,
            _branchName,
            new[]
            {
                new ImpactedFileDetail("models/entity.ts", "Modify", "Update shared input"),
                new ImpactedFileDetail("repository/entityRepository.ts", "Modify", "Consume shared input")
            }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        recorder.Messages.Should().Contain(message =>
            message.Contains("GenerationPrerequisite: models/entity.ts -> repository/entityRepository.ts · DirectLocalReference"));
        recorder.Messages.Should().Contain(message => message.StartsWith("GenerationPrerequisiteSummary:"));
        recorder.Metadata.Any(metadata =>
            metadata?.EventKind == "GenerationPrerequisite" &&
            metadata.PrerequisiteReason == nameof(GenerationPrerequisiteReason.DirectLocalReference)).Should().BeTrue();
    }

    [Fact]
    public void ExistingFixture_DirectlyReferencingTouchedProduction_BecomesBoundedVerificationEvidence()
    {
        WriteWorktreeFile("src/Program.cs", "public class Program { public static void Main() {} }");
        WriteWorktreeFile(
            "tests/CustomWebApplicationFactory.cs",
            """
            using Microsoft.AspNetCore.Mvc.Testing;
            public class CustomWebApplicationFactory : WebApplicationFactory<Program>
            {
                protected override void ConfigureWebHost(IWebHostBuilder builder) { }
            }
            """);
        WriteWorktreeFile("tests/UnrelatedTests.cs", "public class UnrelatedTests { public void Ok() {} }");

        var excerpts = VerificationContractEvidence.Collect(
            "src/Program.cs",
            "public class Program { public static void Main() {} }",
            _worktreeDir,
            new Dictionary<string, string>
            {
                ["src/Program.cs"] = "public class Program { public static void Main() {} }"
            });

        excerpts.Should().ContainSingle(item => item.FilePath.Contains("CustomWebApplicationFactory"));
        excerpts.Should().OnlyContain(item => item.Excerpt.Length <= VerificationContractEvidence.MaxExcerptChars);
        excerpts.Should().NotContain(item => item.FilePath.Contains("UnrelatedTests"));

        var prompt = DeveloperAgent.BuildSingleFileUserPrompt(
            new DeveloperAgentRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Redis configuration",
                "Wire redis in Program",
                "App still boots in tests",
                "Summary",
                "Modify Program.cs",
                new[] { "src/Program.cs" },
                _worktreeDir,
                _branchName),
            new ManifestFileEntry("src/Program.cs", FileEditAction.Modify, "Wire redis", null),
            new Dictionary<string, string> { ["src/Program.cs"] = "public class Program { public static void Main() {} }" },
            new List<DiscoveredProjectNode>());

        prompt.Should().Contain("=== Existing Verification Contract Evidence ===");
        prompt.Should().Contain("CustomWebApplicationFactory");
        prompt.Should().Contain("WebApplicationFactory<Program>");
        prompt.Should().NotContain("UnrelatedTests");
    }

    [Fact]
    public void VerificationEvidence_IsCappedToTwoBoundedExcerpts()
    {
        WriteWorktreeFile("src/Program.cs", "public class Program {}");
        WriteWorktreeFile("tests/FactoryA.cs", "public class FactoryA : WebApplicationFactory<Program> { public FactoryA() {} }");
        WriteWorktreeFile("tests/FactoryB.cs", "public class FactoryB : WebApplicationFactory<Program> { public FactoryB() {} }");
        WriteWorktreeFile("tests/FactoryC.cs", "public class FactoryC : WebApplicationFactory<Program> { public FactoryC() {} }");

        var excerpts = VerificationContractEvidence.Collect(
            "src/Program.cs",
            "public class Program {}",
            _worktreeDir);

        excerpts.Should().HaveCountLessThanOrEqualTo(2);
        excerpts.Should().OnlyContain(item => item.Excerpt.Length <= 1200);
    }

    [Fact]
    public void TryMaterializeCompletedEdit_RejectsTruncatedJson()
    {
        var entry = new ManifestFileEntry("src/A.cs", FileEditAction.Modify);
        DeveloperAgent.TryMaterializeCompletedEdit(
            "{ \"filePath\":\"src/A.cs\",\"action\":\"Modify\",\"searchReplaceEdits\":[",
            entry,
            "class A {}",
            false,
            out _,
            out _).Should().BeFalse();
    }

    [Fact]
    public void EmptySearchReplaceEdits_AreInvalidModifyResponses_NotSilentSuccess()
    {
        var entry = new ManifestFileEntry("src/A.cs", FileEditAction.Modify);
        var act = () => DeveloperAgent.ValidateSingleFileEditSpec(
            new FileEditSpec("src/A.cs", FileEditAction.Modify, null, Array.Empty<SearchReplaceEdit>()),
            entry,
            "class A {}");

        act.Should().Throw<FormatException>().WithMessage("*requires at least one edit*");
        DeveloperAgent.TryMaterializeCompletedEdit(
            """{"filePath":"src/A.cs","action":"Modify","searchReplaceEdits":[]}""",
            entry,
            "class A {}",
            false,
            out _,
            out _).Should().BeFalse();
    }

    private (DeveloperAgent Agent, FakeAiProvider Provider, RecordingActivityRecorder Recorder) CreateAgent(string? concurrency = null)
    {
        var provider = new FakeAiProvider { ProviderName = "Kimi" };
        var recorder = new RecordingActivityRecorder();
        var values = new Dictionary<string, string?>
        {
            ["DeveloperAgent:ModifyReasoningEffort"] = "low",
            ["DeveloperAgent:MechanicalReasoningEffort"] = "low"
        };
        if (concurrency != null)
        {
            values["DeveloperAgent:MaxConcurrentFileGenerations"] = concurrency;
        }

        var agent = new DeveloperAgent(
            provider,
            new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
            NullLogger<DeveloperAgent>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            recorder);
        return (agent, provider, recorder);
    }

    private DeveloperAgentRequest ModifyRequest(string path) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Update service",
            "Change the return value",
            "Value is 2",
            "Summary",
            "Modify the service",
            new[] { path },
            _worktreeDir,
            _branchName,
            new[] { new ImpactedFileDetail(path, "Modify", "Update value") });

    private static AiResponse ValidModify(string path, string search, string replace) =>
        new()
        {
            IsSuccess = true,
            FinishReason = "stop",
            Content = $$"""{"filePath":"{{path}}","action":"Modify","searchReplaceEdits":[{"search":"{{search}}","replace":"{{replace}}"}]}"""
        };

    private void WriteWorktreeFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_worktreeDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static string ExtractTarget(string prompt)
    {
        const string marker = "Target File: ";
        var start = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = prompt.IndexOf('\n', start);
        return end < 0 ? prompt[start..].Trim() : prompt[start..end].Trim();
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "DevPilot Tests");
        RunGit(path, "config", "user.email", "tests@devpilot.local");
    }

    private static void RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
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

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    private sealed class RecordingActivityRecorder : IExecutionActivityRecorder
    {
        public List<string> Messages { get; } = new();
        public List<ExecutionActivityMetadata?> Metadata { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            Metadata.Add(metadata);
            return Task.CompletedTask;
        }
    }
}
