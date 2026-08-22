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

    private static string CreateExtremelyLargeProductsApiTestsContent()
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

        for (int i = 1; i <= 100; i++)
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
        var largeContent = CreateExtremelyLargeProductsApiTestsContent();
        largeContent.Split('\n').Length.Should().BeGreaterThan(600);

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
        prompt.Should().Contain("=== Insertion / End of Class Anchor ===");
        prompt.Should().Contain("ExistingLastTest_ReturnsOk");

        // Prompt must NOT dump intermediate test methods verbatim
        prompt.Should().Contain("omitted for brevity");
        prompt.Should().NotContain("GetProduct_40_ReturnsOk");
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

    [Theory]
    [InlineData(4096, 8192)]
    [InlineData(8192, 8192)]
    [InlineData(16384, 8192)]
    public void DetermineCompactRetryBudget_ModifyAction_HardCappedAt8192EvenWithLargeFileOrHighConfig(int initialBudget, int expectedMax)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxCompactRetryOutputTokens"] = "32768",
                ["DeveloperAgent:MaxOutputTokens"] = "32768"
            })
            .Build();

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            configuration: config,
            activityRecorder: _activityRecorder);

        var largeContent = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"// Line {i} of code"));
        var fileEntry = new ManifestFileEntry("src/App/LargeController.cs", FileEditAction.Modify, "Add method");

        var budget = agent.DetermineCompactRetryBudget(initialBudget, largeContent, fileEntry, isRepair: false);
        budget.Should().BeLessOrEqualTo(8192);
        budget.Should().Be(expectedMax);
    }

    [Fact]
    public void DetermineCompactRetryBudget_ModifyRepair_NeverExceedsInitialBudgetOr8192()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeveloperAgent:MaxCompactRetryOutputTokens"] = "32768"
            })
            .Build();

        var agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            configuration: config,
            activityRecorder: _activityRecorder);

        var fileEntry = new ManifestFileEntry("src/App/TestFile.cs", FileEditAction.Modify, "Fix bug");

        var budget = agent.DetermineCompactRetryBudget(4096, "content", fileEntry, isRepair: true);
        budget.Should().Be(4096);
    }

    [Fact]
    public void TodoService_StyleLocalizedModify_PromptDoesNotRequestFullFileOutput()
    {
        var todoServiceContent = """
            using System.Collections.Concurrent;
            using TodoApi.Models;

            namespace TodoApi.Services;

            public class TodoService : ITodoService
            {
                private readonly ConcurrentDictionary<Guid, TodoItem> _items = new();
                private readonly ITodoAuditLogger _auditLogger;

                public TodoService(ITodoAuditLogger auditLogger)
                {
                    _auditLogger = auditLogger;
                }

                public IReadOnlyList<TodoItem> GetAll()
                {
                    return _items.Values.OrderBy(x => x.CreatedAtUtc).ToList();
                }

                public TodoItem? GetById(Guid id)
                {
                    return _items.TryGetValue(id, out var item) ? item : null;
                }

                public TodoItem Create(CreateTodoRequest request)
                {
                    ArgumentNullException.ThrowIfNull(request);
                    var item = new TodoItem { Id = Guid.NewGuid(), Title = request.Title.Trim() };
                    _items[item.Id] = item;
                    return item;
                }

                public bool Update(Guid id, UpdateTodoRequest request)
                {
                    ArgumentNullException.ThrowIfNull(request);
                    if (!_items.TryGetValue(id, out var existing)) return false;
                    existing.Title = request.Title.Trim();
                    return true;
                }

                public bool Delete(Guid id)
                {
                    return _items.TryRemove(id, out _);
                }
            }
            """;

        // 78-line service is not treated as a small-file full replacement
        WorktreeEditApplier.IsSmallTextFile(todoServiceContent).Should().BeFalse();

        var entry = new ManifestFileEntry("src/TodoApi/Services/TodoService.cs", FileEditAction.Modify, "Verify thread-safety");
        var systemPrompt = DeveloperAgent.BuildSingleFileSystemPrompt(entry, useFullFileReplacement: false);

        systemPrompt.Should().Contain("large-file Modify");
        systemPrompt.Should().Contain("searchReplaceEdits");
        systemPrompt.Should().NotContain("small-file Modify");
        systemPrompt.Should().NotContain("Return the complete resulting file once in 'newContent'");
    }

    [Fact]
    public void LargeRepetitiveTestFile_AddingOneTest_SendsBoundedRelevantContext_NotEntireSuite()
    {
        var todosControllerTestsContent = """
            using FluentAssertions;
            using Microsoft.AspNetCore.Mvc;
            using TodoApi.Controllers;
            using TodoApi.Models;
            using TodoApi.Services;
            using Xunit;

            namespace TodoApi.Tests;

            public class TodosControllerTests
            {
                private readonly TodoService _service;
                private readonly TodosController _controller;

                public TodosControllerTests()
                {
                    var auditLogger = new TodoAuditLogger();
                    _service = new TodoService(auditLogger);
                    _controller = new TodosController(_service);
                }

                [Fact]
                public void GetAll_ReturnsOkWithList()
                {
                    _service.Create(new CreateTodoRequest { Title = "Controller test 1" });
                    var result = _controller.GetAll();
                    var okResult = result.Result as OkObjectResult;
                    okResult.Should().NotBeNull();
                }

                [Fact]
                public void GetById_ExistingItem_ReturnsOkWithItem()
                {
                    var created = _service.Create(new CreateTodoRequest { Title = "Find me" });
                    var result = _controller.GetById(created.Id);
                    result.Should().NotBeNull();
                }

                [Fact]
                public void GetById_NonExistentItem_ReturnsNotFound()
                {
                    var result = _controller.GetById(Guid.NewGuid());
                    result.Result.Should().BeOfType<NotFoundResult>();
                }

                [Fact]
                public void Create_ValidRequest_ReturnsCreatedAtAction()
                {
                    var result = _controller.Create(new CreateTodoRequest { Title = "New item" });
                    result.Should().NotBeNull();
                }

                [Fact]
                public void Create_EmptyTitle_ReturnsBadRequest()
                {
                    var result = _controller.Create(new CreateTodoRequest { Title = "" });
                    result.Result.Should().BeOfType<BadRequestObjectResult>();
                }
            }
            """;

        var bounded = DeveloperAgent.BuildBoundedTargetSourceWindow(
            todosControllerTestsContent,
            applicabilityFailure: null,
            isTestFile: true,
            purpose: "Add thread-safety test");

        // Preserves constructor & fixture setup
        bounded.Should().Contain("public TodosControllerTests()");
        bounded.Should().Contain("_service = new TodoService(auditLogger);");

        // Summarizes existing intermediate tests without dumping their bodies
        bounded.Should().Contain("// === Existing Tests");
        bounded.Should().Contain("GetAll_ReturnsOkWithList");
        bounded.Should().Contain("GetById_ExistingItem_ReturnsOkWithItem");
        bounded.Should().NotContain("Controller test 1");

        // Preserves verbatim insertion anchor at end of class
        bounded.Should().Contain("// === Insertion / End of Class Anchor ===");
        bounded.Should().Contain("Create_EmptyTitle_ReturnsBadRequest");
        bounded.Should().Contain("BadRequestObjectResult");

        // Substantially smaller than full test file
        bounded.Length.Should().BeLessThan(todosControllerTestsContent.Length);
    }

    [Fact]
    public void CompactRetry_InputIsMateriallySmallerThanInitialRequest_AndDoesNotRepeatBroadUnrelatedTaskContext()
    {
        var targetFile = "tests/TodoApi.Tests/TodosControllerTests.cs";
        var fileEntry = new ManifestFileEntry(targetFile, FileEditAction.Modify, "Add thread safety test");
        var contextFiles = new Dictionary<string, string>
        {
            [targetFile] = """
                using Xunit;
                namespace TodoApi.Tests;
                public class TodosControllerTests
                {
                    public TodosControllerTests() { }
                    [Fact] public void Test1() { }
                    [Fact] public void Test2() { }
                    [Fact] public void Test3() { }
                    [Fact] public void Test4() { }
                    [Fact] public void Test5() { }
                }
                """
        };

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Singleton Servislerin Thread-Safety Açısından Doğrulanması",
            TaskDescription: "A very broad and detailed task description that explains why singleton services need thread safety verification, how concurrent dictionary is used, how multiple worker threads should access the API concurrently, and how audit logs must be asserted.",
            AcceptanceCriteria: "- Thread safety is verified under 50 concurrent tasks\n- No data race occurs\n- Audit logger captures all concurrent actions safely",
            ImpactAnalysisSummary: "Affects TodoService and TodosControllerTests",
            ProposedPlan: "1. Update TodoService with ConcurrentDictionary\n2. Add concurrent tests to TodosControllerTests",
            ImpactedFilePaths: new[] { targetFile },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var initialUserPrompt = DeveloperAgent.BuildSingleFileUserPrompt(
            request, fileEntry, contextFiles, completedEdits: null, projectGraph: Array.Empty<DiscoveredProjectNode>(), lockedContracts: null, referencePattern: null, useFullFileReplacement: false);

        var compactUserPrompt = DeveloperAgent.BuildCompactSingleFileUserPrompt(
            request, fileEntry, contextFiles[targetFile], lockedContracts: null, useFullFileReplacement: false);

        // Compact prompt is materially smaller
        compactUserPrompt.Length.Should().BeLessThan(initialUserPrompt.Length);

        // Compact prompt does not repeat broad task description
        compactUserPrompt.Should().NotContain("A very broad and detailed task description that explains why singleton services");

        // Compact prompt focuses on target file and purpose
        compactUserPrompt.Should().Contain($"Target File: {targetFile}");
        compactUserPrompt.Should().Contain("Add thread safety test");
        compactUserPrompt.Should().Contain("surgical patch");
    }

    [Fact]
    public void GeneratedEditRepresentation_CannotRequireReproducingUnrelatedTests()
    {
        var entry = new ManifestFileEntry("tests/TodoApi.Tests/TodosControllerTests.cs", FileEditAction.Modify, "Add test");

        // 1. Rejects full-file replacement on large/test Modify
        var fullFileSpec = new FileEditSpec("tests/TodoApi.Tests/TodosControllerTests.cs", FileEditAction.Modify, NewContent: "full file content", SearchReplaceEdits: null);
        var actFull = () => DeveloperAgent.ValidateSingleFileEditSpec(fullFileSpec, entry, targetContent: "some existing target", useFullFileReplacement: false);
        actFull.Should().Throw<FormatException>().WithMessage("*must use surgical 'searchReplaceEdits'*");

        // 2. Rejects single search block reproducing entire target file
        var largeTarget = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"[Fact] public void Test_{i}() {{ }}"));
        var monolithicSpec = new FileEditSpec("tests/TodoApi.Tests/TodosControllerTests.cs", FileEditAction.Modify, NewContent: null, SearchReplaceEdits: new[]
        {
            new SearchReplaceEdit(largeTarget, "replacement")
        });

        var actMonolithic = () => DeveloperAgent.ValidateSingleFileEditSpec(monolithicSpec, entry, targetContent: largeTarget, useFullFileReplacement: false);
        actMonolithic.Should().Throw<FormatException>().WithMessage("*effectively reproduces the entire file*");
    }

    [Fact]
    public async Task InitialExhaustion_Plus_CompactRetryExhaustion_TerminatesAsToday_NoExtraRetry()
    {
        var targetFile = Path.Combine(_worktreeDir, "Service.cs");
        await File.WriteAllTextAsync(targetFile, "public class Service { public int V = 1; }");

        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            ErrorMessage = "Output token limit exceeded"
        });
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            ErrorMessage = "Output token limit exceeded"
        });

        var agent = new DeveloperAgent(_fakeAiProvider, _editApplier, NullLogger<DeveloperAgent>.Instance, activityRecorder: _activityRecorder);
        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update Service",
            TaskDescription: "Change value",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Service.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exhausted");
        // Exactly 2 calls: 1 initial + 1 compact retry. No 3rd call.
        _fakeAiProvider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public void ExistingBudgetCeiling_RemainsUnchanged()
    {
        var agent = new DeveloperAgent(_fakeAiProvider, _editApplier, NullLogger<DeveloperAgent>.Instance, activityRecorder: _activityRecorder);
        var fileEntry = new ManifestFileEntry("src/App/LargeService.cs", FileEditAction.Modify, "Update");

        var initialBudget = agent.DetermineInitialBudget("src/App/LargeService.cs", FileEditAction.Modify, "content");
        initialBudget.Should().BeLessThanOrEqualTo(8192);

        var compactBudget = agent.DetermineCompactRetryBudget(initialBudget, "content", fileEntry, isRepair: false);
        compactBudget.Should().BeLessThanOrEqualTo(8192);
    }

    [Fact]
    public void SmallFileReplacementContract_RemainsUnchanged()
    {
        const string smallContent = "public class SmallDto { public int Id { get; set; } }";
        WorktreeEditApplier.IsSmallTextFile(smallContent).Should().BeTrue();

        var entry = new ManifestFileEntry("src/App/SmallDto.cs", FileEditAction.Modify, "Add property");
        var prompt = DeveloperAgent.BuildSingleFileSystemPrompt(entry, useFullFileReplacement: true);

        prompt.Should().Contain("small-file Modify");
        prompt.Should().Contain("Return the complete resulting file once in 'newContent'");
    }

    [Fact]
    public void CreateContract_RemainsUnchanged()
    {
        var entry = new ManifestFileEntry("src/App/NewService.cs", FileEditAction.Create, "Create new service");
        var prompt = DeveloperAgent.BuildSingleFileSystemPrompt(entry, useFullFileReplacement: false);

        prompt.Should().Contain("For 'Create' actions, specify 'newContent'");
    }

    [Fact]
    public void ExistingApplicabilityBehavior_RemainsUnchanged()
    {
        var failure = EditApplicabilityResult.Fail(
            errorMessage: "Search text not found",
            failedEditIndex: 1,
            totalEdits: 1,
            failedSearch: "nonExistentMethod()",
            failedReplace: "replace",
            matchCount: 0,
            surroundingContext: "public class Target { public void ActualMethod() { } }");

        var prompt = DeveloperAgent.BuildSingleFileRepairUserPrompt(
            "Applicability error",
            "previous response",
            new ManifestFileEntry("src/App/Target.cs", FileEditAction.Modify, "Fix method"),
            currentTargetContent: "public class Target { public void ActualMethod() { } }",
            applicabilityFailure: failure);

        prompt.Should().Contain("zero matches (the search text was not found");
        prompt.Should().Contain("nonExistentMethod()");
        prompt.Should().Contain("Surrounding Target Source Context");
    }

    [Fact]
    public async Task PromptSizeTelemetry_RecordedForModifyGenerationAndCompactRetry()
    {
        var targetFile = Path.Combine(_worktreeDir, "Service.cs");
        await File.WriteAllTextAsync(targetFile, "public class Service { public int Counter = 0; }");

        // Initial call fails with length to trigger compact retry
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = false,
            FinishReason = "length",
            FailureKind = AiFailureKind.TokenLimitExceeded,
            ErrorMessage = "Output token limit exceeded"
        });

        // Compact retry succeeds with surgical searchReplaceEdits
        _fakeAiProvider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            FinishReason = "stop",
            Content = """
                {
                  "filePath": "Service.cs",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "Counter = 0;",
                      "replace": "Counter = 1;"
                    }
                  ]
                }
                """
        });

        var agent = new DeveloperAgent(_fakeAiProvider, _editApplier, NullLogger<DeveloperAgent>.Instance, activityRecorder: _activityRecorder);
        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Update Service",
            TaskDescription: "Change counter",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[] { "Service.cs" },
            WorkspacePath: _worktreeDir,
            BranchName: _branchName);

        var result = await agent.GenerateAndApplyEditsAsync(request);

        result.Success.Should().BeTrue();

        var providerActivities = _activityRecorder.RecordedActivities
            .Where(a => a.Metadata?.EventKind == "ProviderCall")
            .ToList();

        providerActivities.Should().HaveCount(2);

        // Initial Generation telemetry (small Service.cs was full file replacement)
        var initialMeta = providerActivities[0].Metadata;
        initialMeta.Should().NotBeNull();
        initialMeta!.ProviderCallKind.Should().Be("Generation");
        initialMeta.TargetSourceChars.Should().BeGreaterThan(0);
        initialMeta.TotalPromptChars.Should().BeGreaterThan(0);
        initialMeta.EditStrategy.Should().BeOneOf("FullFileReplacement", "SurgicalPatch");

        // Compact Retry telemetry (switched to surgical patch)
        var retryMeta = providerActivities[1].Metadata;
        retryMeta.Should().NotBeNull();
        retryMeta!.ProviderCallKind.Should().Be("CompactGenerationRetry");
        retryMeta.RetryPromptChars.Should().BeGreaterThan(0);
        retryMeta.EditStrategy.Should().Be("SurgicalPatch");
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
        public ConcurrentQueue<(string Message, ExecutionActivityMetadata? Metadata)> RecordedActivities { get; } = new();
        public IEnumerable<string> RecordedMessages => RecordedActivities.Select(a => a.Message);

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            RecordedActivities.Enqueue((message, metadata));
            return Task.CompletedTask;
        }
    }
}
