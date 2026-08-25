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

public sealed class CompactRetryContractPreservationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;

    private const string FreshIssueService = """
        import { Issue } from './issue';

        export type IssueStatus = 'open' | 'closed';

        export interface IssueDto {
          id: string;
          status: IssueStatus;
        }

        export class IssueService {
          constructor(private readonly repo: IssueRepository) {}

          async updateStatus(id: string, status: IssueStatus): Promise<Issue> {
            const issue = await this.repo.findById(id);
            issue.status = status;
            return this.repo.save(issue);
          }

          private helper() {
            return this.repo;
          }
        }

        export function createIssueService(repo: IssueRepository): IssueService {
          return new IssueService(repo);
        }
        """;

    private const string StaleIssueService = """
        export class IssueService {
          updateIssueStatus(id: string, status: string) {
            return id + status;
          }
        }
        """;

    private const string IssueControllerSource = """
        import { IssueService } from '../services/issueService';

        export class IssueController {
          constructor(private readonly issues: IssueService) {}

          async setStatus(id: string, status: string) {
            return this.issues.updateStatus(id, status);
          }
        }
        """;

    public CompactRetryContractPreservationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotCompactContracts_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/compact-contracts";

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
    public void TsJsClassContract_IncludesExactPublicMethodSignatures_WithoutImplementations()
    {
        var excerpt = PlannedFileDependencyResolver.ExtractLockedContractExcerpt(
            "src/services/issueService.ts",
            FreshIssueService);

        excerpt.Should().NotBeNull();
        excerpt.Should().Contain("export type IssueStatus");
        excerpt.Should().Contain("export interface IssueDto");
        excerpt.Should().Contain("export class IssueService");
        excerpt.Should().Contain("constructor(private readonly repo: IssueRepository)");
        excerpt.Should().Contain("async updateStatus(id: string, status: IssueStatus): Promise<Issue>");
        excerpt.Should().Contain("export function createIssueService(repo: IssueRepository): IssueService");
        excerpt.Should().NotContain("this.repo.findById");
        excerpt.Should().NotContain("this.repo.save");
        excerpt.Should().NotContain("private helper");
        excerpt.Should().NotContain("return new IssueService");
    }

    [Fact]
    public void PrimaryModify_SeesLatestVirtualWorkspaceDependency()
    {
        var primary = BuildPrimaryControllerPrompt();

        primary.Should().Contain("updateStatus");
        primary.Should().Contain("extraField");
        primary.Should().NotContain("STALE_CONTRACT_SNAPSHOT");
        primary.Should().NotContain("updateIssueStatus");
        primary.Should().Contain("exact published method and type names");
    }

    [Fact]
    public void CompactRetry_SeesTheSameFreshDependencyContract()
    {
        var primary = BuildPrimaryControllerPrompt();
        var compact = BuildCompactControllerPrompt();

        compact.Should().Contain("updateStatus");
        compact.Should().Contain("export class IssueService");
        compact.Should().Contain("exact published method and type names");
        compact.Should().NotContain("updateIssueStatus");
        compact.Should().NotContain("STALE_CONTRACT_SNAPSHOT");
        compact.Should().NotContain("this.repo.findById");
        primary.Should().Contain("updateStatus");
    }

    [Fact]
    public void CompactRetry_DoesNotPreferStaleRepositoryDependency()
    {
        var compact = BuildCompactControllerPrompt();

        compact.Should().Contain("updateStatus");
        compact.Should().NotContain("updateIssueStatus");
        compact.Should().NotContain("STALE_CONTRACT_SNAPSHOT");
    }

    [Fact]
    public void ExactGeneratedMethodName_SurvivesPrimaryToCompactRetry()
    {
        var primary = BuildPrimaryControllerPrompt();
        var compact = BuildCompactControllerPrompt();

        primary.Should().Contain("updateStatus");
        compact.Should().Contain("updateStatus");
        primary.Should().NotContain("updateIssueStatus");
        compact.Should().NotContain("updateIssueStatus");
    }

    [Fact]
    public void HeuristicOnlyDependency_IsNotInjectedAsAuthoritativeContext()
    {
        var controller = new ManifestFileEntry(
            "src/controllers/issueController.ts",
            FileEditAction.Modify,
            "Add status",
            null);
        var lockedContracts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/services/issueService.ts"] = PlannedFileDependencyResolver.ExtractLockedContractExcerpt(
                "src/services/issueService.ts",
                FreshIssueService)!
        };
        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["src/services/issueService.ts"] = new(
                "src/services/issueService.ts",
                FileEditAction.Modify,
                FreshIssueService,
                null)
        };
        var virtualWorkspace = new Dictionary<string, string>
        {
            ["src/services/issueService.ts"] = FreshIssueService
        };
        var heuristicOnlyTarget = """
            export function handleRequest() {
              return { ok: true };
            }
            """;

        var authoritative = DeveloperAgent.CollectAuthoritativeDependencyContracts(
            controller,
            heuristicOnlyTarget,
            lockedContracts,
            virtualWorkspace,
            completedEdits);
        var compact = DeveloperAgent.BuildCompactSingleFileUserPrompt(
            new DeveloperAgentRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Add issue status support",
                "A long description of repository history and unrelated planning notes.",
                "Handler returns ok",
                "Summary",
                "Step 1: Inspect architecture\nStep 2: Redesign everything",
                new[] { "src/services/issueService.ts", controller.FilePath },
                _worktreeDir,
                _branchName),
            controller,
            heuristicOnlyTarget,
            lockedContracts,
            useFullFileReplacement: false,
            contextFiles: new Dictionary<string, string>
            {
                ["src/controllers/issueController.ts"] = heuristicOnlyTarget
            },
            completedEdits,
            virtualWorkspace);

        authoritative.Should().BeEmpty();
        compact.Should().NotContain("updateStatus");
        compact.Should().NotContain("src/services/issueService.ts");
        compact.Should().NotContain("--- Locked Contract:");

        var stats = DeveloperAgent.MeasureGenerationPromptContext(
            controller,
            heuristicOnlyTarget,
            lockedContracts,
            virtualWorkspace,
            completedEdits,
            compactRetry: true);
        stats.StrongCount.Should().Be(0);
        stats.HeuristicCount.Should().Be(0);
    }

    [Fact]
    public void DataConfigFiles_DoNotCreateHeuristicCodeDependencyByNameAlone()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/data/issue.json", FileEditAction.Modify),
            new ManifestFileEntry("src/controllers/issueController.ts", FileEditAction.Modify)
        };

        var graph = DeveloperAgent.CollectGenerationPrerequisiteGraph(files);

        graph.Prerequisites.Should().BeEmpty();
        graph.HeuristicCount.Should().Be(0);
        DeveloperAgent.IsNonSourceDataConfigFile("src/data/issue.json").Should().BeTrue();
        DeveloperAgent.IsNonSourceDataConfigFile("src/controllers/issueController.ts").Should().BeFalse();
    }

    [Fact]
    public void DataConfigFiles_StillAllowStrongManifestOrDirectEvidence()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/data/issue.json", FileEditAction.Modify),
            new ManifestFileEntry(
                "src/controllers/issueController.ts",
                FileEditAction.Modify,
                "load issue config",
                new[] { "src/data/issue.json" })
        };

        var graph = DeveloperAgent.CollectGenerationPrerequisiteGraph(files);

        graph.Prerequisites.Should().ContainSingle(item =>
            item.ProducerPath == "src/data/issue.json" &&
            item.ConsumerPath == "src/controllers/issueController.ts" &&
            item.Reason == GenerationPrerequisiteReason.ManifestDependency);
        graph.HeuristicCount.Should().Be(0);
    }

    [Fact]
    public void CompactPrompt_ExcludesFullProposedPlanAndUnrelatedContext()
    {
        var compact = BuildCompactControllerPrompt();

        compact.Should().Contain("src/controllers/issueController.ts");
        compact.Should().Contain("SEARCH/REPLACE");
        compact.Should().NotContain("A long description of repository history");
        compact.Should().NotContain("Step 1: Inspect architecture");
        compact.Should().NotContain("Redesign everything");
        compact.Should().NotContain("ProposedPlan");
        compact.Should().NotContain("src/notes/noteService.ts");
        compact.Should().NotContain("unrelatedNoteMethod");
    }

    [Fact]
    public void CreateBudgetsAndCompactBehavior_RemainUnchanged()
    {
        var agent = new DeveloperAgent(
            new FakeAiProvider(),
            new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
            NullLogger<DeveloperAgent>.Instance);
        var createEntry = new ManifestFileEntry(
            "tests/Orders/ApplicationDbContextTests.cs",
            FileEditAction.Create,
            "Add context tests",
            null);
        var modifyEntry = new ManifestFileEntry(
            "src/controllers/issueController.ts",
            FileEditAction.Modify,
            "Add status",
            null);

        agent.DetermineInitialBudget("src/Orders/OrderDto.cs", FileEditAction.Create).Should().Be(3276);
        agent.DetermineInitialBudget("src/Orders/OrderService.cs", FileEditAction.Create).Should().Be(6553);
        agent.DetermineInitialBudget(modifyEntry.FilePath, FileEditAction.Modify).Should().Be(4096);
        agent.DetermineCompactRetryBudget(4096, "existing", modifyEntry).Should().Be(8192);
        agent.DetermineModifyReasoningEffort().Should().Be("low");

        var createRequest = new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.Empty,
            "Add context tests",
            "A long description of repository history, unrelated planning notes, and extra product context that should not be repeated on compact retry.",
            "Tests compile",
            "Create tests",
            "Step 1: Inspect architecture\nStep 2: Create tests/Orders/ApplicationDbContextTests.cs\nStep 3: Review everything",
            new[] { createEntry.FilePath },
            "/tmp/unused",
            "devpilot/budgets");
        var contextFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tests/Orders/OrderServiceTests.cs"] = "using Xunit;\npublic class OrderServiceTests { [Fact] public void Existing() { } }"
        };
        var firstPass = DeveloperAgent.BuildSingleFileUserPrompt(createRequest, createEntry, contextFiles, new List<DiscoveredProjectNode>());
        var compact = DeveloperAgent.BuildCompactSingleFileUserPrompt(
            createRequest,
            createEntry,
            targetContent: null,
            lockedContracts: null,
            useFullFileReplacement: false,
            contextFiles: contextFiles);

        compact.Length.Should().BeLessThan(firstPass.Length);
        compact.Should().Contain("=== Same-Role Repository Exemplar ===");
        compact.Should().NotContain("A long description of repository history");
        firstPass.Should().Contain("Step 2: Create tests/Orders/ApplicationDbContextTests.cs");
    }

    [Fact]
    public async Task CompactRetryCallSite_ReceivesFreshVirtualWorkspaceContract()
    {
        WriteWorktreeFile("src/services/issueService.ts", StaleIssueService);
        WriteWorktreeFile("src/controllers/issueController.ts", IssueControllerSource);

        var provider = new FakeAiProvider();
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = """
                {
                  "filePath": "src/services/issueService.ts",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "updateIssueStatus(id: string, status: string)",
                      "replace": "async updateStatus(id: string, status: string)"
                    }
                  ]
                }
                """
        });
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
                  "filePath": "src/controllers/issueController.ts",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "return this.issues.updateStatus(id, status);",
                      "replace": "return this.issues.updateStatus(id, status as IssueStatus);"
                    }
                  ]
                }
                """
        });

        var recorder = new RecordingActivityRecorder();
        var agent = new DeveloperAgent(
            provider,
            new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
            NullLogger<DeveloperAgent>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxConcurrentFileGenerations"] = "1",
                ["DeveloperAgent:ModifyReasoningEffort"] = "low"
            }).Build(),
            recorder);

        var result = await agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Add issue status support",
            "A long description of repository history and unrelated planning notes.",
            "Controller uses IssueService.updateStatus",
            "Summary",
            "Step 1: Inspect architecture\nStep 2: Redesign everything\nStep 3: Modify issueController.ts",
            new[] { "src/services/issueService.ts", "src/controllers/issueController.ts" },
            _worktreeDir,
            _branchName,
            new[]
            {
                new ImpactedFileDetail("src/services/issueService.ts", "Modify", "Publish status contract"),
                new ImpactedFileDetail("src/controllers/issueController.ts", "Modify", "Consume status contract")
            }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(3);

        var compact = provider.ReceivedRequests[2];
        compact.UserPrompt.Should().Contain("COMPACT RETRY (TOKEN LIMIT DISCIPLINE)");
        compact.UserPrompt.Should().Contain("updateStatus");
        compact.UserPrompt.Should().NotContain("updateIssueStatus");
        compact.UserPrompt.Should().NotContain("Step 1: Inspect architecture");
        compact.UserPrompt.Should().NotContain("A long description of repository history");
        compact.MaxTokens.Should().Be(8192);
        compact.ReasoningEffort.Should().Be("low");

        var compactTelemetry = recorder.Metadata.FirstOrDefault(metadata =>
            metadata?.ProviderCallKind == "CompactGenerationRetry");
        compactTelemetry.Should().NotBeNull();
        compactTelemetry!.ReasoningEffort.Should().Be("low");
        compactTelemetry.StrongDependencyContractCount.Should().BeGreaterThan(0);
        compactTelemetry.HeuristicInjectedContextCount.Should().Be(0);
        compactTelemetry.PromptSizeEstimate.Should().BeGreaterThan(0);
        compactTelemetry.InputTokens.Should().BeNull();
    }

    private string BuildPrimaryControllerPrompt()
    {
        var controller = ControllerEntry();
        return DeveloperAgent.BuildSingleFileUserPrompt(
            Request("Add issue status support", controller.FilePath),
            controller,
            ControllerContextFiles(),
            CompletedEdits(),
            new List<DiscoveredProjectNode>(),
            StaleLockedContracts(),
            referencePattern: null,
            useFullFileReplacement: false,
            virtualWorkspace: FreshVirtualWorkspace());
    }

    private string BuildCompactControllerPrompt()
    {
        var controller = ControllerEntry();
        return DeveloperAgent.BuildCompactSingleFileUserPrompt(
            Request("Add issue status support", controller.FilePath),
            controller,
            IssueControllerSource,
            StaleLockedContracts(),
            useFullFileReplacement: false,
            contextFiles: ControllerContextFiles(),
            CompletedEdits(),
            FreshVirtualWorkspace());
    }

    private static ManifestFileEntry ControllerEntry() =>
        new("src/controllers/issueController.ts", FileEditAction.Modify, "Consume issue status", null);

    private static Dictionary<string, string> ControllerContextFiles() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/services/issueService.ts"] = StaleIssueService + "\n/* STALE_CONTRACT_SNAPSHOT */",
        ["src/controllers/issueController.ts"] = IssueControllerSource,
        ["src/notes/noteService.ts"] = "export function unrelatedNoteMethod() { return 1; }"
    };

    private static Dictionary<string, string> FreshVirtualWorkspace() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/services/issueService.ts"] = FreshIssueService.Replace("id: string;", "id: string;\n  extraField: string;")
    };

    private static Dictionary<string, FileEditSpec> CompletedEdits() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/services/issueService.ts"] = new(
            "src/services/issueService.ts",
            FileEditAction.Modify,
            FreshIssueService,
            null),
        ["src/notes/noteService.ts"] = new(
            "src/notes/noteService.ts",
            FileEditAction.Modify,
            "export function unrelatedNoteMethod() { return 1; }",
            null)
    };

    private static Dictionary<string, string> StaleLockedContracts() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/services/issueService.ts"] = "export class IssueService {\n  updateIssueStatus(id: string, status: string)\n}",
        ["src/notes/noteService.ts"] = "export function unrelatedNoteMethod()"
    };

    private DeveloperAgentRequest Request(string title, string path) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            title,
            "A long description of repository history and unrelated planning notes.",
            "Controller uses IssueService.updateStatus",
            "Summary",
            "Step 1: Inspect architecture\nStep 2: Redesign everything\nStep 3: Modify issueController.ts",
            new[] { "src/services/issueService.ts", path, "src/notes/noteService.ts" },
            _worktreeDir,
            _branchName);

    private void WriteWorktreeFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_worktreeDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
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
        public List<ExecutionActivityMetadata?> Metadata { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Metadata.Add(metadata);
            message.Should().NotContain("updateStatus(");
            message.Should().NotContain("export class IssueService");
            return Task.CompletedTask;
        }
    }
}
