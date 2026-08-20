using System.Collections.Concurrent;
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

public class DeveloperAgentRepairDisciplineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly FakeExecutionActivityRecorder _activityRecorder;

    public DeveloperAgentRepairDisciplineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotRepairTests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/repair-test-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        InitGitRepo(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _fakeAiProvider = new FakeAiProvider();
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _activityRecorder = new FakeExecutionActivityRecorder();
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

    private static string CreateLargeProductsApiTestsContent()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using System.Net;");
        sb.AppendLine("using System.Net.Http.Json;");
        sb.AppendLine("using FluentAssertions;");
        sb.AppendLine("using Microsoft.AspNetCore.Mvc.Testing;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine();
        sb.AppendLine("namespace NetCaseStudy.Tests.Api;");
        sb.AppendLine();
        sb.AppendLine("public class ProductsApiTests : IClassFixture<WebApplicationFactory<Program>>");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly HttpClient _client;");
        sb.AppendLine();
        sb.AppendLine("    public ProductsApiTests(WebApplicationFactory<Program> factory)");
        sb.AppendLine("    {");
        sb.AppendLine("        _client = factory.CreateClient();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // 20 existing large test methods to simulate a realistic >300-line test suite
        for (int i = 1; i <= 20; i++)
        {
            sb.AppendLine($"    [Fact]");
            sb.AppendLine($"    public async Task GetProduct_{i}_ReturnsOk()");
            sb.AppendLine("    {");
            sb.AppendLine($"        var response = await _client.GetAsync(\"/api/products/{i}\");");
            sb.AppendLine("        response.StatusCode.Should().Be(HttpStatusCode.OK);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("    [Fact]");
        sb.AppendLine("    public async Task ExistingLastTest_ReturnsOk()");
        sb.AppendLine("    {");
        sb.AppendLine("        var response = await _client.GetAsync(\"/api/products/last\");");
        sb.AppendLine("        response.StatusCode.Should().Be(HttpStatusCode.OK);");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    [Fact]
    public void RepairPrompt_WhenTargetFileIsLarge_BoundsSourceWindowToFixtureAndInsertionAnchor()
    {
        var largeContent = CreateLargeProductsApiTestsContent();
        largeContent.Split('\n').Length.Should().BeGreaterThan(100);

        var entry = new ManifestFileEntry("NetCaseStudy.Tests/Api/ProductsApiTests.cs", FileEditAction.Modify, "Add low-stock endpoint tests", null);

        var prompt = DeveloperAgent.BuildSingleFileRepairUserPrompt(
            parseError: "SEARCH block not found (0 matches)",
            previousResponse: "{\"search\": \"non-existent\", \"replace\": \"code\"}",
            fileEntry: entry,
            currentTargetContent: largeContent,
            relevantGeneratedDependencies: null,
            lockedContracts: null,
            applicabilityFailure: null);

        // Prompt must contain header and fixture
        prompt.Should().Contain("public class ProductsApiTests : IClassFixture<WebApplicationFactory<Program>>");
        prompt.Should().Contain("public ProductsApiTests(WebApplicationFactory<Program> factory)");

        // Prompt must contain insertion anchor footer
        prompt.Should().Contain("=== Insertion Anchor (End of Class) ===");
        prompt.Should().Contain("ExistingLastTest_ReturnsOk");

        // Prompt must NOT dump all 20 existing tests verbatim
        prompt.Should().Contain("// ... [prior existing test methods omitted for brevity] ...");
        prompt.Should().Contain("// ... [subsequent existing methods omitted for brevity] ...");
        prompt.Should().NotContain("GetProduct_10_ReturnsOk");
        prompt.Should().NotContain("GetProduct_15_ReturnsOk");
    }

    [Fact]
    public void RepairPrompt_ContainsExactFailedAnchorAndSanitizedValidationReason()
    {
        var largeContent = CreateLargeProductsApiTestsContent();
        var entry = new ManifestFileEntry("NetCaseStudy.Tests/Api/ProductsApiTests.cs", FileEditAction.Modify, "Add low-stock endpoint tests", null);

        var failure = EditApplicabilityResult.Fail(
            errorMessage: "Edit 1 of 1: exact search string matched 0 occurrences in 'NetCaseStudy.Tests/Api/ProductsApiTests.cs'.",
            failedEditIndex: 1,
            totalEdits: 1,
            failedSearch: "public async Task NonExistentAnchor()",
            failedReplace: "new test",
            matchCount: 0,
            surroundingContext: "    [Fact]\n    public async Task ExistingLastTest_ReturnsOk()");

        var prompt = DeveloperAgent.BuildSingleFileRepairUserPrompt(
            parseError: failure.ErrorMessage ?? "Error",
            previousResponse: "{\"search\": \"public async Task NonExistentAnchor()\", \"replace\": \"new test\"}",
            fileEntry: entry,
            currentTargetContent: largeContent,
            relevantGeneratedDependencies: null,
            lockedContracts: null,
            applicabilityFailure: failure);

        prompt.Should().Contain("=== Applicability Failure Evidence ===");
        prompt.Should().Contain("Failed Edit Block: 1 of 1");
        prompt.Should().Contain("zero matches");
        prompt.Should().Contain("public async Task NonExistentAnchor()");
        prompt.Should().Contain("=== Context Around Target Edit / Failure Point ===");
        prompt.Should().Contain("ExistingLastTest_ReturnsOk");
    }

    [Fact]
    public void RepairPrompt_FiltersUnrelatedGeneratedDependencies_AndExtractsContractsForTests()
    {
        var entry = new ManifestFileEntry("NetCaseStudy.Tests/Api/ProductsApiTests.cs", FileEditAction.Modify, "Add low-stock endpoint tests", null);

        var completedEdits = new Dictionary<string, FileEditSpec>
        {
            ["src/NetCaseStudy.Application/Dtos/LowStockProductDto.cs"] = new(
                "src/NetCaseStudy.Application/Dtos/LowStockProductDto.cs",
                FileEditAction.Create,
                "public record LowStockProductDto(int Id, string Name, int Stock);",
                null),
            ["src/NetCaseStudy.Application/Handlers/GetLowStockProductsQueryHandler.cs"] = new(
                "src/NetCaseStudy.Application/Handlers/GetLowStockProductsQueryHandler.cs",
                FileEditAction.Create,
                "public class GetLowStockProductsQueryHandler : IRequestHandler<GetLowStockProductsQuery, List<LowStockProductDto>>\n{\n    private readonly IDbContext _db;\n    public GetLowStockProductsQueryHandler(IDbContext db) { _db = db; }\n    public async Task<List<LowStockProductDto>> Handle(GetLowStockProductsQuery request, CancellationToken ct) { return new List<LowStockProductDto>(); }\n}",
                null)
        };

        var relevant = DeveloperAgent.GetRelevantGeneratedEdits(entry, completedEdits);

        // Should extract public contracts rather than dumping full implementation bodies
        relevant.Should().ContainKey("src/NetCaseStudy.Application/Dtos/LowStockProductDto.cs");
        relevant["src/NetCaseStudy.Application/Dtos/LowStockProductDto.cs"].Should().Contain("LowStockProductDto");

        relevant.Should().ContainKey("src/NetCaseStudy.Application/Handlers/GetLowStockProductsQueryHandler.cs");
        // For test targets, contracts are extracted without implementation details
        relevant["src/NetCaseStudy.Application/Handlers/GetLowStockProductsQueryHandler.cs"].Should().NotContain("return new List<LowStockProductDto>();");
    }

    [Fact]
    public void RepairPrompt_ContainsStrictSurgicalOutputContract()
    {
        var entry = new ManifestFileEntry("NetCaseStudy.Tests/Api/ProductsApiTests.cs", FileEditAction.Modify, "Add tests", null);
        var sysPrompt = DeveloperAgent.BuildSingleFileRepairSystemPrompt(entry);
        var userPrompt = DeveloperAgent.BuildSingleFileRepairUserPrompt("Error", "{}", entry);

        sysPrompt.Should().Contain("Return only compact 'searchReplaceEdits'");
        sysPrompt.Should().Contain("searchReplaceEdits");

        userPrompt.Should().Contain("surgical patch");
        userPrompt.Should().Contain("smallest verbatim search anchors");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_WhenApplicabilityRecoveryHitsTokenLimit_StopsAfterOneRecovery()
    {
        var testProjPath = Path.Combine(_worktreeDir, "NetCaseStudy.Tests", "NetCaseStudy.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(testProjPath)!);
        await File.WriteAllTextAsync(testProjPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.0" />
              </ItemGroup>
            </Project>
            """);

        var testFilePath = Path.Combine(_worktreeDir, "NetCaseStudy.Tests", "Api", "ProductsApiTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFilePath)!);
        var originalContent = CreateLargeProductsApiTestsContent();
        await File.WriteAllTextAsync(testFilePath, originalContent);

        // 1. Initial generation returns an invalid search anchor that fails applicability
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = """
                {
                  "filePath": "NetCaseStudy.Tests/Api/ProductsApiTests.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "NonExistentMethodAnchor()",
                      "replace": "public async Task NewTest() {}"
                    }
                  ]
                }
                """
        });

        // 2. Normal repair response hits token limit (finish_reason = length)
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            ErrorMessage = "Output token limit exceeded"
        });

        // 3. Compact repair retry response succeeds with surgical edit
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = """
                {
                  "filePath": "NetCaseStudy.Tests/Api/ProductsApiTests.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "    [Fact]\n    public async Task ExistingLastTest_ReturnsOk()",
                      "replace": "    [Fact]\n    public async Task GetLowStockProducts_ReturnsOk()\n    {\n        var response = await _client.GetAsync(\"/api/products/low-stock\");\n        response.StatusCode.Should().Be(HttpStatusCode.OK);\n    }\n\n    [Fact]\n    public async Task ExistingLastTest_ReturnsOk()"
                    }
                  ]
                }
                """
        });

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            activityRecorder: _activityRecorder);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add Low Stock API Tests",
            TaskDescription: "Add low-stock endpoint tests",
            AcceptanceCriteria: "Must pass",
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "NetCaseStudy.Tests/Api/ProductsApiTests.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("output token limit");

        var modifiedContent = await File.ReadAllTextAsync(testFilePath);
        modifiedContent.Should().NotContain("GetLowStockProducts_ReturnsOk");
        modifiedContent.Should().Contain("ExistingLastTest_ReturnsOk");

        var messages = _activityRecorder.RecordedMessages.ToList();
        messages.Should().Contain(m => m.StartsWith("Repair triggered for ProductsApiTests.cs"));
        messages.Should().NotContain(m => m.StartsWith("Performing compact repair retry for ProductsApiTests.cs"));

        _fakeAiProvider.ReceivedRequests.Should().HaveCount(2, "applicability recovery owns one bounded call and cannot stack a compact retry");
    }

    [Fact]
    public async Task GenerateAndApplyEditsAsync_Observability_LogsDiagnosticMessagesWithoutLeakingSecrets()
    {
        var testFilePath = Path.Combine(_worktreeDir, "ServiceTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFilePath)!);
        await File.WriteAllTextAsync(testFilePath, "public class ServiceTests { public void Test1() {} }");

        // Initial generation fails with invalid anchor
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = """
                {
                  "filePath": "ServiceTests.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    { "search": "InvalidAnchorToken secret=12345", "replace": "replacement" }
                  ]
                }
                """
        });

        // Repair succeeds
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = """
                {
                  "filePath": "ServiceTests.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    { "search": "public void Test1() {}", "replace": "public void Test1() {}\npublic void Test2() {}" }
                  ]
                }
                """
        });

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            activityRecorder: _activityRecorder);

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Observability Test",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "ServiceTests.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();

        var messages = _activityRecorder.RecordedMessages.ToList();
        messages.Should().Contain(m => m.Contains("Repair triggered for ServiceTests.cs"));

        foreach (var msg in messages)
        {
            msg.Should().NotContain("secret=12345");
            msg.Should().NotContain("SystemPrompt");
            msg.Should().NotContain("UserPrompt");
        }
    }

    [Fact]
    public void TokenLimitPolicy_ModifyRetryNeverUsesFullFileOrTestFileCeiling()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DeveloperAgent:MaxCompactRetryOutputTokens"] = "24576"
        }).Build();

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            configuration: config);

        var largeContent = CreateLargeProductsApiTestsContent();
        var entry = new ManifestFileEntry("NetCaseStudy.Tests/Api/ProductsApiTests.cs", FileEditAction.Modify, "Add tests", null);

        var retryBudget = agent.DetermineCompactRetryBudget(6144, largeContent, entry, isRepair: false);
        retryBudget.Should().Be(8192, "Modify retry is capped by expected compact patch size");

        var repairRetryBudget = agent.DetermineCompactRetryBudget(6144, largeContent, entry, isRepair: true);
        repairRetryBudget.Should().Be(6144, "applicability recovery cannot own another token escalation");
    }

    [Fact]
    public void SemanticSymbolResolution_XUnitFixtureTypes_ResolveWhenXUnitReferenced()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "NetCaseStudy.Tests",
                ProjectPath = "tests/NetCaseStudy.Tests/NetCaseStudy.Tests.csproj",
                ProjectDirectory = "tests/NetCaseStudy.Tests",
                IsTestProject = true,
                PackageReferences = new() { "xunit", "xunit.runner.visualstudio" }
            }
        };

        var code = """
            using Xunit;
            public class SampleTests : IClassFixture<object>, ICollectionFixture<object>, IAsyncLifetime
            {
                private readonly ITestOutputHelper _output;
                public SampleTests(ITestOutputHelper output) { _output = output; }
                public Task InitializeAsync() => Task.CompletedTask;
                public Task DisposeAsync() => Task.CompletedTask;
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "tests/NetCaseStudy.Tests/SampleTests.cs",
            code,
            _tempDir,
            lockedContracts: null,
            projectGraph: projectGraph);

        isValid.Should().BeTrue(errorMessage);
    }

    [Fact]
    public void SemanticSymbolResolution_WebApplicationFactory_ResolvesWhenMvcTestingReferenced()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "NetCaseStudy.Tests",
                ProjectPath = "tests/NetCaseStudy.Tests/NetCaseStudy.Tests.csproj",
                ProjectDirectory = "tests/NetCaseStudy.Tests",
                IsTestProject = true,
                PackageReferences = new() { "Microsoft.AspNetCore.Mvc.Testing", "xunit" }
            }
        };

        var code = """
            using Microsoft.AspNetCore.Mvc.Testing;
            public class ApiTests : IClassFixture<WebApplicationFactory<object>>
            {
                public ApiTests(WebApplicationFactory<object> factory) {}
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "tests/NetCaseStudy.Tests/ApiTests.cs",
            code,
            _tempDir,
            lockedContracts: null,
            projectGraph: projectGraph);

        isValid.Should().BeTrue(errorMessage);
    }

    [Fact]
    public void SemanticSymbolResolution_TestServer_ResolvesWhenTestHostOrMvcTestingReferenced()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "NetCaseStudy.Tests",
                ProjectPath = "tests/NetCaseStudy.Tests/NetCaseStudy.Tests.csproj",
                ProjectDirectory = "tests/NetCaseStudy.Tests",
                IsTestProject = true,
                PackageReferences = new() { "Microsoft.AspNetCore.TestHost" }
            }
        };

        var code = """
            using Microsoft.AspNetCore.TestHost;
            public class ServerTests
            {
                private readonly TestServer _server;
                public ServerTests(TestServer server) { _server = server; }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "tests/NetCaseStudy.Tests/ServerTests.cs",
            code,
            _tempDir,
            lockedContracts: null,
            projectGraph: projectGraph);

        isValid.Should().BeTrue(errorMessage);
    }

    [Fact]
    public void SemanticSymbolResolution_FrameworkAndPackageTypes_AllowedToCompilationWithoutBlocking()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "NetCaseStudy.Domain",
                ProjectPath = "src/NetCaseStudy.Domain/NetCaseStudy.Domain.csproj",
                ProjectDirectory = "src/NetCaseStudy.Domain",
                IsTestProject = false,
                PackageReferences = new() { /* No xUnit or Mvc.Testing */ }
            }
        };

        var xunitCode = "public class DomainClass : IClassFixture<object> {}";
        var (xunitValid, xunitError) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/NetCaseStudy.Domain/DomainClass.cs",
            xunitCode,
            _tempDir,
            lockedContracts: null,
            projectGraph: projectGraph);

        xunitValid.Should().BeTrue("Framework/package types must not be blocked before compilation");
        xunitError.Should().BeNull();

        var factoryCode = "public class DomainFactoryConsumer { public void Run(WebApplicationFactory<object> f) {} }";
        var (factoryValid, factoryError) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/NetCaseStudy.Domain/DomainFactoryConsumer.cs",
            factoryCode,
            _tempDir,
            lockedContracts: null,
            projectGraph: projectGraph);

        factoryValid.Should().BeTrue("WebApplicationFactory must be allowed to compilation without pre-build blocking");
        factoryError.Should().BeNull();

        var serverCode = "public class DomainServerConsumer { public void Run(TestServer s) {} }";
        var (serverValid, serverError) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/NetCaseStudy.Domain/DomainServerConsumer.cs",
            serverCode,
            _tempDir,
            lockedContracts: null,
            projectGraph: projectGraph);

        serverValid.Should().BeTrue("TestServer must be allowed to compilation without pre-build blocking");
        serverError.Should().BeNull();
    }

    [Fact]
    public void SemanticSymbolResolution_UnknownTypes_AllowedToCompilationWithoutBlocking()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "NetCaseStudy.Tests",
                ProjectPath = "tests/NetCaseStudy.Tests/NetCaseStudy.Tests.csproj",
                ProjectDirectory = "tests/NetCaseStudy.Tests",
                IsTestProject = true,
                PackageReferences = new() { "xunit", "Microsoft.AspNetCore.Mvc.Testing" }
            }
        };

        var fixtureCode = """
            using Xunit;
            public class InventedTests : IInventedFixture<object>
            {
            }
            """;

        var (fixtureValid, fixtureError) = RoslynContractExtractor.ValidateSymbolResolution(
            "tests/NetCaseStudy.Tests/InventedTests.cs",
            fixtureCode,
            _tempDir,
            lockedContracts: null,
            projectGraph: projectGraph);

        fixtureValid.Should().BeTrue("Unknown symbols must not be blocked before compiler can evaluate them");
        fixtureError.Should().BeNull();

        var repoCode = """
            public class InventedServiceConsumer
            {
                private readonly IFakeRepositoryService _repo;
            }
            """;

        var (repoValid, repoError) = RoslynContractExtractor.ValidateSymbolResolution(
            "tests/NetCaseStudy.Tests/InventedServiceConsumer.cs",
            repoCode,
            _tempDir,
            lockedContracts: null,
            projectGraph: projectGraph);

        repoValid.Should().BeTrue("Unknown repo-like symbols must defer to compilation diagnostics");
        repoError.Should().BeNull();
    }

    [Fact]
    public void SemanticContractConsistency_HttpClientInProductsApiTests_DoesNotCollideWithProductsControllerContract()
    {
        var productsControllerContract = """
            namespace NetCaseStudy.Api.Controllers;
            public class ProductsController
            {
                public ProductsController(IProductService productService);
                public IActionResult Get(int id);
                public IActionResult GetAll();
            }
            """;

        var lockedContracts = new Dictionary<string, string>
        {
            { "NetCaseStudy.Api/Controllers/ProductsController.cs", productsControllerContract }
        };

        var productsApiTestsCode = """
            using System.Net;
            using System.Net.Http.Json;
            using FluentAssertions;
            using Microsoft.AspNetCore.Mvc.Testing;
            using Xunit;

            namespace NetCaseStudy.Tests.Api;

            public class ProductsApiTests : IClassFixture<WebApplicationFactory<Program>>
            {
                private readonly HttpClient _client;

                public ProductsApiTests(WebApplicationFactory<Program> factory)
                {
                    _client = factory.CreateClient();
                }

                [Fact]
                public async Task Get_ReturnsSuccess()
                {
                    var response = await _client.GetAsync("/api/products/1");
                    response.StatusCode.Should().Be(HttpStatusCode.OK);
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSemanticContractConsistency(
            "tests/NetCaseStudy.Tests/Api/ProductsApiTests.cs",
            productsApiTestsCode,
            lockedContracts);

        isValid.Should().BeTrue("HttpClient receiver calling GetAsync must not be matched against ProductsController.Get");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void SemanticContractConsistency_CallingGetAsyncOnProductsController_IsRejected()
    {
        var productsControllerContract = """
            namespace NetCaseStudy.Api.Controllers;
            public class ProductsController
            {
                public ProductsController(IProductService productService);
                public IActionResult Get(int id);
            }
            """;

        var lockedContracts = new Dictionary<string, string>
        {
            { "NetCaseStudy.Api/Controllers/ProductsController.cs", productsControllerContract }
        };

        var directControllerCallCode = """
            public class ProductsControllerTests
            {
                [Fact]
                public async Task DirectCall()
                {
                    var controller = new ProductsController(null!);
                    await controller.GetAsync(1);
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSemanticContractConsistency(
            "tests/NetCaseStudy.Tests/ProductsControllerTests.cs",
            directControllerCallCode,
            lockedContracts);

        isValid.Should().BeFalse("Direct call to controller.GetAsync must be rejected when controller only defines Get");
        errorMessage.Should().Contain("Semantic contract drift");
        errorMessage.Should().Contain("ProductsController");
    }

    [Fact]
    public void SemanticContractConsistency_RepositoryOwnedMethodDrift_IsRejected()
    {
        var productRepositoryContract = """
            namespace NetCaseStudy.Domain.Interfaces;
            public interface IProductRepository
            {
                Task<Product?> GetAsync(int id);
            }
            """;

        var lockedContracts = new Dictionary<string, string>
        {
            { "NetCaseStudy.Domain/Interfaces/IProductRepository.cs", productRepositoryContract }
        };

        var serviceCode = """
            public class ProductService
            {
                private readonly IProductRepository _repository;

                public ProductService(IProductRepository repository)
                {
                    _repository = repository;
                }

                public void Fetch(int id)
                {
                    _repository.Get(id);
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSemanticContractConsistency(
            "src/NetCaseStudy.Application/Services/ProductService.cs",
            serviceCode,
            lockedContracts);

        isValid.Should().BeFalse("Calling sync Get on IProductRepository when GetAsync is declared must be rejected");
        errorMessage.Should().Contain("Semantic contract drift");
        errorMessage.Should().Contain("IProductRepository");
    }

    [Fact]
    public void SemanticContractConsistency_InventedMethodOnRepositoryContract_IsRejected()
    {
        var productRepositoryContract = """
            namespace NetCaseStudy.Domain.Interfaces;
            public interface IProductRepository
            {
                Task<Product?> GetAsync(int id);
            }
            """;

        var lockedContracts = new Dictionary<string, string>
        {
            { "NetCaseStudy.Domain/Interfaces/IProductRepository.cs", productRepositoryContract }
        };

        var serviceCode = """
            public class ProductService
            {
                private readonly IProductRepository _repository;

                public ProductService(IProductRepository repository)
                {
                    _repository = repository;
                }

                public void Fetch(int id)
                {
                    _repository.CompletelyInventedMethod(id);
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSemanticContractConsistency(
            "src/NetCaseStudy.Application/Services/ProductService.cs",
            serviceCode,
            lockedContracts);

        isValid.Should().BeFalse("Calling invented method on IProductRepository must be rejected");
        errorMessage.Should().Contain("does not exist on locked contract 'IProductRepository'");
    }

    [Fact]
    public void RealSystemNetHttpClient_GetAsync_Accepted()
    {
        var lockedContracts = new Dictionary<string, string>
        {
            { "NetCaseStudy.Api/Controllers/ProductsController.cs", "namespace NetCaseStudy.Api.Controllers;\npublic class ProductsController { public IActionResult Get() => null; }" }
        };

        var testCode = """
            using System.Net.Http;
            using System.Threading.Tasks;

            public class ProductsApiTests
            {
                private readonly HttpClient _client;

                public ProductsApiTests(HttpClient client)
                {
                    _client = client;
                }

                public async Task Test()
                {
                    var response = await _client.GetAsync("/api/products");
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSemanticContractConsistency(
            "tests/NetCaseStudy.Api.Tests/ProductsApiTests.cs",
            testCode,
            lockedContracts);

        isValid.Should().BeTrue("Real System.Net.Http.HttpClient.GetAsync must not collide with ProductsController.Get");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void RepositoryOwned_FakeHttpClient_GetAsync_DoesNotGetGlobalBypass_IsRejected()
    {
        var lockedContracts = new Dictionary<string, string>
        {
            { "src/NetCaseStudy.Infrastructure/Http/HttpClient.cs", "namespace NetCaseStudy.Infrastructure.Http;\npublic class HttpClient { public string Get(string url) => url; }" }
        };

        var consumerCode = """
            using NetCaseStudy.Infrastructure.Http;
            using System.Threading.Tasks;

            public class MyService
            {
                private readonly HttpClient _customClient;

                public MyService(HttpClient customClient)
                {
                    _customClient = customClient;
                }

                public async Task Execute()
                {
                    await _customClient.GetAsync("https://example.com");
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSemanticContractConsistency(
            "src/NetCaseStudy.Application/Services/MyService.cs",
            consumerCode,
            lockedContracts);

        isValid.Should().BeFalse("Repository-owned fake HttpClient must not be bypassed by global short name whitelist");
        errorMessage.Should().Contain("Semantic contract drift");
        errorMessage.Should().Contain("defines 'Get'");
    }

    [Fact]
    public void RealReferenced_WebApplicationFactory_Accepted()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "NetCaseStudy.Api.Tests",
                ProjectDirectory = "tests/NetCaseStudy.Api.Tests",
                ProjectPath = "tests/NetCaseStudy.Api.Tests/NetCaseStudy.Api.Tests.csproj",
                IsTestProject = true,
                PackageReferences = new List<string> { "Microsoft.AspNetCore.Mvc.Testing", "xunit", "FluentAssertions" },
                ProjectReferences = new List<string> { "src/NetCaseStudy.Api/NetCaseStudy.Api.csproj" }
            }
        };

        var testCode = """
            using Microsoft.AspNetCore.Mvc.Testing;
            using Xunit;

            public class IntegrationTests
            {
                private readonly WebApplicationFactory<Program> _factory;

                public IntegrationTests(WebApplicationFactory<Program> factory)
                {
                    _factory = factory;
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "tests/NetCaseStudy.Api.Tests/IntegrationTests.cs",
            testCode,
            projectGraph,
            null);

        isValid.Should().BeTrue("Referenced WebApplicationFactory in test project must be accepted");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void MissingPackageReference_DefersToCompilationDiagnostic()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "NetCaseStudy.Domain",
                ProjectDirectory = "src/NetCaseStudy.Domain",
                ProjectPath = "src/NetCaseStudy.Domain/NetCaseStudy.Domain.csproj",
                IsTestProject = false,
                PackageReferences = new List<string>(),
                ProjectReferences = new List<string>()
            }
        };

        var code = """
            using MediatR;

            public class MyQuery : IRequest<string>
            {
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateProjectArchitecturalDependencies(
            "src/NetCaseStudy.Domain/MyQuery.cs",
            code,
            projectGraph,
            null);

        isValid.Should().BeTrue("Project architectural dependencies must defer to authoritative compiler diagnostics");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void TargetProject_WithAutoMapper_IMapperInHandler_Accepted()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "NetCaseStudy.Application",
                ProjectDirectory = "src/NetCaseStudy.Application",
                ProjectPath = "src/NetCaseStudy.Application/NetCaseStudy.Application.csproj",
                IsTestProject = false,
                PackageReferences = new List<string> { "AutoMapper", "MediatR" },
                ProjectReferences = new List<string>()
            }
        };

        var handlerCode = """
            using AutoMapper;
            using System.Threading;
            using System.Threading.Tasks;

            namespace NetCaseStudy.Application.Features.Products.Queries;

            public class ListProductsQueryHandler
            {
                private readonly IMapper _mapper;

                public ListProductsQueryHandler(IMapper mapper)
                {
                    _mapper = mapper;
                }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/NetCaseStudy.Application/Features/Products/Queries/ListProductsQueryHandler.cs",
            handlerCode,
            workspacePath: "C:/fake/path",
            lockedContracts: null,
            projectGraph: projectGraph);

        isValid.Should().BeTrue("Referenced AutoMapper package in application project must allow IMapper in handler");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void TargetProject_WithoutAutoMapper_IMapper_AllowedToCompilation()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "NetCaseStudy.Domain",
                ProjectDirectory = "src/NetCaseStudy.Domain",
                ProjectPath = "src/NetCaseStudy.Domain/NetCaseStudy.Domain.csproj",
                IsTestProject = false,
                PackageReferences = new List<string>(),
                ProjectReferences = new List<string>()
            }
        };

        var code = """
            using AutoMapper;

            namespace NetCaseStudy.Domain.Entities;

            public class Product
            {
                public void Map(IMapper mapper) {}
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/NetCaseStudy.Domain/Entities/Product.cs",
            code,
            workspacePath: "C:/fake/path",
            lockedContracts: null,
            projectGraph: projectGraph);

        isValid.Should().BeTrue("IMapper without AutoMapper in project must be allowed to compilation for authoritative Roslyn diagnostics");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void TargetProject_WithAutoMapper_MissingUsing_AllowedToCompilation()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new DiscoveredProjectNode
            {
                ProjectName = "NetCaseStudy.Application",
                ProjectDirectory = "src/NetCaseStudy.Application",
                ProjectPath = "src/NetCaseStudy.Application/NetCaseStudy.Application.csproj",
                IsTestProject = false,
                PackageReferences = new List<string> { "AutoMapper" },
                ProjectReferences = new List<string>()
            }
        };

        var queryWithMissingUsing = """
            namespace NetCaseStudy.Application.Features.Products.Queries;

            public class ListProductsQuery
            {
                public IMapper Mapper { get; set; }
            }
            """;

        var (isValid, errorMessage) = RoslynContractExtractor.ValidateSymbolResolution(
            "src/NetCaseStudy.Application/Features/Products/Queries/ListProductsQuery.cs",
            queryWithMissingUsing,
            workspacePath: "C:/fake/path",
            lockedContracts: null,
            projectGraph: projectGraph);

        isValid.Should().BeTrue("Using IMapper without using AutoMapper must reach compilation where CS0246 will be reported");
        errorMessage.Should().BeNull();
    }

    [Fact]
    public void RepairPrompt_ContainsArchitecturalGuidance_ForUnresolvedServiceInQuery()
    {
        var fileEntry = new ManifestFileEntry(
            "src/NetCaseStudy.Application/Features/Products/Queries/ListProductsQuery.cs",
            FileEditAction.Modify,
            "Add Search property to query");

        var prompt = DeveloperAgent.BuildSingleFileRepairUserPrompt(
            "Unresolved symbol 'IMapper' detected in 'src/NetCaseStudy.Application/Features/Products/Queries/ListProductsQuery.cs'. Package 'AutoMapper' is referenced by the project, but 'using AutoMapper;' is missing in this file, or this dependency does not belong in this file.",
            previousResponse: "{\"edits\": []}",
            fileEntry: fileEntry);

        prompt.Should().Contain("ARCHITECTURAL DEPENDENCY REPAIR GUIDANCE");
        prompt.Should().Contain("Queries, Commands, and DTOs");
        prompt.Should().Contain("must ONLY contain query parameters and filter data");
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

    private sealed class FakeExecutionActivityRecorder : IExecutionActivityRecorder
    {
        public ConcurrentQueue<string> RecordedMessages { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            RecordedMessages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
