using System.Collections.Concurrent;
using System.Diagnostics;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Commands.ApproveExecutionReview;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class TargetCsProjPathValidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeDir;

    public TargetCsProjPathValidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotCsProjValidationTests_" + Guid.NewGuid().ToString("N"));
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        Directory.CreateDirectory(_worktreeDir);
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
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void DiscoverProjectRoots_MultiProjectRepository_ReturnsAllRelativeProjectDirectories()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_worktreeDir, "src", "App.Api"));
        Directory.CreateDirectory(Path.Combine(_worktreeDir, "src", "App.Services"));
        File.WriteAllText(Path.Combine(_worktreeDir, "src", "App.Api", "App.Api.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_worktreeDir, "src", "App.Services", "App.Services.csproj"), "<Project />");

        // Act
        var roots = WorktreeEditApplier.DiscoverProjectRoots(_worktreeDir);

        // Assert
        roots.Should().HaveCount(2);
        roots.Should().Contain("src/App.Api");
        roots.Should().Contain("src/App.Services");
    }

    [Fact]
    public void IsCsFileInProjectRoot_ValidNewFileUnderNestedProject_ReturnsTrue()
    {
        // Arrange
        var roots = new[] { "src/App.Api", "src/App.Services" };

        // Act & Assert
        WorktreeEditApplier.IsCsFileInProjectRoot("src/App.Api/Controllers/SystemController.cs", roots).Should().BeTrue();
        WorktreeEditApplier.IsCsFileInProjectRoot("src/App.Services/Logging/LoggerService.cs", roots).Should().BeTrue();
    }

    [Fact]
    public async Task ApplyEditsAsync_InvalidRepositoryRootControllersFile_WhenNoRootCsproj_FailsWithActionableError()
    {
        // Arrange
        InitGitRepo(_worktreeDir);
        File.WriteAllText(Path.Combine(_worktreeDir, "README.md"), "# Test Repo");
        RunGit(_worktreeDir, "add", ".");
        RunGit(_worktreeDir, "commit", "-m", "Initial commit");

        // Create nested project
        Directory.CreateDirectory(Path.Combine(_worktreeDir, "src", "DevPilot.Api"));
        File.WriteAllText(Path.Combine(_worktreeDir, "src", "DevPilot.Api", "DevPilot.Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        RunGit(_worktreeDir, "add", ".");
        RunGit(_worktreeDir, "commit", "-m", "Add project");

        var applier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec(
                FilePath: "Controllers/SystemController.cs",
                Action: FileEditAction.Create,
                NewContent: "namespace Controllers { public class SystemController {} }")
        });

        // Act
        var result = await applier.ApplyEditsAsync(_worktreeDir, "master", plan);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Target path safety violation");
        result.ErrorMessage.Should().Contain("Controllers/SystemController.cs");
        result.ErrorMessage.Should().Contain("outside all discovered .NET project roots");
        File.Exists(Path.Combine(_worktreeDir, "Controllers", "SystemController.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyEditsAsync_ValidRepositoryRootCsFile_WhenRootCsprojExists_Succeeds()
    {
        // Arrange
        InitGitRepo(_worktreeDir);
        File.WriteAllText(Path.Combine(_worktreeDir, "RootApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(_worktreeDir, "README.md"), "# Test Repo");
        RunGit(_worktreeDir, "add", ".");
        RunGit(_worktreeDir, "commit", "-m", "Initial commit");

        var applier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec(
                FilePath: "Controllers/SystemController.cs",
                Action: FileEditAction.Create,
                NewContent: "namespace Controllers { public class SystemController {} }")
        });

        // Act
        var result = await applier.ApplyEditsAsync(_worktreeDir, "master", plan);

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(_worktreeDir, "Controllers", "SystemController.cs")).Should().BeTrue();
    }

    [Fact]
    public async Task ApplyEditsAsync_ExistingFileEdit_SucceedsNormally()
    {
        // Arrange
        InitGitRepo(_worktreeDir);
        var originalFile = Path.Combine(_worktreeDir, "README.md");
        File.WriteAllText(originalFile, "# Hello World");
        RunGit(_worktreeDir, "add", ".");
        RunGit(_worktreeDir, "commit", "-m", "Initial commit");

        var applier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec(
                FilePath: "README.md",
                Action: FileEditAction.Modify,
                SearchReplaceEdits: new[]
                {
                    new SearchReplaceEdit("Hello World", "Hello DevPilot")
                })
        });

        // Act
        var result = await applier.ApplyEditsAsync(_worktreeDir, "master", plan);

        // Assert
        result.Success.Should().BeTrue();
        File.ReadAllText(originalFile).Should().Be("# Hello DevPilot");
    }

    [Fact]
    public async Task ApplyEditsAsync_WindowsPathSeparators_ResolvedAndValidatedSuccessfully()
    {
        // Arrange
        InitGitRepo(_worktreeDir);
        Directory.CreateDirectory(Path.Combine(_worktreeDir, "src", "DevPilot.Api"));
        File.WriteAllText(Path.Combine(_worktreeDir, "src", "DevPilot.Api", "DevPilot.Api.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_worktreeDir, "README.md"), "# Repo");
        RunGit(_worktreeDir, "add", ".");
        RunGit(_worktreeDir, "commit", "-m", "Initial commit");

        var applier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec(
                FilePath: @"src\DevPilot.Api\Services\MyService.cs",
                Action: FileEditAction.Create,
                NewContent: "namespace DevPilot.Api.Services { public class MyService {} }")
        });

        // Act
        var result = await applier.ApplyEditsAsync(_worktreeDir, "master", plan);

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(_worktreeDir, "src", "DevPilot.Api", "Services", "MyService.cs")).Should().BeTrue();
    }

    [Fact]
    public void ValidateAndResolvePath_PathTraversal_RemainsRejected()
    {
        // Act & Assert
        var act1 = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, "../outside.cs");
        act1.Should().Throw<InvalidOperationException>().WithMessage("*Path safety violation*");

        var act2 = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, @"src\DevPilot.Api\..\..\..\outside.cs");
        act2.Should().Throw<InvalidOperationException>().WithMessage("*Path safety violation*");
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
}
