using System.Diagnostics;
using DevPilot.Application.Executions.Models;
using DevPilot.Infrastructure.Executions;
using DevPilot.Infrastructure.Executions.Visual;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class VisualChangeDetectorTests
{
    [Theory]
    [InlineData("src/App.tsx", true)]
    [InlineData("src/index.css", true)]
    [InlineData("index.html", true)]
    [InlineData("src\\components\\Card.jsx", true)]
    [InlineData("src/App.test.tsx", false)]
    [InlineData("src/__tests__/a.tsx", false)]
    [InlineData("src/util.ts", false)]
    [InlineData("README.md", false)]
    [InlineData("node_modules/x/y.css", false)]
    public void IsUiFile_ClassifiesByExtensionAndTestPath(string path, bool expected) =>
        VisualChangeDetector.IsUiFile(path).Should().Be(expected);
}

public sealed class StaticSiteServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dp-site-" + Guid.NewGuid().ToString("N"));

    public StaticSiteServerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<h1>x</h1>");
        File.WriteAllText(Path.Combine(_root, "assets", "a.js"), "1");
    }

    [Fact]
    public void ResolveFile_ServesFilesAndSpaFallback_ButNeverEscapesTheRoot()
    {
        using var server = new StaticSiteServer(_root);

        server.ResolveFile("/").Should().EndWith("index.html");
        server.ResolveFile("/assets/a.js").Should().EndWith("a.js");
        server.ResolveFile("/some/client/route").Should().EndWith("index.html");
        server.ResolveFile("/assets/missing.js").Should().BeNull();
        server.ResolveFile("/../../etc/passwd").Should().BeNull();
        server.ResolveFile("/..%2f..%2fsecret.txt").Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}

public sealed class VisualArtifactStoreTests
{
    [Fact]
    public void ResolveImagePath_AcceptsOnlyKnownScreenshotNames()
    {
        var store = new VisualArtifactStore();
        var workspace = Path.Combine(Path.GetTempPath(), "dp-root", "executions", Guid.NewGuid().ToString());

        store.ResolveImagePath(workspace, Guid.NewGuid(), "../manifest.json").Should().BeNull();
        store.ResolveImagePath(workspace, Guid.NewGuid(), "after-desktop.png").Should().BeNull("the file does not exist");
        store.ResolveImagePath(workspace, Guid.NewGuid(), "..\\after-desktop.png").Should().BeNull();
        store.ResolveImagePath(workspace, Guid.NewGuid(), "evil.png").Should().BeNull();
    }

    [Fact]
    public void DirectoryFor_PlacesArtifactsBesideTheExecutionsFolder()
    {
        var id = Guid.NewGuid();
        var workspace = Path.Combine(Path.GetTempPath(), "dp-root", "executions", id.ToString());

        VisualArtifactStore.DirectoryFor(workspace, id)
            .Should().Be(Path.Combine(Path.GetTempPath(), "dp-root", "visual", id.ToString()));
    }
}

/// <summary>
/// Runs the real capture pipeline (git worktree, build script, static server, headless browser) on a tiny project.
/// Skips itself when node, git or a Chromium-based browser is not installed.
/// </summary>
public sealed class VisualCaptureServiceEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dp-visual-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Capture_ProducesBeforeAndAfterShots_AndLeavesTheWorkspaceUntouched()
    {
        if (HeadlessBrowserLocator.Find() == null || !Available("git") || !Available("node"))
        {
            return;
        }

        var executionId = Guid.NewGuid();
        var workspace = Path.Combine(_root, "executions", executionId.ToString());
        Directory.CreateDirectory(workspace);

        File.WriteAllText(Path.Combine(workspace, "package.json"), "{ \"scripts\": { \"build\": \"node build.js\" } }");
        File.WriteAllText(Path.Combine(workspace, "build.js"),
            "const fs=require('fs');fs.rmSync('dist',{recursive:true,force:true});fs.mkdirSync('dist');" +
            "for(const f of ['index.html','style.css'])fs.copyFileSync(f,'dist/'+f);");
        File.WriteAllText(Path.Combine(workspace, "index.html"),
            "<!doctype html><link rel=\"stylesheet\" href=\"/style.css\"><h1>Hello</h1>");
        File.WriteAllText(Path.Combine(workspace, "style.css"), "body{background:#ffffff;color:#000000}h1{font-size:48px}");
        File.WriteAllText(Path.Combine(workspace, ".gitignore"), "node_modules\ndist\n");

        Git(workspace, "init", "-q");
        Git(workspace, "add", ".");
        Git(workspace, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "base");
        var baseSha = Git(workspace, "rev-parse", "HEAD").Trim();

        Directory.CreateDirectory(Path.Combine(workspace, "node_modules"));
        File.WriteAllText(Path.Combine(workspace, "node_modules", "marker.txt"), "keep");
        File.WriteAllText(Path.Combine(workspace, "style.css"), "body{background:#102030;color:#ffffff}h1{font-size:48px}");

        var service = new VisualCaptureService(
            new BoundedProcessRunner(NullLogger<BoundedProcessRunner>.Instance),
            NullLogger<VisualCaptureService>.Instance);

        var manifest = await service.CaptureAsync(new VisualCaptureRequest(
            executionId, workspace, baseSha, new[] { "style.css" }));

        manifest.RequiresReview.Should().BeTrue();
        manifest.Status.Should().Be("Captured", manifest.Reason);
        manifest.Shots.Should().HaveCount(2);

        var directory = VisualArtifactStore.DirectoryFor(workspace, executionId);
        foreach (var shot in manifest.Shots)
        {
            File.Exists(Path.Combine(directory, shot.BeforeFile!)).Should().BeTrue();
            File.Exists(Path.Combine(directory, shot.AfterFile!)).Should().BeTrue();
        }

        File.ReadAllBytes(Path.Combine(directory, "before-desktop.png"))
            .Should().NotEqual(File.ReadAllBytes(Path.Combine(directory, "after-desktop.png")), "the stylesheet changed");

        var stored = await new VisualArtifactStore().GetManifestAsync(workspace, executionId);
        stored!.Status.Should().Be("Captured");

        Directory.Exists(Path.Combine(directory, "_base")).Should().BeFalse("the temporary base checkout is removed");
        File.ReadAllText(Path.Combine(workspace, "node_modules", "marker.txt")).Should().Be("keep");
        File.ReadAllText(Path.Combine(workspace, "style.css")).Should().Contain("#102030");
        Git(workspace, "worktree", "list").Trim().Split('\n').Should().HaveCount(1);
    }

    [Fact]
    public async Task Capture_SkipsWithoutBlocking_WhenNoUiFilesChanged()
    {
        var executionId = Guid.NewGuid();
        var workspace = Path.Combine(_root, "executions", executionId.ToString());
        Directory.CreateDirectory(workspace);

        var manifest = await new VisualCaptureService(
                new BoundedProcessRunner(NullLogger<BoundedProcessRunner>.Instance),
                NullLogger<VisualCaptureService>.Instance)
            .CaptureAsync(new VisualCaptureRequest(executionId, workspace, null, new[] { "src/util.ts", "README.md" }));

        manifest.Status.Should().Be("Skipped");
        manifest.RequiresReview.Should().BeFalse();
    }

    private static bool Available(string tool)
    {
        try
        {
            var psi = new ProcessStartInfo(tool, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            process!.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Git(string directory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }
}
