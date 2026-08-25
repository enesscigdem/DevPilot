using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class RepositoryGenerationExemplarTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryGenerationExemplarTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotExemplarTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
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
    public void SelectSameRoleExemplar_SameDirectoryDifferentFeature_IsSelectedAndBounded()
    {
        var orders = """
            import { Router } from "express";

            export function createOrdersRouter() {
              const router = Router();
              router.get("/", async (req, res) => {
                res.json([]);
              });
              return router;
            }
            """;
        var repositoryContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/routes/ordersRoutes.ts"] = orders,
            ["src/routes/ordersRoutes.ts.bak"] = "should not match extension",
            ["src/models/order.ts"] = "export type Order = { id: string };"
        };

        var first = RepositoryGenerationExemplar.SelectSameRoleExemplar(
            "src/routes/invoicesRoutes.ts",
            repositoryContents);
        var second = RepositoryGenerationExemplar.SelectSameRoleExemplar(
            "src/routes/invoicesRoutes.ts",
            repositoryContents);

        first.Should().NotBeNull();
        first!.FilePath.Should().Be("src/routes/ordersRoutes.ts");
        first.BoundedExcerpt.Should().Contain("createOrdersRouter");
        first.BoundedExcerpt.Length.Should().BeLessThanOrEqualTo(RepositoryGenerationExemplar.MaxExemplarChars);
        first.BoundedExcerpt.Split('\n').Length.Should().BeLessThanOrEqualTo(RepositoryGenerationExemplar.MaxExemplarLines);
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void SelectSameRoleExemplar_SkipsSameFeatureStem()
    {
        var repositoryContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/invoices/invoicesModel.ts"] = "export type InvoiceModel = { id: string };",
            ["src/invoices/ordersRoutes.ts"] = """
                export function createOrdersRouter() {
                  return [];
                }
                """
        };

        var exemplar = RepositoryGenerationExemplar.SelectSameRoleExemplar(
            "src/invoices/invoicesRoutes.ts",
            repositoryContents);

        exemplar.Should().NotBeNull();
        exemplar!.FilePath.Should().Be("src/invoices/ordersRoutes.ts");
        exemplar.FilePath.Should().NotBe("src/invoices/invoicesModel.ts");
    }

    [Fact]
    public void BuildRepositorySizeHint_UsesEvidenceLineCount_NotHardCodedRoles()
    {
        var exemplarContent = string.Join('\n', Enumerable.Range(1, 18).Select(i => $"export const item{i} = {i};"));
        var repositoryContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lib/widgets/alphaWidget.py"] = exemplarContent
        };

        var hint = RepositoryGenerationExemplar.BuildRepositorySizeHint(
            "lib/widgets/betaWidget.py",
            repositoryContents);

        hint.Should().NotBeNull();
        hint.Should().Contain("approximately 18 source lines");
        hint.Should().NotContain("controller");
        hint.Should().NotContain("service");
        hint.Should().NotContain("Program.cs");
        hint.Should().NotContain("app.ts");
        hint.Should().NotContain("main.py");
    }

    [Fact]
    public void BoundStructuralExemplar_NeverDumpsFullNeighborFile()
    {
        var huge = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"public int Field{i} {{ get; set; }}"));
        var excerpt = RepositoryGenerationExemplar.BoundStructuralExemplar(huge);

        excerpt.Length.Should().BeLessThanOrEqualTo(RepositoryGenerationExemplar.MaxExemplarChars);
        excerpt.Split('\n').Length.Should().BeLessThanOrEqualTo(RepositoryGenerationExemplar.MaxExemplarLines);
    }

    [Fact]
    public void BuildSingleFileUserPrompt_InjectsBoundedExemplarAndSizeHint()
    {
        var contextFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/routes/ordersRoutes.ts"] = """
                import { Router } from "express";
                export function createOrdersRouter() {
                  const router = Router();
                  return router;
                }
                """
        };

        var prompt = DeveloperAgent.BuildSingleFileUserPrompt(
            CreateRequest("src/routes/invoicesRoutes.ts"),
            new ManifestFileEntry("src/routes/invoicesRoutes.ts", FileEditAction.Create, "Add invoices routes", null),
            contextFiles,
            new List<DiscoveredProjectNode>());

        prompt.Should().Contain("=== Same-Role Repository Exemplar ===");
        prompt.Should().Contain("src/routes/ordersRoutes.ts");
        prompt.Should().Contain("createOrdersRouter");
        prompt.Should().Contain("approximately");
        prompt.Should().Contain("source lines");
        var excerptStart = prompt.IndexOf("--- Exemplar:", StringComparison.Ordinal);
        var excerptEnd = prompt.IndexOf("--- End Exemplar ---", StringComparison.Ordinal);
        excerptEnd.Should().BeGreaterThan(excerptStart);
        (excerptEnd - excerptStart).Should().BeLessThan(RepositoryGenerationExemplar.MaxExemplarChars + 200);
    }

    [Fact]
    public void BuildSingleFileUserPrompt_Modify_DoesNotInjectExemplar()
    {
        var contextFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/routes/invoicesRoutes.ts"] = "export function createInvoicesRouter() { return []; }",
            ["src/routes/ordersRoutes.ts"] = "export function createOrdersRouter() { return []; }"
        };

        var prompt = DeveloperAgent.BuildSingleFileUserPrompt(
            CreateRequest("src/routes/invoicesRoutes.ts", FileEditAction.Modify),
            new ManifestFileEntry("src/routes/invoicesRoutes.ts", FileEditAction.Modify, "Update invoices routes", null),
            contextFiles,
            new List<DiscoveredProjectNode>());

        prompt.Should().NotContain("=== Same-Role Repository Exemplar ===");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_UsesOneProviderCall_WhenExemplarExistsInContext()
    {
        var workspace = Path.Combine(_tempDir, "one-call");
        Directory.CreateDirectory(workspace);
        InitGitRepo(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "src", "routes"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "src", "routes", "ordersRoutes.ts"),
            "export function createOrdersRouter() { return []; }");
        RunGit(workspace, "add", ".");
        RunGit(workspace, "commit", "-m", "add exemplar");
        RunGit(workspace, "checkout", "-b", "devpilot/exemplar");

        var provider = new FakeAiProvider();
        provider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/routes/invoicesRoutes.ts",
              "action": "Create",
              "newContent": "export function createInvoicesRouter() { return []; }"
            }
            """);

        var agent = new DeveloperAgent(provider, new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance), NullLogger<DeveloperAgent>.Instance);
        var result = await agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.Empty,
            TaskTitle: "Add invoices routes",
            TaskDescription: "Create invoices routes using existing route conventions",
            AcceptanceCriteria: "File exists",
            ImpactAnalysisSummary: "Create invoices routes",
            ProposedPlan: "Create src/routes/invoicesRoutes.ts",
            ImpactedFilePaths: new[] { "src/routes/invoicesRoutes.ts" },
            WorkspacePath: workspace,
            BranchName: "devpilot/exemplar",
            ImpactedFiles: new[] { new ImpactedFileDetail("src/routes/invoicesRoutes.ts", "Create", "Add invoices routes") }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(1);
        provider.ReceivedRequests[0].UserPrompt.Should().Contain("=== Same-Role Repository Exemplar ===");
        provider.ReceivedRequests[0].UserPrompt.Should().Contain("ordersRoutes.ts");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_CreateFailure_DoesNotPartiallyApplyEarlierFiles()
    {
        var workspace = Path.Combine(_tempDir, "all-or-nothing");
        Directory.CreateDirectory(workspace);
        InitGitRepo(workspace);
        RunGit(workspace, "checkout", "-b", "devpilot/exemplar");

        var provider = new FakeAiProvider();
        provider.ResponsesToReturn.Enqueue("""
            {
              "filePath": "src/alpha.ts",
              "action": "Create",
              "newContent": "export const alpha = 1;"
            }
            """);
        provider.ResponsesToReturn.Enqueue("MALFORMED_NON_JSON");
        provider.ResponsesToReturn.Enqueue("STILL_MALFORMED");

        var agent = new DeveloperAgent(provider, new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance), NullLogger<DeveloperAgent>.Instance);
        var result = await agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.Empty,
            TaskTitle: "Create two files",
            TaskDescription: "Create alpha and beta",
            AcceptanceCriteria: "Both files exist",
            ImpactAnalysisSummary: "Create two files",
            ProposedPlan: "Create src/alpha.ts then src/beta.ts",
            ImpactedFilePaths: new[] { "src/alpha.ts", "src/beta.ts" },
            WorkspacePath: workspace,
            BranchName: "devpilot/exemplar",
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("src/alpha.ts", "Create", "Add alpha"),
                new ImpactedFileDetail("src/beta.ts", "Create", "Add beta")
            }));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        File.Exists(Path.Combine(workspace, "src", "alpha.ts")).Should().BeFalse();
        File.Exists(Path.Combine(workspace, "src", "beta.ts")).Should().BeFalse();
    }

    private static DeveloperAgentRequest CreateRequest(string filePath, FileEditAction action = FileEditAction.Create) =>
        new(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.Empty,
            TaskTitle: "Grounded generation",
            TaskDescription: "Follow repository conventions",
            AcceptanceCriteria: "Match existing structure",
            ImpactAnalysisSummary: "Create file",
            ProposedPlan: $"Create {filePath}",
            ImpactedFilePaths: new[] { filePath },
            WorkspacePath: "/tmp/unused",
            BranchName: "devpilot/exemplar");

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

        using var process = Process.Start(psi);
        process!.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }
}
