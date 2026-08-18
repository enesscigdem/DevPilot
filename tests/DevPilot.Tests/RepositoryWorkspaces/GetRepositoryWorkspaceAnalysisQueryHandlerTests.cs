using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetRepositoryWorkspaceAnalysis;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.CodeAnalysis;
using DevPilot.Infrastructure.RepositoryInspection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.RepositoryWorkspaces;

public class GetRepositoryWorkspaceAnalysisQueryHandlerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly FakeRepositoryWorkspaceQuery _workspaceQuery = new();
    private readonly RepositoryStructureScanner _structureScanner = new(NullLogger<RepositoryStructureScanner>.Instance);

    public GetRepositoryWorkspaceAnalysisQueryHandlerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevPilotAnalysisTest_" + Guid.NewGuid().ToString("N"));
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
        var handler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            new FakeRepositoryAnalyzer(),
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var result = await handler.HandleAsync(
            new GetRepositoryWorkspaceAnalysisQuery(Guid.NewGuid()),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.NotFound.Should().BeTrue();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Theory]
    [InlineData(RepositoryWorkspaceStatus.Cloning)]
    [InlineData(RepositoryWorkspaceStatus.Failed)]
    [InlineData(RepositoryWorkspaceStatus.AlreadyExists)]
    public async Task HandleAsync_WorkspaceNotCompleted_ReturnsConflict(RepositoryWorkspaceStatus status)
    {
        var id = Guid.NewGuid();
        _workspaceQuery.WorkspaceToReturn = new RepositoryWorkspace
        {
            Id = id,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            Status = status,
            LocalPath = _tempDirectory,
        };

        var handler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            new FakeRepositoryAnalyzer(),
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var result = await handler.HandleAsync(
            new GetRepositoryWorkspaceAnalysisQuery(id),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("not ready");
    }

    [Fact]
    public async Task HandleAsync_LocalPathDoesNotExist_ReturnsConflict()
    {
        var id = Guid.NewGuid();
        _workspaceQuery.WorkspaceToReturn = new RepositoryWorkspace
        {
            Id = id,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = Path.Combine(_tempDirectory, "does_not_exist_xyz"),
        };

        var handler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            new FakeRepositoryAnalyzer(),
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var result = await handler.HandleAsync(
            new GetRepositoryWorkspaceAnalysisQuery(id),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.ErrorMessage.Should().Contain("does not exist");
    }

    [Fact]
    public async Task HandleAsync_ValidCompletedWorkspace_ReturnsCompleteAnalysisWithoutExposingLocalPath()
    {
        // 1. Setup mock workspace folder structure
        var srcDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "src", "DevPilot.Api", "Controllers"));
        var appDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "src", "DevPilot.Application"));
        var binDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "src", "DevPilot.Api", "bin"));
        var gitDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, ".git"));
        var nodeModulesDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "node_modules"));

        // Add dummy files
        await File.WriteAllTextAsync(Path.Combine(gitDir.FullName, "HEAD"), "ref: refs/heads/master");
        await File.WriteAllTextAsync(Path.Combine(binDir.FullName, "temp.dll"), "binary");
        await File.WriteAllTextAsync(Path.Combine(nodeModulesDir.FullName, "pkg.js"), "console.log()");
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "OrdersController.cs"), "public class OrdersController {}");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "README.md"), "# DevPilot");

        // .csproj with packages
        var csprojContent = @"<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Microsoft.EntityFrameworkCore"" Version=""8.0.4"" />
    <PackageReference Include=""Npgsql.EntityFrameworkCore.PostgreSQL"" Version=""8.0.2"" />
    <PackageReference Include=""MediatR"" Version=""12.2.0"" />
    <PackageReference Include=""xunit"" Version=""2.7.0"" />
  </ItemGroup>
