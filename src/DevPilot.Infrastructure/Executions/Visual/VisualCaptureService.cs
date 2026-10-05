using System.Diagnostics;
using System.Text.Json;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions.Visual;

/// <summary>
/// Builds the app at the base commit and with the execution's changes, screenshots both with the installed headless
/// Chrome/Edge and stores the pairs for review. Every problem is reported in the manifest instead of thrown: a visual
/// check is evidence for the reviewer and must never fail or block an execution.
/// </summary>
public sealed class VisualCaptureService : IVisualCaptureService
{
    private static readonly (string Name, int Width, int Height)[] Viewports =
    {
        ("desktop", 1440, 900),
        ("mobile", 390, 844)
    };

    private static readonly string[] OutputDirectories = { "dist", "build", "out" };

    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private readonly IProcessRunner _processRunner;
    private readonly PackageManagerProcessResolver _packageManagerResolver;
    private readonly ILogger<VisualCaptureService> _logger;

    public VisualCaptureService(
        IProcessRunner processRunner,
        ILogger<VisualCaptureService> logger,
        PackageManagerProcessResolver? packageManagerResolver = null)
    {
        _processRunner = processRunner;
        _logger = logger;
        _packageManagerResolver = packageManagerResolver ?? new PackageManagerProcessResolver();
    }

