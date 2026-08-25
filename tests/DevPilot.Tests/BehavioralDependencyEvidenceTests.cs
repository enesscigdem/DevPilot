using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests;

public sealed class BehavioralDependencyEvidenceTests : IDisposable
{
    private readonly string _tempDir;

    public BehavioralDependencyEvidenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotBehavioralEvidence_" + Guid.NewGuid().ToString("N"));
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
            // Ignore temp cleanup.
        }
    }

    [Fact]
    public void TestRepairTarget_WithStrongTouchedProductionDependency_ReceivesLatestFreshBehavioralSource()
    {
        var factory = CustomWebApplicationFactorySource();
        var freshProgram = FreshProgramSource("UseInMemoryDatabase(\"FreshDb\")");
        var excerpts = BehavioralDependencyEvidence.Collect(
            "tests/TestUtils/CustomWebApplicationFactory.cs",
            factory,
            new[] { "src/Program.cs", "src/UnrelatedService.cs" },
            freshSources: new Dictionary<string, string>
            {
                ["src/Program.cs"] = freshProgram,
                ["src/UnrelatedService.cs"] = "public class UnrelatedService { public int Value => 1; }"
            },
            originalSnapshots: new Dictionary<string, string>
            {
                ["src/Program.cs"] = "public class Program { /* STALE_ORIGINAL_SNAPSHOT */ }"
            },
            diagnosticEvidence: "Services for database providers Microsoft.EntityFrameworkCore.SqlServer and Microsoft.EntityFrameworkCore.InMemory have been registered in the service provider.");

        excerpts.Should().ContainSingle(item => item.FilePath == "src/Program.cs");
        excerpts[0].Excerpt.Should().Contain("UseInMemoryDatabase(\"FreshDb\")");
        excerpts[0].Excerpt.Should().Contain("AddDbContext");
        excerpts[0].Excerpt.Length.Should().BeLessThanOrEqualTo(BehavioralDependencyEvidence.MaxExcerptChars);

        var request = new FocusedRepairRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Wire test host",
            "Resolve the failing test without weakening existing test assertions.",
            _tempDir,
            "devpilot/test",
            new[] { "tests/TestUtils/CustomWebApplicationFactory.cs" },
            "NetCaseStudy.Tests.PipelineTests.Boots FAILED: Microsoft.EntityFrameworkCore.SqlServer and Microsoft.EntityFrameworkCore.InMemory were both registered",
            TestName: "NetCaseStudy.Tests.PipelineTests.Boots");
        var prompt = DeveloperAgent.BuildFocusedDiagnosticRepairUserPrompt(
            "tests/TestUtils/CustomWebApplicationFactory.cs",
            factory,
            request,
            peerContext: new Dictionary<string, string>(),
            behavioralEvidence: excerpts,
            behavioralSignatures: BehavioralDependencyEvidence.CollectLockedSignatures(
                excerpts,
                new Dictionary<string, string> { ["src/Program.cs"] = freshProgram }));

        prompt.Should().Contain("=== Fresh Behavioral Dependency Evidence ===");
        prompt.Should().Contain("Use this only to understand the exact existing/generated runtime wiring");
        prompt.Should().Contain("Fix the production/test-fixture behavioral incompatibility shown by the failure. Do not merely make the file compile.");
        prompt.Should().Contain("Failing Test: NetCaseStudy.Tests.PipelineTests.Boots");
        prompt.Should().Contain("UseInMemoryDatabase(\"FreshDb\")");
        prompt.Should().Contain("=== Relevant Locked Signatures ===");
        prompt.Should().NotContain("STALE_ORIGINAL_SNAPSHOT");
        prompt.Should().NotContain("UnrelatedService");
    }

    [Fact]
    public void StaleOriginalProducerSource_IsNotPreferred_OverFreshGeneratedSource()
    {
        var excerpts = BehavioralDependencyEvidence.Collect(
            "tests/TestUtils/CustomWebApplicationFactory.cs",
            CustomWebApplicationFactorySource(),
            new[] { "src/Program.cs" },
            freshSources: new Dictionary<string, string>
            {
                ["src/Program.cs"] = FreshProgramSource("UseInMemoryDatabase(\"VirtualWorkspace\")")
            },
            originalSnapshots: new Dictionary<string, string>
            {
                ["src/Program.cs"] = FreshProgramSource("/* STALE_ORIGINAL_SNAPSHOT */ UseSqlServer(\"Old\")")
            },
            diagnosticEvidence: "Microsoft.EntityFrameworkCore.InMemory and Microsoft.EntityFrameworkCore.SqlServer were both registered");

        excerpts.Should().ContainSingle();
        excerpts[0].Excerpt.Should().Contain("UseInMemoryDatabase(\"VirtualWorkspace\")");
        excerpts[0].Excerpt.Should().NotContain("STALE_ORIGINAL_SNAPSHOT");
    }

    [Fact]
    public void UnrelatedTouchedFiles_AreExcluded()
    {
        var excerpts = BehavioralDependencyEvidence.Collect(
            "tests/TestUtils/CustomWebApplicationFactory.cs",
            CustomWebApplicationFactorySource(),
            new[] { "src/Program.cs", "src/UnrelatedService.cs", "src/Comments.md" },
            freshSources: new Dictionary<string, string>
            {
                ["src/Program.cs"] = FreshProgramSource("UseInMemoryDatabase(\"FactoryDb\")"),
                ["src/UnrelatedService.cs"] = "public class UnrelatedService { public string Name => \"UNRELATED_TOUCHED_FILE\"; }",
                ["src/Comments.md"] = "UNRELATED_MARKDOWN"
            },
            diagnosticEvidence: "Microsoft.EntityFrameworkCore.SqlServer and Microsoft.EntityFrameworkCore.InMemory");

        excerpts.Should().ContainSingle(item => item.FilePath == "src/Program.cs");
        excerpts.Should().NotContain(item => item.FilePath.Contains("UnrelatedService", StringComparison.OrdinalIgnoreCase));
        excerpts.Should().NotContain(item => item.Excerpt.Contains("UNRELATED_TOUCHED_FILE"));
        excerpts.Should().NotContain(item => item.Excerpt.Contains("UNRELATED_MARKDOWN"));
    }

    [Fact]
    public void Evidence_IsBoundedToMaxTwoFiles()
    {
        var factory = """
            public class CustomWebApplicationFactory : WebApplicationFactory<Program>
            {
                private readonly StartupSupport _startup = new();
                private readonly HostingSupport _hosting = new();
            }
            """;
        var excerpts = BehavioralDependencyEvidence.Collect(
            "tests/CustomWebApplicationFactory.cs",
            factory,
            new[] { "src/Program.cs", "src/StartupSupport.cs", "src/HostingSupport.cs" },
            freshSources: new Dictionary<string, string>
            {
                ["src/Program.cs"] = "public class Program { public static void Main() { builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer()); } }",
                ["src/StartupSupport.cs"] = "public class StartupSupport { public void Configure() { services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(\"a\")); } }",
                ["src/HostingSupport.cs"] = "public class HostingSupport { public void Configure() { services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(\"b\")); } }"
            },
            diagnosticEvidence: "Microsoft.EntityFrameworkCore.SqlServer and Microsoft.EntityFrameworkCore.InMemory AddDbContext");

        excerpts.Should().HaveCountLessThanOrEqualTo(2);
        excerpts.Should().OnlyContain(item => item.Excerpt.Length <= BehavioralDependencyEvidence.MaxExcerptChars);
    }

    [Fact]
    public void ProductionCompileTarget_DoesNotReceiveBehavioralEvidence()
    {
        var excerpts = BehavioralDependencyEvidence.Collect(
            "src/Program.cs",
            FreshProgramSource("UseSqlServer()"),
            new[] { "src/Program.cs", "tests/CustomWebApplicationFactory.cs" },
            freshSources: new Dictionary<string, string>
            {
                ["src/Program.cs"] = FreshProgramSource("UseSqlServer()"),
                ["tests/CustomWebApplicationFactory.cs"] = CustomWebApplicationFactorySource()
            },
            diagnosticEvidence: "error CS1002: ; expected");

        excerpts.Should().BeEmpty();
    }

    [Fact]
    public void FactoryRepair_WithActuallyModifiedProducerOnly_StillReceivesFreshProgramEvidence()
    {
        var factory = CustomWebApplicationFactorySource();
        var freshProgram = FreshProgramSource("UseInMemoryDatabase(\"FreshDb\")");
        var excerpts = BehavioralDependencyEvidence.Collect(
            "tests/TestUtils/CustomWebApplicationFactory.cs",
            factory,
            new[] { "src/Program.cs" },
            freshSources: new Dictionary<string, string>
            {
                ["src/Program.cs"] = freshProgram
            },
            diagnosticEvidence: "Microsoft.EntityFrameworkCore.SqlServer and Microsoft.EntityFrameworkCore.InMemory were both registered");

        excerpts.Should().ContainSingle(item => item.FilePath == "src/Program.cs");
        excerpts[0].Excerpt.Should().Contain("UseInMemoryDatabase(\"FreshDb\")");
        excerpts.Should().HaveCountLessThanOrEqualTo(2);
    }

    [Fact]
    public void CollectFocusedRepairBehavioralEvidence_ReadsFreshWorkspaceOverMissingSnapshot()
    {
        var programPath = Path.Combine(_tempDir, "src");
        var factoryPath = Path.Combine(_tempDir, "tests", "TestUtils");
        Directory.CreateDirectory(programPath);
        Directory.CreateDirectory(factoryPath);
        File.WriteAllText(Path.Combine(programPath, "Program.cs"), FreshProgramSource("UseInMemoryDatabase(\"DiskFresh\")"));
        File.WriteAllText(Path.Combine(factoryPath, "CustomWebApplicationFactory.cs"), CustomWebApplicationFactorySource());

        var request = new FocusedRepairRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Fix factory",
            "Resolve the failing test",
            _tempDir,
            "devpilot/test",
            new[] { "tests/TestUtils/CustomWebApplicationFactory.cs" },
            "Microsoft.EntityFrameworkCore.SqlServer and Microsoft.EntityFrameworkCore.InMemory were both registered",
            TouchedFiles: new[] { "src/Program.cs", "src/UnrelatedService.cs" },
            TestName: "PipelineTests.Boots");

        var excerpts = DeveloperAgent.CollectFocusedRepairBehavioralEvidence(
            "tests/TestUtils/CustomWebApplicationFactory.cs",
            CustomWebApplicationFactorySource(),
            request);

        excerpts.Should().ContainSingle(item => item.FilePath == "src/Program.cs");
        excerpts[0].Excerpt.Should().Contain("UseInMemoryDatabase(\"DiskFresh\")");
    }

    private static string CustomWebApplicationFactorySource() =>
        """
        using Microsoft.AspNetCore.Mvc.Testing;
        public class CustomWebApplicationFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureServices(services => { });
            }
        }
        """;

    private static string FreshProgramSource(string databaseLine) =>
        $$"""
        public class Program
        {
            public static void Main(string[] args)
            {
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddDbContext<AppDbContext>(options =>
                    options.{{databaseLine}});
                builder.Services.AddControllers();
                var app = builder.Build();
                app.Run();
            }
        }
        """;
}