</Project>";
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "src", "DevPilot.Api", "DevPilot.Api.csproj"), csprojContent);

        // package.json with frontend deps
        var packageJsonContent = @"{
  ""name"": ""devpilot-web"",
  ""dependencies"": {
    ""react"": ""^18.3.1"",
    ""typescript"": ""~5.4.2""
  }
}";
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "package.json"), packageJsonContent);

        // 2. Setup Roslyn Analyzer fake result
        var analyzer = new FakeRepositoryAnalyzer
        {
            ResultToReturn = new RepositoryAnalysisResult
            {
                Success = true,
                Warnings = new List<string>
                {
                    $"Warning in {Path.Combine(_tempDirectory, "src", "DevPilot.Api", "Controllers", "OrdersController.cs")}: unused using",
                },
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
                                Path = Path.Combine(_tempDirectory, "src", "DevPilot.Api", "DevPilot.Api.csproj"),
                                ProjectType = "Web",
                                TargetFramework = "net8.0",
                                CompilationSucceeded = true,
                                ProjectReferences = new List<ProjectReferenceInfo>
                                {
                                    new()
                                    {
                                        Name = "DevPilot.Application",
                                        Path = Path.Combine(_tempDirectory, "src", "DevPilot.Application", "DevPilot.Application.csproj"),
                                    }
                                },
                                Controllers = new List<ControllerAnalysisResult>
                                {
                                    new()
                                    {
                                        Name = "OrdersController",
                                        Namespace = "DevPilot.Api.Controllers",
                                        SourcePath = Path.Combine(_tempDirectory, "src", "DevPilot.Api", "Controllers", "OrdersController.cs"),
                                        Methods = new List<MethodAnalysisResult> { new() { Name = "GetOrders" } },
                                        Actions = new List<ControllerActionAnalysisResult>
                                        {
                                            new()
                                            {
                                                Name = "GetOrders",
                                                HttpMethod = "GET",
                                                RouteTemplate = "api/orders",
                                                IsAuthorized = true,
                                                SourcePath = Path.Combine(_tempDirectory, "src", "DevPilot.Api", "Controllers", "OrdersController.cs"),
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var id = Guid.NewGuid();
        _workspaceQuery.WorkspaceToReturn = new RepositoryWorkspace
        {
            Id = id,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            CommitSha = "abc1234",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = _tempDirectory,
        };

        var handler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            analyzer,
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var result = await handler.HandleAsync(
            new GetRepositoryWorkspaceAnalysisQuery(id),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Analysis.Should().NotBeNull();

        var dto = result.Analysis!;
        dto.Repository.Owner.Should().Be("enesscigdem");
        dto.Repository.Repository.Should().Be("DevPilot");
        dto.Repository.FullName.Should().Be("enesscigdem/DevPilot");
        dto.Repository.Branch.Should().Be("master");
        dto.Repository.CommitSha.Should().Be("abc1234");

        dto.Summary.Status.Should().Be("Partial");
        dto.Summary.SymbolsCount.Should().Be(1);
        dto.Summary.TypesCount.Should().Be(1);
        dto.Summary.ReferencesCount.Should().Be(1);
        dto.Summary.Steps.Should().HaveCount(5);
        dto.Summary.Steps.Should().Contain(s => s.Label == "Load solution & projects" && s.Done);
        dto.Summary.Steps.Should().Contain(s => s.Label == "Compile & parse source" && s.Done);
        dto.Summary.Steps.Should().Contain(s => s.Label == "Extract types & symbols" && s.Done);
        dto.Summary.Steps.Should().Contain(s => s.Label == "Resolve project references" && s.Done);
        dto.Summary.Steps.Should().Contain(s => s.Label == "Scan structure & technologies" && s.Done);

        // Verify Repository Tree Exclusions
        dto.FileTree.Should().NotBeEmpty();
        var allTreePaths = FlattenTree(dto.FileTree);
        allTreePaths.Should().NotContain(p => p.StartsWith(".git") || p.StartsWith("node_modules") || p.Contains("/bin"));
        allTreePaths.Should().Contain("README.md");
        allTreePaths.Should().Contain("src/DevPilot.Api/Controllers/OrdersController.cs");

        // Verify Privacy: Absolute path should NOT be present anywhere in DTO
        var serialized = System.Text.Json.JsonSerializer.Serialize(dto);
        serialized.Should().NotContain(_tempDirectory.Replace('\\', '/'));
        serialized.Should().NotContain(_tempDirectory);

        // Verify Projects
        dto.Projects.Should().HaveCount(1);
        var proj = dto.Projects[0];
        proj.Name.Should().Be("DevPilot.Api");
        proj.Layer.Should().Be("Web");
        proj.Path.Should().Be("src/DevPilot.Api/DevPilot.Api.csproj");
        proj.ProjectReferences[0].Path.Should().Be("src/DevPilot.Application/DevPilot.Application.csproj");

        // Verify Endpoints
        dto.Endpoints.Should().HaveCount(1);
        var ep = dto.Endpoints[0];
        ep.Method.Should().Be("GET");
        ep.Route.Should().Be("/api/orders");
        ep.Controller.Should().Be("OrdersController");
        ep.Action.Should().Be("GetOrders");
        ep.Auth.Should().BeTrue();
        ep.SourcePath.Should().Be("src/DevPilot.Api/Controllers/OrdersController.cs");

        // Verify Technologies
        dto.Technologies.Should().Contain(t => t.Name == ".NET" && t.Version == "8.0");
        dto.Technologies.Should().Contain(t => t.Name == "ASP.NET Core");
        dto.Technologies.Should().Contain(t => t.Name == "Entity Framework Core" && t.Version == "8.0.4");
        dto.Technologies.Should().Contain(t => t.Name == "PostgreSQL" && t.Version == "8.0.2");
        dto.Technologies.Should().Contain(t => t.Name == "React" && t.Version == "18.3.1");
        dto.Technologies.Should().Contain(t => t.Name == "TypeScript" && t.Version == "5.4.2");

        // Verify Warnings sanitized
        dto.Warnings.Should().HaveCount(1);
        dto.Warnings[0].Should().NotContain(_tempDirectory);
        dto.Warnings[0].Should().Contain("src/DevPilot.Api/Controllers/OrdersController.cs");
    }

    [Fact]
    public async Task HandleAsync_AnalyzerThrows_ReturnsSafeErrorResponse()
    {
        var id = Guid.NewGuid();
        _workspaceQuery.WorkspaceToReturn = new RepositoryWorkspace
        {
            Id = id,
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "master",
            Status = RepositoryWorkspaceStatus.Completed,
            LocalPath = _tempDirectory,
        };

        var handler = new GetRepositoryWorkspaceAnalysisQueryHandler(
            _workspaceQuery,
            new ThrowingRepositoryAnalyzer(),
            _structureScanner,
            NullLogger<GetRepositoryWorkspaceAnalysisQueryHandler>.Instance);

        var result = await handler.HandleAsync(
            new GetRepositoryWorkspaceAnalysisQuery(id),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Code analysis failed");
    }

    private static List<string> FlattenTree(List<WorkspaceFileNodeDto> nodes)
    {
        var paths = new List<string>();
        foreach (var node in nodes)
        {
            paths.Add(node.Path);
            if (node.Children is not null)
            {
                paths.AddRange(FlattenTree(node.Children));
            }
        }
        return paths;
    }

    private sealed class FakeRepositoryWorkspaceQuery : IRepositoryWorkspaceQuery
    {
        public RepositoryWorkspace? WorkspaceToReturn { get; set; }

        public Task<RepositoryWorkspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(WorkspaceToReturn);
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

    private sealed class ThrowingRepositoryAnalyzer : IRepositoryAnalyzer
    {
        public Task<RepositoryAnalysisResult> AnalyzeAsync(
            RepositoryAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("MSBuild crashed unexpectedly.");
        }
    }
}
