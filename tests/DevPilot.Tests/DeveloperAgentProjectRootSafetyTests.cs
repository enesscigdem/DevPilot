using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests;

public class DeveloperAgentProjectRootSafetyTests
{
    [Fact]
    public void BuildManifestFromImpactAnalysis_UnambiguousSingleTestProject_SafelyRemapsDevPilotApiTests()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "DevPilot.Api",
                ProjectPath = "src/DevPilot.Api/DevPilot.Api.csproj",
                ProjectDirectory = "src/DevPilot.Api",
                IsTestProject = false
            },
            new()
            {
                ProjectName = "DevPilot.Tests",
                ProjectPath = "tests/DevPilot.Tests/DevPilot.Tests.csproj",
                ProjectDirectory = "tests/DevPilot.Tests",
                IsTestProject = true
            }
        };

        var projectRoots = new[] { "src/DevPilot.Api", "tests/DevPilot.Tests" };

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add summary endpoint",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[]
            {
                "src/DevPilot.Api/Controllers/RepositoryWorkspaceController.cs",
                "tests/DevPilot.Api.Tests/RepositoryWorkspaceTaskSummaryTests.cs"
            },
            WorkspacePath: "c:/repo",
            BranchName: "devpilot/task-1",
            ImpactedFiles: new[]
            {
                new ImpactedFileDetail("src/DevPilot.Api/Controllers/RepositoryWorkspaceController.cs", "Modify", "Add endpoint"),
                new ImpactedFileDetail("tests/DevPilot.Api.Tests/RepositoryWorkspaceTaskSummaryTests.cs", "Create", "Add test")
            });

        var manifest = DeveloperAgent.BuildManifestFromImpactAnalysis(request, "c:/repo", projectRoots, 10, projectGraph);

        manifest.Should().NotBeNull();
        manifest.Files.Should().HaveCount(2);
        manifest.Files[0].FilePath.Should().Be("src/DevPilot.Api/Controllers/RepositoryWorkspaceController.cs");
        manifest.Files[1].FilePath.Should().Be("tests/DevPilot.Tests/RepositoryWorkspaceTaskSummaryTests.cs");
        manifest.Files[1].Action.Should().Be(FileEditAction.Create);
    }

    [Fact]
    public void BuildManifestFromImpactAnalysis_MultipleCandidateTestProjects_ThrowsControlledAmbiguityErrorWithoutGuessing()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "DevPilot.Api",
                ProjectPath = "src/DevPilot.Api/DevPilot.Api.csproj",
                ProjectDirectory = "src/DevPilot.Api",
                IsTestProject = false
            },
            new()
            {
                ProjectName = "DevPilot.UnitTests",
                ProjectPath = "tests/DevPilot.UnitTests/DevPilot.UnitTests.csproj",
                ProjectDirectory = "tests/DevPilot.UnitTests",
                IsTestProject = true
            },
            new()
            {
                ProjectName = "DevPilot.IntegrationTests",
                ProjectPath = "tests/DevPilot.IntegrationTests/DevPilot.IntegrationTests.csproj",
                ProjectDirectory = "tests/DevPilot.IntegrationTests",
                IsTestProject = true
            }
        };

        var projectRoots = new[] { "src/DevPilot.Api", "tests/DevPilot.UnitTests", "tests/DevPilot.IntegrationTests" };

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add summary endpoint",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[]
            {
                "tests/DevPilot.Api.Tests/RepositoryWorkspaceTaskSummaryTests.cs"
            },
            WorkspacePath: "c:/repo",
            BranchName: "devpilot/task-1");

        var act = () => DeveloperAgent.BuildManifestFromImpactAnalysis(request, "c:/repo", projectRoots, 10, projectGraph);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*multiple candidate test projects exist*");
    }

    [Fact]
    public void BuildManifestFromImpactAnalysis_NoTestProjects_ThrowsControlledFailure()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "DevPilot.Api",
                ProjectPath = "src/DevPilot.Api/DevPilot.Api.csproj",
                ProjectDirectory = "src/DevPilot.Api",
                IsTestProject = false
            }
        };

        var projectRoots = new[] { "src/DevPilot.Api" };

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add test",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[]
            {
                "tests/DevPilot.Api.Tests/RepositoryWorkspaceTaskSummaryTests.cs"
            },
            WorkspacePath: "c:/repo",
            BranchName: "devpilot/task-1");

        var act = () => DeveloperAgent.BuildManifestFromImpactAnalysis(request, "c:/repo", projectRoots, 10, projectGraph);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no test project was discovered*");
    }

    [Fact]
    public void BuildManifestFromImpactAnalysis_NonTestInvalidPath_ThrowsOutsideProjectRootsError()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "DevPilot.Api",
                ProjectPath = "src/DevPilot.Api/DevPilot.Api.csproj",
                ProjectDirectory = "src/DevPilot.Api",
                IsTestProject = false
            },
            new()
            {
                ProjectName = "DevPilot.Tests",
                ProjectPath = "tests/DevPilot.Tests/DevPilot.Tests.csproj",
                ProjectDirectory = "tests/DevPilot.Tests",
                IsTestProject = true
            }
        };

        var projectRoots = new[] { "src/DevPilot.Api", "tests/DevPilot.Tests" };

        var request = new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add random file",
            TaskDescription: "Desc",
            AcceptanceCriteria: null,
            ImpactAnalysisSummary: "Summary",
            ProposedPlan: "Plan",
            ImpactedFilePaths: new[]
            {
                "src/ImaginaryService/RandomFile.cs"
            },
            WorkspacePath: "c:/repo",
            BranchName: "devpilot/task-1");

        var act = () => DeveloperAgent.BuildManifestFromImpactAnalysis(request, "c:/repo", projectRoots, 10, projectGraph);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*is outside all discovered .NET project roots*");
    }

    [Fact]
    public void ProjectGraphHelper_TryRemapTestFileToSingleTestProject_CorrectlyStripsImaginaryPrefix()
    {
        var projectGraph = new List<DiscoveredProjectNode>
        {
            new()
            {
                ProjectName = "DevPilot.Tests",
                ProjectPath = "tests/DevPilot.Tests/DevPilot.Tests.csproj",
                ProjectDirectory = "tests/DevPilot.Tests",
                IsTestProject = true
            }
        };

        var success = ProjectGraphHelper.TryRemapTestFileToSingleTestProject(
            "tests/DevPilot.Api.Tests/Controllers/TaskSummaryTests.cs",
            projectGraph,
            out var remapped,
            out var err);

        success.Should().BeTrue();
        err.Should().BeNull();
        remapped.Should().Be("tests/DevPilot.Tests/Controllers/TaskSummaryTests.cs");
    }
}
