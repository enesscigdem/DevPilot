using System.Diagnostics;
using DevPilot.Application.RepositoryClone;
using DevPilot.Infrastructure.RepositoryClone;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.RepositoryWorkspaces;

/// <summary>
/// Real-git tests: fetch origin, fast-forward only when provably safe, never overwrite local work.
/// </summary>
public sealed class GitRepositoryFreshnessServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _local;
    private readonly string _other;
    private readonly GitRepositoryFreshnessService _service = new(NullLogger<GitRepositoryFreshnessService>.Instance);

    public GitRepositoryFreshnessServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DevPilotFreshness_" + Guid.NewGuid().ToString("N"));
        _origin = Path.Combine(_root, "origin.git");
        _local = Path.Combine(_root, "local");
        _other = Path.Combine(_root, "other");
        Directory.CreateDirectory(_root);

        Git(_root, "init", "--bare", "-b", "main", _origin);
        Git(_root, "clone", _origin, _local);
        Git(_local, "checkout", "-B", "main");
        Commit(_local, "README.md", "# repo", "initial");
        Git(_local, "push", "-u", "origin", "main");
        Git(_root, "clone", _origin, _other);
    }

    public void Dispose()
    {
        try
        {
            ForceDelete(_root);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public async Task UpToDate_ReportsUpToDate_AndChangesNothing()
    {
        var before = Head(_local);

        var result = await _service.RefreshAsync(Request());

        result.Status.Should().Be(RepositoryFreshnessStatus.UpToDate);
        result.IsStale.Should().BeFalse();
        result.LocalCommitSha.Should().Be(before);
        result.RemoteCommitSha.Should().Be(before);
        Head(_local).Should().Be(before);
    }

    [Fact]
    public async Task Behind_CleanTree_IsFastForwarded()
    {
        var oldHead = Head(_local);
        PushFromOther("feature.txt", "new work", "remote commit");
        var remoteHead = Head(_other);

        var result = await _service.RefreshAsync(Request());

        result.Status.Should().Be(RepositoryFreshnessStatus.FastForwarded);
        result.FastForwarded.Should().BeTrue();
        result.PreviousCommitSha.Should().Be(oldHead);
        result.LocalCommitSha.Should().Be(remoteHead);
        Head(_local).Should().Be(remoteHead);
        File.Exists(Path.Combine(_local, "feature.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Behind_WithLocalTrackedChanges_IsNotUpdated_AndLocalWorkIsPreserved()
    {
        var oldHead = Head(_local);
        PushFromOther("README.md", "# changed upstream", "remote edits readme");
        File.WriteAllText(Path.Combine(_local, "README.md"), "# my uncommitted work");

        var result = await _service.RefreshAsync(Request());

        result.Status.Should().Be(RepositoryFreshnessStatus.Behind);
        result.IsStale.Should().BeTrue();
        result.BehindCount.Should().Be(1);
        Head(_local).Should().Be(oldHead);
        File.ReadAllText(Path.Combine(_local, "README.md")).Should().Be("# my uncommitted work");
    }

    [Fact]
    public async Task Diverged_IsNeverTouched()
    {
        Commit(_local, "local.txt", "local only", "local commit");
        var localHead = Head(_local);
        PushFromOther("remote.txt", "remote only", "remote commit");

        var result = await _service.RefreshAsync(Request());

        result.Status.Should().Be(RepositoryFreshnessStatus.Diverged);
        result.IsStale.Should().BeTrue();
        result.AheadCount.Should().Be(1);
        result.BehindCount.Should().Be(1);
        Head(_local).Should().Be(localHead);
        File.Exists(Path.Combine(_local, "remote.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Ahead_IsReportedAndNotTouched()
    {
        Commit(_local, "local.txt", "local only", "local commit");
        var localHead = Head(_local);

        var result = await _service.RefreshAsync(Request());

        result.Status.Should().Be(RepositoryFreshnessStatus.Ahead);
        result.IsStale.Should().BeFalse();
        Head(_local).Should().Be(localHead);
    }

    [Fact]
    public async Task UnreachableOrigin_ReturnsFetchFailed_WithoutThrowing_AndWithoutChangingTheClone()
    {
        var before = Head(_local);
        ForceDelete(_origin);

        var result = await _service.RefreshAsync(Request());

        result.Status.Should().Be(RepositoryFreshnessStatus.FetchFailed);
        result.IsStale.Should().BeFalse("freshness is unknown, not proven stale");
        Head(_local).Should().Be(before);
    }

    [Fact]
    public async Task DetachedHead_IsNotApplicable_AndLeftAlone()
    {
        var head = Head(_local);
        Git(_local, "checkout", "--detach", "HEAD");

        var result = await _service.RefreshAsync(Request());

        result.Status.Should().Be(RepositoryFreshnessStatus.NotApplicable);
        Head(_local).Should().Be(head);
    }

    [Fact]
    public async Task MissingDirectory_IsNotApplicable()
    {
        var result = await _service.RefreshAsync(new RepositoryFreshnessRequest(
            Path.Combine(_root, "nope"), "owner", "repo", "main"));

        result.Status.Should().Be(RepositoryFreshnessStatus.NotApplicable);
    }

    private RepositoryFreshnessRequest Request() => new(_local, "owner", "repo", "main");

    private void PushFromOther(string file, string content, string message)
    {
        Git(_other, "pull", "--ff-only", "origin", "main");
        Commit(_other, file, content, message);
        Git(_other, "push", "origin", "main");
    }

    private static void Commit(string repo, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=test@devpilot.local", "-c", "user.name=Test", "commit", "-m", message);
    }

    private static string Head(string repo) => Git(repo, "rev-parse", "HEAD").Trim();

    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }

        return stdout;
    }

    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
