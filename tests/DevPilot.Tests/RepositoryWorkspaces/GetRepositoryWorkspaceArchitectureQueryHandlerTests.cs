using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceAnalysis;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceArchitecture;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.RepositoryInspection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.RepositoryWorkspaces;

public class GetRepositoryWorkspaceArchitectureQueryHandlerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly FakeRepositoryWorkspaceQuery _workspaceQuery = new();
    private readonly RepositoryStructureScanner _structureScanner = new(NullLogger<RepositoryStructureScanner>.Instance);

    public GetRepositoryWorkspaceArchitectureQueryHandlerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevPilotArchTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task HandleAsync_WorkspaceNotFound_ReturnsNotFound()
    {
        var analysisHandler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            new FakeRepositoryAnalyzer(),
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var archHandler = new GetRepositoryWorkspaceArchitectureQueryHandler(
            analysisHandler,
            NullLogger<GetRepositoryWorkspaceArchitectureQueryHandler>.Instance);

        var result = await archHandler.HandleAsync(
            new GetRepositoryWorkspaceArchitectureQuery(Guid.NewGuid()),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.NotFound.Should().BeTrue();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task HandleAsync_WorkspaceNotReady_ReturnsConflict()
    {
        var id = Guid.NewGuid();
        _workspaceQuery.Workspaces[id] = new RepositoryWorkspace
        {
            Id = id,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            Status = RepositoryWorkspaceStatus.Cloning,
            LocalPath = _tempDirectory,
        };

        var analysisHandler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            new FakeRepositoryAnalyzer(),
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var archHandler = new GetRepositoryWorkspaceArchitectureQueryHandler(
            analysisHandler,
            NullLogger<GetRepositoryWorkspaceArchitectureQueryHandler>.Instance);

        var result = await archHandler.HandleAsync(
            new GetRepositoryWorkspaceArchitectureQuery(id),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("not ready");
    }

    [Fact]
    public async Task HandleAsync_LocalPathDoesNotExist_ReturnsConflict()
    {
        var id = Guid.NewGuid();
        _workspaceQuery.Workspaces[id] = new RepositoryWorkspace
        {
            Id = id,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = Path.Combine(_tempDirectory, "non_existent_folder"),
        };

        var analysisHandler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            new FakeRepositoryAnalyzer(),
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var archHandler = new GetRepositoryWorkspaceArchitectureQueryHandler(
            analysisHandler,
            NullLogger<GetRepositoryWorkspaceArchitectureQueryHandler>.Instance);

        var result = await archHandler.HandleAsync(
            new GetRepositoryWorkspaceArchitectureQuery(id),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("does not exist");
    }

    [Fact]
    public async Task HandleAsync_MultiProjectGraph_ProducesCorrectEdgeDirectionsAndRelativePaths()
    {
        var id = Guid.NewGuid();
        _workspaceQuery.Workspaces[id] = new RepositoryWorkspace
        {
            Id = id,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            CommitSha = "abc1234",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = _tempDirectory,
        };

        // Create sample project directories and files
        var apiDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "src", "DevPilot.Api", "Controllers"));
        var appDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "src", "DevPilot.Application"));
        var domainDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "src", "DevPilot.Domain", "Entities"));
        var infraDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "src", "DevPilot.Infrastructure"));

        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "src", "DevPilot.Api", "Program.cs"), "// Program");
        await File.WriteAllTextAsync(Path.Combine(apiDir.FullName, "OrdersController.cs"), "public class OrdersController {}");
        await File.WriteAllTextAsync(Path.Combine(appDir.FullName, "DependencyInjection.cs"), "public static class DependencyInjection {}");
        await File.WriteAllTextAsync(Path.Combine(domainDir.FullName, "Order.cs"), "public class Order {}");
        await File.WriteAllTextAsync(Path.Combine(infraDir.FullName, "DevPilotDbContext.cs"), "public class DevPilotDbContext {}");

        var apiCsproj = Path.Combine(_tempDirectory, "src", "DevPilot.Api", "DevPilot.Api.csproj");
        var appCsproj = Path.Combine(_tempDirectory, "src", "DevPilot.Application", "DevPilot.Application.csproj");
        var domainCsproj = Path.Combine(_tempDirectory, "src", "DevPilot.Domain", "DevPilot.Domain.csproj");
        var infraCsproj = Path.Combine(_tempDirectory, "src", "DevPilot.Infrastructure", "DevPilot.Infrastructure.csproj");

        await File.WriteAllTextAsync(apiCsproj, "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        await File.WriteAllTextAsync(appCsproj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        await File.WriteAllTextAsync(domainCsproj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        await File.WriteAllTextAsync(infraCsproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Npgsql.EntityFrameworkCore.PostgreSQL\" Version=\"8.0.0\" /></ItemGroup></Project>");

        var analyzer = new FakeRepositoryAnalyzer
        {
            ResultToReturn = new RepositoryAnalysisResult
            {
                Success = true,
                Solutions = new List<SolutionAnalysisResult>
                {
                    new()
                    {
                        Name = "DevPilot",
                        Path = Path.Combine(_tempDirectory, "DevPilot.sln"),
                        Projects = new List<ProjectAnalysisResult>
                        {
                            new()
                            {
                                Name = "DevPilot.Api",
                                Path = apiCsproj,
                                ProjectType = "Web",
                                CompilationSucceeded = true,
                                ProjectReferences = new List<ProjectReferenceInfo>
                                {
                                    new() { Name = "DevPilot.Application", Path = appCsproj },
                                },
                            },
                            new()
                            {
                                Name = "DevPilot.Application",
                                Path = appCsproj,
                                ProjectType = "Library",
                                CompilationSucceeded = true,
                                ProjectReferences = new List<ProjectReferenceInfo>
                                {
                                    new() { Name = "DevPilot.Domain", Path = domainCsproj },
                                },
                            },
                            new()
                            {
                                Name = "DevPilot.Domain",
                                Path = domainCsproj,
                                ProjectType = "Library",
                                CompilationSucceeded = true,
                                ProjectReferences = new List<ProjectReferenceInfo>(),
                            },
                            new()
                            {
                                Name = "DevPilot.Infrastructure",
                                Path = infraCsproj,
                                ProjectType = "Library",
                                CompilationSucceeded = true,
                                ProjectReferences = new List<ProjectReferenceInfo>
                                {
                                    new() { Name = "DevPilot.Application", Path = appCsproj },
                                    new() { Name = "DevPilot.Domain", Path = domainCsproj },
                                },
                            },
                        },
                    },
                },
            },
        };

        var analysisHandler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            analyzer,
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var archHandler = new GetRepositoryWorkspaceArchitectureQueryHandler(
            analysisHandler,
            NullLogger<GetRepositoryWorkspaceArchitectureQueryHandler>.Instance);

        var result = await archHandler.HandleAsync(
            new GetRepositoryWorkspaceArchitectureQuery(id),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Architecture.Should().NotBeNull();

        var arch = result.Architecture!;
        arch.Repository.FullName.Should().Be("enesscigdem/DevPilot");

        // Nodes
        var apiNode = arch.Nodes.FirstOrDefault(n => n.Id == "DevPilot.Api");
        var appNode = arch.Nodes.FirstOrDefault(n => n.Id == "DevPilot.Application");
        var domainNode = arch.Nodes.FirstOrDefault(n => n.Id == "DevPilot.Domain");
        var infraNode = arch.Nodes.FirstOrDefault(n => n.Id == "DevPilot.Infrastructure");
        var postgresNode = arch.Nodes.FirstOrDefault(n => n.Id == "postgres");

        apiNode.Should().NotBeNull();
        appNode.Should().NotBeNull();
        domainNode.Should().NotBeNull();
        infraNode.Should().NotBeNull();
        postgresNode.Should().NotBeNull(); // Real detected PostgreSQL from Npgsql package

        // Edge directions:
        // Api references Application -> Api depends on Application -> Edge From Api To Application
        arch.Edges.Should().Contain(e => e.From == "DevPilot.Api" && e.To == "DevPilot.Application" && e.Type == "ProjectReference");
        arch.Edges.Should().Contain(e => e.From == "DevPilot.Application" && e.To == "DevPilot.Domain" && e.Type == "ProjectReference");
        arch.Edges.Should().Contain(e => e.From == "DevPilot.Infrastructure" && e.To == "DevPilot.Application" && e.Type == "ProjectReference");
        arch.Edges.Should().Contain(e => e.From == "DevPilot.Infrastructure" && e.To == "DevPilot.Domain" && e.Type == "ProjectReference");
        arch.Edges.Should().Contain(e => e.From == "DevPilot.Infrastructure" && e.To == "postgres" && e.Type == "DatabaseConnection");
        arch.Edges.Should().NotContain(e => e.From == "DevPilot.Api" && e.To == "postgres");
        arch.Edges.Should().NotContain(e => e.From == "DevPilot.Application" && e.To == "postgres");

        // Incoming / Outgoing mappings:
        // Api depends on Application:
        apiNode!.Outgoing.Should().Contain("DevPilot.Application");
        apiNode.Incoming.Should().BeEmpty();

        // Application is depended on by Api and Infrastructure, and depends on Domain:
        appNode!.Incoming.Should().Contain(new[] { "DevPilot.Api", "DevPilot.Infrastructure" });
        appNode.Outgoing.Should().Contain("DevPilot.Domain");

        // Domain is depended on by Application and Infrastructure, has no outgoing project references:
        domainNode!.Incoming.Should().Contain(new[] { "DevPilot.Application", "DevPilot.Infrastructure" });
        domainNode.Outgoing.Should().BeEmpty();

        // No absolute path leakage
        foreach (var node in arch.Nodes)
        {
            if (!string.IsNullOrEmpty(node.Path))
            {
                node.Path.Should().NotContain(_tempDirectory);
                node.Path.Should().NotContain("\\");
            }

            foreach (var keyFile in node.KeyFiles)
            {
                keyFile.Should().NotContain(_tempDirectory);
                keyFile.Should().NotContain("\\");
            }
        }

        // Key files deterministic check
        apiNode.KeyFiles.Should().Contain("src/DevPilot.Api/Program.cs");
        apiNode.KeyFiles.Should().Contain("src/DevPilot.Api/Controllers/OrdersController.cs");
        infraNode!.KeyFiles.Should().Contain("src/DevPilot.Infrastructure/DevPilotDbContext.cs");
    }

    [Fact]
    public async Task HandleAsync_NoExternalDependenciesWhenNotDetected_OmitsDatabaseNode()
    {
        var id = Guid.NewGuid();
        _workspaceQuery.Workspaces[id] = new RepositoryWorkspace
        {
            Id = id,
            Owner = "enesscigdem",
            Repository = "StandaloneLib",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = _tempDirectory,
        };

        var csproj = Path.Combine(_tempDirectory, "MyLib.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "Class1.cs"), "public class Class1 {}");

        var analyzer = new FakeRepositoryAnalyzer
        {
            ResultToReturn = new RepositoryAnalysisResult
            {
                Success = true,
                StandaloneProjects = new List<ProjectAnalysisResult>
                {
                    new()
                    {
                        Name = "MyLib",
                        Path = csproj,
                        ProjectType = "Library",
                        CompilationSucceeded = true,
                    },
                },
            },
        };

        var analysisHandler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            analyzer,
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var archHandler = new GetRepositoryWorkspaceArchitectureQueryHandler(
            analysisHandler,
            NullLogger<GetRepositoryWorkspaceArchitectureQueryHandler>.Instance);

        var result = await archHandler.HandleAsync(
            new GetRepositoryWorkspaceArchitectureQuery(id),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Architecture.Should().NotBeNull();
        result.Architecture!.Nodes.Should().HaveCount(1);
        result.Architecture.Nodes[0].Id.Should().Be("MyLib");
        result.Architecture.Nodes.Should().NotContain(n => n.Id == "postgres" || n.Id == "redis" || n.Id == "sqlserver");
        result.Architecture.Edges.Should().BeEmpty();
    }

    private sealed class FakeRepositoryWorkspaceQuery : IRepositoryWorkspaceQuery
    {
        public Dictionary<Guid, RepositoryWorkspace> Workspaces { get; } = new();

        public Task<RepositoryWorkspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Workspaces.TryGetValue(id, out var ws);
            return Task.FromResult(ws);
        }

        public Task<RepositoryWorkspace?> GetByOwnerAndRepositoryAndBranchAsync(
            string owner, string repository, string branch, CancellationToken cancellationToken = default)
        {
            var ws = Workspaces.Values.FirstOrDefault(w =>
                w.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
                w.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
                w.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(ws);
        }

        public Task<List<RepositoryWorkspace>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Workspaces.Values.ToList());
        }
    }

    private sealed class FakeRepositoryAnalyzer : IRepositoryAnalyzer
    {
        public RepositoryAnalysisResult ResultToReturn { get; set; } = new() { Success = true };

        public Task<RepositoryAnalysisResult> AnalyzeAsync(
            RepositoryAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResultToReturn);
        }
    }
}
