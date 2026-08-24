using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
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
    public async Task ExecuteFocusedRepairAsync_TwoCorrelatedFiles_RepairsHighestConfidenceFileOnly()
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
        result.ModifiedFiles.Should().ContainSingle(f => f == "src/ServiceA.cs");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1, "file B must wait for a fresh build before repair");

        File.ReadAllText(fileA).Should().Contain("\"new\"");
        File.ReadAllText(fileB).Should().Contain("\"old\"");
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

    [Fact]
    public async Task ExecuteFocusedRepairAsync_SmallModifyFile_UsesSearchReplaceNotFullNewContent()
    {
        WriteWorktreeFile("src/controllers/issueController.ts", SmallIssueController);
        var agent = CreateAgent();
        _fakeAiProvider.ResponsesToReturn.Enqueue(ControllerSearchReplaceResponse);

        var result = await agent.ExecuteFocusedRepairAsync(CreateTypeScriptControllerRepairRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
        AssertFocusedRepairIsSurgical(_fakeAiProvider.ReceivedRequests[0], "src/controllers/issueController.ts");
        _fakeAiProvider.ReceivedRequests[0].UserPrompt.Should().NotContain("hash-guarded small-file replacement");
        _fakeAiProvider.ReceivedRequests[0].SystemPrompt.Should().NotContain("newContent\":");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "controllers", "issueController.ts"))
            .Should().Contain("createIssue({ title: req.body.title, description: req.body.description })");
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_SmallController_DoesNotRequestFullFileReplacement()
    {
        WriteWorktreeFile("src/controllers/issueController.ts", SmallIssueController);
        var agent = CreateAgent();
        _fakeAiProvider.ResponsesToReturn.Enqueue(ControllerSearchReplaceResponse);

        await agent.ExecuteFocusedRepairAsync(CreateTypeScriptControllerRepairRequest(), CancellationToken.None);

        var request = _fakeAiProvider.ReceivedRequests.Should().ContainSingle().Subject;
        AssertFocusedRepairIsSurgical(request, "src/controllers/issueController.ts");
        WorktreeEditApplier.IsSmallTextFile(SmallIssueController).Should().BeTrue();
        request.SystemPrompt.Should().NotContain("hash-guarded");
        request.UserPrompt.Should().NotContain("complete resulting content");
        request.UserPrompt.Should().NotContain("small-file replacement");
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_SmallRoute_DoesNotRequestFullFileReplacement()
    {
        WriteWorktreeFile("src/routes/issueRoutes.ts", SmallIssueRoutes);
        var agent = CreateAgent();
        _fakeAiProvider.ResponsesToReturn.Enqueue(RoutesSearchReplaceResponse);

        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix issue routes",
            AcceptanceCriteria: "Route binds the generated controller method",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/routes/issueRoutes.ts" },
            DiagnosticEvidence: "src/routes/issueRoutes.ts(5,48): error TS2551: Property 'updateStatus' does not exist on type 'IssueController'.",
            DiagnosticLocations: new[] { "src/routes/issueRoutes.ts(5,48): error TS2551" },
            LanguageContext: "TypeScript Express routes",
            Model: "test-model");

        var result = await agent.ExecuteFocusedRepairAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        var aiRequest = _fakeAiProvider.ReceivedRequests.Should().ContainSingle().Subject;
        AssertFocusedRepairIsSurgical(aiRequest, "src/routes/issueRoutes.ts");
        WorktreeEditApplier.IsSmallTextFile(SmallIssueRoutes).Should().BeTrue();
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "routes", "issueRoutes.ts"))
            .Should().Contain("controller.updateIssueStatus.bind(controller)");
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_TypeScriptTwoFileRepair_IsSurgicalWithOneCallPerTargetAndPeerContext()
    {
        WriteWorktreeFile("src/services/issueService.ts", SmallIssueService);
        WriteWorktreeFile("src/controllers/issueController.ts", SmallIssueController);
        WriteWorktreeFile("src/routes/issueRoutes.ts", SmallIssueRoutes);
        WriteWorktreeFile("src/controllers/userController.ts", UnrelatedUserController);

        var agent = CreateAgent();
        _fakeAiProvider.ResponsesToReturn.Enqueue(ControllerSearchReplaceResponse);
        _fakeAiProvider.ResponsesToReturn.Enqueue(RoutesSearchReplaceResponse);

        var diagnosticEvidence =
            """
            src/controllers/issueController.ts(6,44): error TS2554: Expected 1 arguments, but got 2.
            src/controllers/issueController.ts(11,42): error TS2551: Property 'updateIssueStatus' does not exist on type 'IssueService'. Did you mean 'updateStatus'?
            src/routes/issueRoutes.ts(5,48): error TS2551: Property 'updateStatus' does not exist on type 'IssueController'. Did you mean 'updateIssueStatus'?
            """;

        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix issue create/status contract mismatches",
            AcceptanceCriteria: "Controller and routes match the generated service/controller methods",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/controllers/issueController.ts", "src/routes/issueRoutes.ts" },
            DiagnosticEvidence: diagnosticEvidence,
            DiagnosticLocations: new[]
            {
                "src/controllers/issueController.ts(6,44): error TS2554",
                "src/controllers/issueController.ts(11,42): error TS2551",
                "src/routes/issueRoutes.ts(5,48): error TS2551"
            },
            LanguageContext: "TypeScript Node/Express",
            Model: "test-model");

        var result = await agent.ExecuteFocusedRepairAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1, "repair the highest-confidence file only before the next build");
        _fakeAiProvider.ReceivedRequests.Should().ContainSingle();

        var controllerRequest = _fakeAiProvider.ReceivedRequests[0];
        AssertFocusedRepairIsSurgical(controllerRequest, "src/controllers/issueController.ts");

        controllerRequest.UserPrompt.Should().Contain(diagnosticEvidence);
        controllerRequest.UserPrompt.Should().Contain("error TS2554: Expected 1 arguments, but got 2.");
        controllerRequest.UserPrompt.Should().Contain("Authoritative Peer Contract Context");
        controllerRequest.UserPrompt.Should().Contain("src/routes/issueRoutes.ts");
        controllerRequest.UserPrompt.Should().Contain("src/services/issueService.ts");
        controllerRequest.UserPrompt.Should().Contain("createIssue(input: CreateIssueInput)");
        controllerRequest.UserPrompt.Should().Contain("updateStatus(id: string, status: string)");
        controllerRequest.UserPrompt.Should().NotContain("UserController");
        controllerRequest.UserPrompt.Should().NotContain("listUsers");
        controllerRequest.UserPrompt.Should().NotContain(UnrelatedUserController);

        File.ReadAllText(Path.Combine(_worktreeDir, "src", "controllers", "issueController.ts"))
            .Should().Contain("createIssue({ title: req.body.title, description: req.body.description })");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "routes", "issueRoutes.ts"))
            .Should().Be(SmallIssueRoutes);
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "controllers", "userController.ts"))
            .Should().Be(UnrelatedUserController);
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "services", "issueService.ts"))
            .Should().Be(SmallIssueService);
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_TokenLimitExceeded_DoesNotRetryOrEscalate()
    {
        WriteWorktreeFile("src/controllers/issueController.ts", SmallIssueController);
        var agent = CreateAgent();
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FailureKind = AiFailureKind.TokenLimitExceeded,
            Content = string.Empty,
            ErrorMessage = "TokenLimitExceeded"
        });

        var result = await agent.ExecuteFocusedRepairAsync(CreateTypeScriptControllerRepairRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("TokenLimitExceeded");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1, "focused repair must not compact-retry or escalate tokens");
        _fakeAiProvider.ReceivedRequests[0].MaxTokens.Should().Be(4096);
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "controllers", "issueController.ts"))
            .Should().Be(SmallIssueController);
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_FullFileNewContent_IsRejectedForExistingModify()
    {
        WriteWorktreeFile("src/controllers/issueController.ts", SmallIssueController);
        var agent = CreateAgent();
        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/controllers/issueController.ts","action":"Modify","newContent":"export class IssueController {}"}""");

        var result = await agent.ExecuteFocusedRepairAsync(CreateTypeScriptControllerRepairRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Match(message =>
            message!.Contains("newContent", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("searchReplaceEdits", StringComparison.OrdinalIgnoreCase));
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "controllers", "issueController.ts"))
            .Should().Be(SmallIssueController);
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_BareClosingBraceAnchor_IsRejected()
    {
        WriteWorktreeFile("src/controllers/issueController.ts", SmallIssueController);
        var agent = CreateAgent();
        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/controllers/issueController.ts","action":"Modify","searchReplaceEdits":[{"search":"}","replace":"  extra() {}\n}"}]}""");

        var result = await agent.ExecuteFocusedRepairAsync(CreateTypeScriptControllerRepairRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("bare closing brace");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteFocusedRepairAsync_MoreThanTwoRepairFiles_RepairsOnlyTheFirstFile()
    {
        WriteWorktreeFile("src/A.ts", "export const a = 1;");
        WriteWorktreeFile("src/B.ts", "export const b = 1;");
        WriteWorktreeFile("src/C.ts", "export const c = 1;");
        var agent = CreateAgent();
        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/A.ts","action":"Modify","searchReplaceEdits":[{"search":"export const a = 1;","replace":"export const a = 2;"}]}""");
        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/B.ts","action":"Modify","searchReplaceEdits":[{"search":"export const b = 1;","replace":"export const b = 2;"}]}""");
        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/C.ts","action":"Modify","searchReplaceEdits":[{"search":"export const c = 1;","replace":"export const c = 2;"}]}""");

        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Cap repair scope",
            AcceptanceCriteria: null,
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/A.ts", "src/B.ts", "src/C.ts" },
            DiagnosticEvidence: "src/A.ts(1,1): error TS0001\nsrc/B.ts(1,1): error TS0001\nsrc/C.ts(1,1): error TS0001",
            Model: "test-model");

        var result = await agent.ExecuteFocusedRepairAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1, "repair one highest-confidence file before the next build");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "A.ts")).Should().Contain("a = 2");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "B.ts")).Should().Be("export const b = 1;");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "C.ts")).Should().Be("export const c = 1;");
    }

    [Fact]
    public void DetermineFocusedRepairBudget_UsesPatchBudgetWithoutSmallFileOrEscalation()
    {
        var agent = CreateAgent();
        var generationBudget = agent.DetermineInitialBudget(
            "src/controllers/issueController.ts",
            FileEditAction.Modify,
            SmallIssueController);
        var repairBudget = agent.DetermineFocusedRepairBudget();

        WorktreeEditApplier.IsSmallTextFile(SmallIssueController).Should().BeTrue();
        generationBudget.Should().Be(4096, "first-pass Modify uses the bounded SEARCH/REPLACE patch budget");
        repairBudget.Should().Be(4096, "focused repair uses the existing ModifyPatch budget only");
        repairBudget.Should().BeLessThan(8192);
        repairBudget.Should().BeLessThan(32768);
    }

    [Fact]
    public void FocusedRepairPrompt_ContainsAnchorDisciplineAndExactDiagnostics()
    {
        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix issue controller",
            AcceptanceCriteria: "Match CreateIssueInput",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/controllers/issueController.ts", "src/routes/issueRoutes.ts" },
            DiagnosticEvidence: "src/controllers/issueController.ts(6,44): error TS2554: Expected 1 arguments, but got 2.",
            DiagnosticLocations: new[] { "src/controllers/issueController.ts(6,44): error TS2554" },
            LanguageContext: "TypeScript Node/Express",
            Model: "test-model");

        var systemPrompt = DeveloperAgent.BuildFocusedDiagnosticRepairSystemPrompt("src/controllers/issueController.ts");
        var userPrompt = DeveloperAgent.BuildFocusedDiagnosticRepairUserPrompt(
            "src/controllers/issueController.ts",
            SmallIssueController,
            request,
            new Dictionary<string, string>
            {
                ["src/routes/issueRoutes.ts"] = SmallIssueRoutes,
                ["src/services/issueService.ts"] = SmallIssueService
            });

        systemPrompt.Should().Contain("searchReplaceEdits");
        systemPrompt.Should().Contain("2-5 line unique excerpt");
        systemPrompt.Should().Contain("Never use a bare closing brace alone");
        systemPrompt.Should().Contain("NEVER return 'newContent'");
        systemPrompt.Should().NotContain("hash-guarded");

        userPrompt.Should().Contain("src/controllers/issueController.ts(6,44): error TS2554: Expected 1 arguments, but got 2.");
        userPrompt.Should().Contain("surgical SEARCH/REPLACE");
        userPrompt.Should().Contain("unique 2-5 line anchor");
        userPrompt.Should().Contain("Never use a bare closing brace alone");
        userPrompt.Should().Contain("Never return full newContent");
        userPrompt.Should().Contain("src/routes/issueRoutes.ts");
        userPrompt.Should().Contain("createIssue(input: CreateIssueInput)");
        userPrompt.Should().NotContain("hash-guarded small-file replacement");
        userPrompt.Should().NotContain(UnrelatedUserController);
    }

    [Fact]
    public void CollectFocusedRepairPeerContext_IncludesCorrelatedAndImportedContractsOnly()
    {
        WriteWorktreeFile("src/services/issueService.ts", SmallIssueService);
        WriteWorktreeFile("src/controllers/issueController.ts", SmallIssueController);
        WriteWorktreeFile("src/routes/issueRoutes.ts", SmallIssueRoutes);
        WriteWorktreeFile("src/controllers/userController.ts", UnrelatedUserController);

        var request = new FocusedRepairRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix issues",
            AcceptanceCriteria: null,
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/controllers/issueController.ts", "src/routes/issueRoutes.ts" },
            DiagnosticEvidence: "src/controllers/issueController.ts(6,44): error TS2554",
            DiagnosticLocations: new[] { "src/controllers/issueController.ts(6,44): error TS2554" },
            Model: "test-model");

        var peers = DeveloperAgent.CollectFocusedRepairPeerContext(
            _worktreeDir,
            "src/controllers/issueController.ts",
            request,
            SmallIssueController);

        peers.Should().ContainKey("src/routes/issueRoutes.ts");
        peers.Should().ContainKey("src/services/issueService.ts");
        peers.Should().NotContainKey("src/controllers/userController.ts");
        peers.Should().NotContainKey("src/controllers/issueController.ts");
        peers["src/services/issueService.ts"].Should().Contain("updateStatus");
        peers.Count.Should().BeLessThanOrEqualTo(4);
    }

    [Theory]
    [InlineData("}")]
    [InlineData("};")]
    [InlineData("},")]
    [InlineData("})")]
    [InlineData("});")]
    [InlineData("  }  ")]
    public void IsBareClosingBraceAnchor_ForbidsBraceOnlyAnchors(string search)
    {
        DeveloperAgent.IsBareClosingBraceAnchor(search).Should().BeTrue();
    }

    [Fact]
    public void ValidateFocusedRepairSearchAnchors_RejectsNewContentAndBareBrace()
    {
        var withNewContent = new FileEditSpec(
            "src/controllers/issueController.ts",
            FileEditAction.Modify,
            NewContent: "export class IssueController {}");
        var actNewContent = () => DeveloperAgent.ValidateFocusedRepairSearchAnchors(
            withNewContent,
            "src/controllers/issueController.ts");
        actNewContent.Should().Throw<FormatException>().WithMessage("*newContent*");

        var withBareBrace = new FileEditSpec(
            "src/controllers/issueController.ts",
            FileEditAction.Modify,
            SearchReplaceEdits: new[] { new SearchReplaceEdit("}", "  extra() {}\n}") });
        var actBareBrace = () => DeveloperAgent.ValidateFocusedRepairSearchAnchors(
            withBareBrace,
            "src/controllers/issueController.ts");
        actBareBrace.Should().Throw<FormatException>().WithMessage("*bare closing brace*");
    }

    [Fact]
    public void WorktreeEditApplier_SmallFilePredicate_RemainsUnchanged()
    {
        WorktreeEditApplier.IsSmallTextFile(SmallIssueController).Should().BeTrue();
        WorktreeEditApplier.IsSmallTextFile(SmallIssueRoutes).Should().BeTrue();
        var large = new string('a', 4001);
        WorktreeEditApplier.IsSmallTextFile(large).Should().BeFalse();
    }

    private DeveloperAgent CreateAgent() =>
        new(_fakeAiProvider, _editApplier, NullLogger<DeveloperAgent>.Instance);

    private FocusedRepairRequest CreateTypeScriptControllerRepairRequest() =>
        new(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Fix issue controller",
            AcceptanceCriteria: "Controller matches CreateIssueInput",
            WorkspacePath: _worktreeDir,
            BranchName: _branchName,
            RepairFiles: new[] { "src/controllers/issueController.ts" },
            DiagnosticEvidence: "src/controllers/issueController.ts(6,44): error TS2554: Expected 1 arguments, but got 2.",
            DiagnosticLocations: new[] { "src/controllers/issueController.ts(6,44): error TS2554" },
            LanguageContext: "TypeScript Node/Express",
            Model: "test-model");

    private void WriteWorktreeFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_worktreeDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void AssertFocusedRepairIsSurgical(AiRequest request, string filePath)
    {
        request.MaxTokens.Should().Be(4096);
        request.SystemPrompt.Should().Contain($"lightweight focused verification repair for a single existing file: '{filePath}'");
        request.SystemPrompt.Should().Contain("searchReplaceEdits");
        request.SystemPrompt.Should().Contain("NEVER return 'newContent'");
        request.SystemPrompt.Should().Contain("2-5 line unique excerpt");
        request.SystemPrompt.Should().Contain("Never use a bare closing brace alone");
        request.SystemPrompt.Should().NotContain("hash-guarded");
        request.SystemPrompt.Should().NotContain("complete resulting content");
        request.UserPrompt.Should().Contain($"Target File: {filePath}");
        request.UserPrompt.Should().Contain("surgical SEARCH/REPLACE");
        request.UserPrompt.Should().Contain("unique 2-5 line anchor");
        request.UserPrompt.Should().Contain("Never use a bare closing brace alone");
        request.UserPrompt.Should().Contain("Never return full newContent");
        request.UserPrompt.Should().NotContain("hash-guarded small-file replacement");
        request.UserPrompt.Should().NotContain("complete resulting content once in newContent");
    }

    private const string SmallIssueService =
        """
        export interface CreateIssueInput {
          title: string;
          description: string;
        }

        export class IssueService {
          async createIssue(input: CreateIssueInput) {
            return { id: '1', ...input };
          }

          async updateStatus(id: string, status: string) {
            return { id, status };
          }
        }
        """;

    private const string SmallIssueController =
        """
        import { IssueService } from '../services/issueService';

        export class IssueController {
          constructor(private readonly issueService: IssueService) {}

          async createIssue(req: any, res: any) {
            const issue = await this.issueService.createIssue(req.body.title, req.body.description);
            res.status(201).json(issue);
          }

          async updateIssueStatus(req: any, res: any) {
            const issue = await this.issueService.updateIssueStatus(req.params.id, req.body.status);
            res.json(issue);
          }
        }
        """;

    private const string SmallIssueRoutes =
        """
        import { IssueController } from '../controllers/issueController';

        export function registerIssueRoutes(router: any, controller: IssueController) {
          router.post('/issues', controller.createIssue.bind(controller));
          router.patch('/issues/:id/status', controller.updateStatus.bind(controller));
        }
        """;

    private const string UnrelatedUserController =
        """
        export class UserController {
          listUsers() {
            return [];
          }
        }
        """;

    private const string ControllerSearchReplaceResponse =
        """
        {"filePath":"src/controllers/issueController.ts","action":"Modify","searchReplaceEdits":[{"search":"const issue = await this.issueService.createIssue(req.body.title, req.body.description);","replace":"const issue = await this.issueService.createIssue({ title: req.body.title, description: req.body.description });"},{"search":"const issue = await this.issueService.updateIssueStatus(req.params.id, req.body.status);","replace":"const issue = await this.issueService.updateStatus(req.params.id, req.body.status);"}]}
        """;

    private const string RoutesSearchReplaceResponse =
        """
        {"filePath":"src/routes/issueRoutes.ts","action":"Modify","searchReplaceEdits":[{"search":"router.patch('/issues/:id/status', controller.updateStatus.bind(controller));","replace":"router.patch('/issues/:id/status', controller.updateIssueStatus.bind(controller));"}]}
        """;

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
