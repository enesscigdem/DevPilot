using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests;

public sealed class WorkspaceAnalogDependencyDiscoveryTests : IDisposable
{
    private readonly string _workspacePath;

    public WorkspaceAnalogDependencyDiscoveryTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), "DevPilotWsAnalog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspacePath))
            {
                Directory.Delete(_workspacePath, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void BuildPrerequisiteMap_DiscoversServiceRepositoryEdge_FromRealWorkspaceFiles()
    {
        SeedTypeScriptFeatureLayer(includeDecoys: false);

        var planned = new[]
        {
            new ManifestFileEntry("src/services/projectService.ts", FileEditAction.Create),
            new ManifestFileEntry("src/repositories/projectRepository.ts", FileEditAction.Create),
            new ManifestFileEntry("src/routes/projectRoutes.ts", FileEditAction.Create),
            new ManifestFileEntry("src/controllers/projectController.ts", FileEditAction.Create)
        };

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(
            planned,
            existingFileContents: new Dictionary<string, string>(),
            workspacePath: _workspacePath);

        map["src/services/projectService.ts"].Should().Contain("src/repositories/projectRepository.ts");
        map["src/routes/projectRoutes.ts"].Should().Contain("src/controllers/projectController.ts");
        map["src/repositories/projectRepository.ts"].Should().NotContain("src/services/projectService.ts");
        map["src/controllers/projectController.ts"].Should().NotContain("src/routes/projectRoutes.ts");
    }

    [Fact]
    public void BuildPrerequisiteMap_DiscoversAnalogEdges_WhenSiblingDirectoriesHaveManyFiles()
    {
        SeedTypeScriptFeatureLayer(includeDecoys: true);

        var planned = new[]
        {
            new ManifestFileEntry("src/services/projectService.ts", FileEditAction.Create),
            new ManifestFileEntry("src/repositories/projectRepository.ts", FileEditAction.Create),
            new ManifestFileEntry("src/routes/projectRoutes.ts", FileEditAction.Create),
            new ManifestFileEntry("src/controllers/projectController.ts", FileEditAction.Create)
        };

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(
            planned,
            existingFileContents: new Dictionary<string, string>(),
            workspacePath: _workspacePath);

        map["src/services/projectService.ts"].Should().Contain("src/repositories/projectRepository.ts");
        map["src/routes/projectRoutes.ts"].Should().Contain("src/controllers/projectController.ts");
    }

    [Fact]
    public void BuildPrerequisiteMap_RemovesContradictoryLayerEdge_WhenRepositoryEvidenceProvesOpposite()
    {
        SeedTypeScriptFeatureLayer(includeDecoys: false);

        var planned = new[]
        {
            new ManifestFileEntry("src/services/projectService.ts", FileEditAction.Create),
            new ManifestFileEntry("src/repositories/projectRepository.ts", FileEditAction.Create)
        };

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(
            planned,
            existingFileContents: new Dictionary<string, string>(),
            workspacePath: _workspacePath);

        map["src/repositories/projectRepository.ts"].Should().NotContain("src/services/projectService.ts");
        map["src/services/projectService.ts"].Should().Contain("src/repositories/projectRepository.ts");
    }

    [Fact]
    public void BuildPrerequisiteMap_DiscoversPythonAnalogEdge_FromRealWorkspaceFiles()
    {
        Write("src/services/feature_a_service.py", """
            from repositories.feature_a_repository import FeatureARepository
            class FeatureAService:
                def __init__(self, repo: FeatureARepository):
                    self._repo = repo
            """);
        Write("src/repositories/feature_a_repository.py", """
            class FeatureARepository:
                pass
            """);

        var planned = new[]
        {
            new ManifestFileEntry("src/services/feature_b_service.py", FileEditAction.Create),
            new ManifestFileEntry("src/repositories/feature_b_repository.py", FileEditAction.Create)
        };

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(
            planned,
            existingFileContents: new Dictionary<string, string>(),
            workspacePath: _workspacePath);

        map["src/services/feature_b_service.py"].Should().Contain("src/repositories/feature_b_repository.py");
        map["src/repositories/feature_b_repository.py"].Should().NotContain("src/services/feature_b_service.py");
    }

    [Fact]
    public void BuildPrerequisiteMap_SuppressesContradictoryLayerEdges_WhenAnalogEvidenceExists()
    {
        SeedTypeScriptFeatureLayer(includeDecoys: false);
        var planned = new[]
        {
            new ManifestFileEntry("src/services/projectService.ts", FileEditAction.Create),
            new ManifestFileEntry("src/repositories/projectRepository.ts", FileEditAction.Create),
            new ManifestFileEntry("src/routes/projectRoutes.ts", FileEditAction.Create),
            new ManifestFileEntry("src/controllers/projectController.ts", FileEditAction.Create)
        };

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(
            planned,
            existingFileContents: new Dictionary<string, string>(),
            workspacePath: _workspacePath);

        map["src/repositories/projectRepository.ts"].Should().NotContain("src/routes/projectRoutes.ts");
        map["src/controllers/projectController.ts"].Should().NotContain("src/routes/projectRoutes.ts");
        map["src/services/projectService.ts"].Should().Contain("src/repositories/projectRepository.ts");
        map["src/routes/projectRoutes.ts"].Should().Contain("src/controllers/projectController.ts");
    }

    [Fact]
    public void BuildPrerequisiteMap_AmbiguousBidirectionalEvidence_ProducesNoInferredEdge()
    {
        Write("src/services/featureAService.ts", "import { a } from '../repositories/featureARepository';");
        Write("src/repositories/featureARepository.ts", "import { b } from '../services/featureAService';");
        Write("src/routes/featureARoutes.ts", "import { c } from '../controllers/featureAController';");
        Write("src/controllers/featureAController.ts", "import { d } from '../routes/featureARoutes';");

        var planned = new[]
        {
            new ManifestFileEntry("src/services/featureBService.ts", FileEditAction.Create),
            new ManifestFileEntry("src/repositories/featureBRepository.ts", FileEditAction.Create),
            new ManifestFileEntry("src/routes/featureBRoutes.ts", FileEditAction.Create),
            new ManifestFileEntry("src/controllers/featureBController.ts", FileEditAction.Create)
        };

        var map = GenerationDependencyAnalyzer.BuildPrerequisiteMap(
            planned,
            existingFileContents: new Dictionary<string, string>(),
            workspacePath: _workspacePath);

        map["src/services/featureBService.ts"].Should().NotContain("src/repositories/featureBRepository.ts");
        map["src/routes/featureBRoutes.ts"].Should().NotContain("src/controllers/featureBController.ts");
        HasPrerequisiteCycle(map).Should().BeFalse();
    }

    [Fact]
    public void CollectNeighborFileContents_IncludesEvidenceFiles_BeyondAlphabeticalCutoff()
    {
        SeedTypeScriptFeatureLayer(includeDecoys: true);

        var contents = GenerationDependencyAnalyzer.CollectNeighborFileContents(
            _workspacePath,
            new[]
            {
                "src/services/projectService.ts",
                "src/repositories/projectRepository.ts",
                "src/routes/projectRoutes.ts",
                "src/controllers/projectController.ts"
            });

        contents.Should().ContainKey("src/services/issueService.ts");
        contents.Should().ContainKey("src/routes/issueRoutes.ts");
        contents.Keys.Count(key => key.StartsWith("src/services/", StringComparison.OrdinalIgnoreCase))
            .Should().BeLessThanOrEqualTo(16);
        contents.Keys.Count(key => key.StartsWith("src/repositories/", StringComparison.OrdinalIgnoreCase))
            .Should().BeLessThanOrEqualTo(16);
    }

    private void SeedTypeScriptFeatureLayer(bool includeDecoys)
    {
        if (includeDecoys)
        {
            for (var i = 0; i < 20; i++)
            {
                var suffix = i.ToString("D2");
                Write($"src/services/decoyService{suffix}.ts", $"export const decoyService{suffix} = {i};");
                Write($"src/repositories/decoyRepository{suffix}.ts", $"export const decoyRepository{suffix} = {i};");
                Write($"src/routes/decoyRoutes{suffix}.ts", $"export const decoyRoutes{suffix} = {i};");
                Write($"src/controllers/decoyController{suffix}.ts", $"export const decoyController{suffix} = {i};");
            }
        }

        Write("src/services/issueService.ts", """
            import { IssueRepository } from '../repositories/issueRepository';
            export class IssueService {
              constructor(private readonly repo: IssueRepository) {}
            }
            """);
        Write("src/repositories/issueRepository.ts", """
            export class IssueRepository {}
            """);
        Write("src/routes/issueRoutes.ts", """
            import { IssueController } from '../controllers/issueController';
            export function registerIssueRoutes(app: any, controller: IssueController) {}
            """);
        Write("src/controllers/issueController.ts", """
            export class IssueController {}
            """);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_workspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static bool HasPrerequisiteCycle(IReadOnlyDictionary<string, IReadOnlyList<string>> map)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool Visit(string node)
        {
            if (state.TryGetValue(node, out var current))
            {
                return current == 1;
            }

            state[node] = 1;
            if (map.TryGetValue(node, out var next))
            {
                foreach (var child in next)
                {
                    if (Visit(child))
                    {
                        return true;
                    }
                }
            }

            state[node] = 2;
            return false;
        }

        return map.Keys.Any(Visit);
    }
}