    public async Task<VisualCaptureManifest> CaptureAsync(
        VisualCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        var manifest = new VisualCaptureManifest
        {
            CapturedAtUtc = DateTime.UtcNow,
            BaseCommitSha = request.BaseCommitSha
        };
        var directory = VisualArtifactStore.DirectoryFor(request.WorkspacePath, request.ExecutionId);
        var baseWorktree = Path.Combine(directory, "_base");
        var profileDirectory = Path.Combine(directory, "_browser");

        try
        {
            if (!VisualChangeDetector.HasUiChanges(request.ChangedFiles))
            {
                manifest.Status = "Skipped";
                manifest.Reason = "No UI files changed.";
                return await SaveAsync(directory, manifest).ConfigureAwait(false);
            }

            // UI files changed: a person has to look, whatever the capture below manages to do.
            manifest.RequiresReview = true;
            ResetDirectory(directory);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OverallTimeout);
            var token = timeout.Token;

            var skipReason = CheckPrerequisites(request.WorkspacePath, out var browser);
            if (skipReason != null)
            {
                manifest.Status = "Skipped";
                manifest.Reason = skipReason;
                return await SaveAsync(directory, manifest).ConfigureAwait(false);
            }

            var notes = new List<string>();

            var afterRoot = await EnsureBuiltAsync(request.WorkspacePath, request.ChangedFiles, token).ConfigureAwait(false);
            if (afterRoot == null)
            {
                manifest.Status = "Failed";
                manifest.Reason = "The app could not be built for screenshots (it needs a build script that writes dist, build or out).";
                return await SaveAsync(directory, manifest).ConfigureAwait(false);
            }

            string? beforeRoot = null;
            var baseline = await TryBuildBaselineAsync(request, baseWorktree, token).ConfigureAwait(false);
            if (baseline.Root != null)
            {
                beforeRoot = baseline.Root;
            }
            else if (baseline.Note != null)
            {
                notes.Add(baseline.Note);
            }

            using var afterSite = StartSite(afterRoot);
            using var beforeSite = beforeRoot != null ? StartSite(beforeRoot) : null;
            Directory.CreateDirectory(profileDirectory);

            foreach (var (name, width, height) in Viewports)
            {
                var shot = new VisualShot { Page = "/", Viewport = name, Width = width, Height = height };

                var afterFile = $"after-{name}.png";
                if (await ScreenshotAsync(browser!, afterSite.BaseUrl, Path.Combine(directory, afterFile), profileDirectory, width, height, token).ConfigureAwait(false))
                {
                    shot.AfterFile = afterFile;
                }

                if (beforeSite != null)
                {
                    var beforeFile = $"before-{name}.png";
                    if (await ScreenshotAsync(browser!, beforeSite.BaseUrl, Path.Combine(directory, beforeFile), profileDirectory, width, height, token).ConfigureAwait(false))
                    {
                        shot.BeforeFile = beforeFile;
                    }
                }

                if (shot.AfterFile != null || shot.BeforeFile != null)
                {
                    manifest.Shots.Add(shot);
                }
            }

            var complete = manifest.Shots.Count == Viewports.Length &&
                           manifest.Shots.All(s => s.AfterFile != null && s.BeforeFile != null);
            manifest.Status = manifest.Shots.Count == 0 ? "Failed" : complete ? "Captured" : "Partial";
            manifest.Reason = manifest.Shots.Count == 0
                ? "The browser could not produce a screenshot."
                : notes.Count > 0 ? string.Join(" ", notes) : null;
            return await SaveAsync(directory, manifest).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            manifest.Status = "Failed";
            manifest.Reason = "The visual check timed out.";
            return await SaveAsync(directory, manifest).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Visual capture failed for execution {ExecutionId}.", request.ExecutionId);
            manifest.Status = "Failed";
            manifest.Reason = $"The visual check failed: {ex.Message}";
            return await SaveAsync(directory, manifest).ConfigureAwait(false);
        }
        finally
        {
            await RemoveBaseWorktreeAsync(request.WorkspacePath, baseWorktree).ConfigureAwait(false);
            TryDeleteDirectory(profileDirectory);
        }
    }

    private static string? CheckPrerequisites(string workspacePath, out string? browser)
    {
        browser = HeadlessBrowserLocator.Find();
        if (browser == null)
        {
            return "No Chrome, Chromium or Edge was found on this machine. Set DEVPILOT_BROWSER to a browser executable to enable screenshots.";
        }

        var packageJson = Path.Combine(workspacePath, "package.json");
        if (!File.Exists(packageJson))
        {
            return "The repository has no package.json, so there is no web app to screenshot.";
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object ||
                !scripts.TryGetProperty("build", out _))
            {
                return "package.json has no build script, so the app cannot be built for screenshots.";
            }
        }
        catch (JsonException)
        {
            return "package.json could not be read.";
        }

        return null;
    }

    /// <summary>Uses the existing build output when it is newer than every changed file, otherwise builds.</summary>
    private async Task<string?> EnsureBuiltAsync(
        string projectDirectory,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken)
    {
        var existing = FindBuildOutput(projectDirectory);
        if (existing != null && IsNewerThanChanges(existing, projectDirectory, changedFiles))
        {
            return existing;
        }

        if (!await RunBuildAsync(projectDirectory, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return FindBuildOutput(projectDirectory);
    }

    private async Task<(string? Root, string? Note)> TryBuildBaselineAsync(
        VisualCaptureRequest request,
        string baseWorktree,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaseCommitSha))
        {
            return (null, "No baseline: the base commit is unknown.");
        }

        if (request.ChangedFiles.Any(IsDependencyFile))
        {
            return (null, "No baseline: dependencies changed, so the base commit cannot reuse the installed packages.");
        }

        var sourceModules = Path.Combine(request.WorkspacePath, "node_modules");
        if (!Directory.Exists(sourceModules))
        {
            return (null, "No baseline: dependencies are not installed in the execution workspace.");
        }

        var add = await RunGitAsync(
            request.WorkspacePath,
            new[] { "worktree", "add", "--detach", baseWorktree, request.BaseCommitSha },
            cancellationToken).ConfigureAwait(false);
        if (!add)
        {
            return (null, "No baseline: the base commit could not be checked out.");
        }

        if (!TryLinkDirectory(Path.Combine(baseWorktree, "node_modules"), sourceModules))
        {
            return (null, "No baseline: the installed packages could not be shared with the base checkout.");
        }

        if (!await RunBuildAsync(baseWorktree, cancellationToken).ConfigureAwait(false))
        {
            return (null, "No baseline: the base commit does not build.");
        }

        var root = FindBuildOutput(baseWorktree);
        return root == null ? (null, "No baseline: the base build produced no output folder.") : (root, null);
    }

    private static bool IsDependencyFile(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/'));
        return name is "package.json" or "package-lock.json" or "npm-shrinkwrap.json" or "pnpm-lock.yaml" or "yarn.lock";
    }

    private async Task<bool> RunBuildAsync(string directory, CancellationToken cancellationToken)
    {
        if (!_packageManagerResolver.TryResolve("npm", new[] { "run", "build" }, out var fileName, out var arguments, out _))
        {
            return false;
        }

        var result = await _processRunner.RunProcessAsync(fileName, arguments, directory, BuildTimeout, cancellationToken)
            .ConfigureAwait(false);
        return result is { ExitCode: 0, IsTimedOut: false };
    }

    private async Task<bool> RunGitAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunProcessAsync("git", arguments, directory, GitTimeout, cancellationToken)
                .ConfigureAwait(false);
            return result is { ExitCode: 0, IsTimedOut: false };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> ScreenshotAsync(
        string browser,
        string url,
        string outputFile,
        string profileDirectory,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "--headless=new",
            "--disable-gpu",
            "--hide-scrollbars",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--force-device-scale-factor=1",
            $"--user-data-dir={profileDirectory}",
            $"--window-size={width},{height}",
            "--virtual-time-budget=6000",
            $"--screenshot={outputFile}",
            url
        };

        try
        {
            await _processRunner.RunProcessAsync(browser, arguments, Path.GetDirectoryName(outputFile)!, ScreenshotTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Headless browser failed to screenshot {Url}.", url);
        }

        var file = new FileInfo(outputFile);
        return file.Exists && file.Length > 0;
    }

    private static StaticSiteServer StartSite(string root)
    {
        var server = new StaticSiteServer(root);
        server.Start();
        return server;
    }

    private static string? FindBuildOutput(string projectDirectory)
    {
        foreach (var name in OutputDirectories)
        {
            var candidate = Path.Combine(projectDirectory, name);
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsNewerThanChanges(string outputDirectory, string projectDirectory, IReadOnlyList<string> changedFiles)
    {
        try
        {
            var built = File.GetLastWriteTimeUtc(Path.Combine(outputDirectory, "index.html"));
            var newestChange = changedFiles
                .Select(file => Path.Combine(projectDirectory, file))
                .Where(File.Exists)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return built >= newestChange;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Links a directory without copying it: a symlink, or a junction on Windows when symlinks are not allowed.</summary>
    private static bool TryLinkDirectory(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        // Fixed, internally built arguments only: nothing from the repository reaches this command.
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(linkPath);
            psi.ArgumentList.Add(targetPath);
            using var process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(15_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task RemoveBaseWorktreeAsync(string workspacePath, string baseWorktree)
    {
        if (!Directory.Exists(baseWorktree))
        {
            return;
        }

        // The packages are shared through a link: remove the link itself first, never what it points to.
        var modules = Path.Combine(baseWorktree, "node_modules");
        try
        {
            if (Directory.Exists(modules) &&
                new DirectoryInfo(modules).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(modules, recursive: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the shared node_modules link in {Path}; keeping the folder so the packages are not deleted.", modules);
            return;
        }

        await RunGitAsync(workspacePath, new[] { "worktree", "remove", "--force", baseWorktree }, CancellationToken.None).ConfigureAwait(false);
        await RunGitAsync(workspacePath, new[] { "worktree", "prune" }, CancellationToken.None).ConfigureAwait(false);
        TryDeleteDirectory(baseWorktree);
    }

    private static void ResetDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(directory, "*.png"))
        {
            try { File.Delete(file); } catch (IOException) { /* replaced by the next capture */ }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort cleanup of temporary folders.
        }
    }

    private static async Task<VisualCaptureManifest> SaveAsync(string directory, VisualCaptureManifest manifest)
    {
        try
        {
            await VisualArtifactStore.SaveManifestAsync(directory, manifest).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The in-memory manifest is still returned to the caller.
        }

        return manifest;
    }
}
