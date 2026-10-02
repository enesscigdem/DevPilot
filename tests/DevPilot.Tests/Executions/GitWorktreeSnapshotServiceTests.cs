using System.Diagnostics;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class GitWorktreeSnapshotServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "devpilot_snapshot_" + Guid.NewGuid().ToString("N"));

    public GitWorktreeSnapshotServiceTests()
    {
        Directory.CreateDirectory(_dir);
        Git("init");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "t@example.com");
        File.WriteAllText(Path.Combine(_dir, "A.cs"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(_dir, "B.cs"), "untouched\n");
        Git("add", "-A");
        Git("commit", "-m", "initial");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task TheDifferenceBetweenTwoSnapshots_IsExactlyWhatTheFixChanged()
    {
        var service = new GitWorktreeSnapshotService(NullLogger<GitWorktreeSnapshotService>.Instance);
        var id = Guid.NewGuid();

        // Uncommitted work that existed before the fix must not appear in the fix diff.
        File.WriteAllText(Path.Combine(_dir, "A.cs"), "one\ntwo\nthree\nfour\n");
        var before = await service.CaptureAsync(_dir, id, "r1-base");

        File.WriteAllText(Path.Combine(_dir, "A.cs"), "one\nTWO\nthree\nfour\n");
        File.WriteAllText(Path.Combine(_dir, "New.cs"), "created\n");
        var after = await service.CaptureAsync(_dir, id, "r1-result");

        before.Should().NotBeNullOrWhiteSpace();
        after.Should().NotBeNullOrWhiteSpace();

        var reader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var diff = await reader.ReadCommittedDiffAsync(_dir, before!, after!);

        diff.Success.Should().BeTrue(diff.ErrorMessage);
        diff.ChangedFiles!.Select(f => f.Path).Should().BeEquivalentTo(new[] { "A.cs", "New.cs" });
        var a = diff.ChangedFiles!.Single(f => f.Path == "A.cs");
        (a.Additions, a.Deletions).Should().Be((1, 1));
        diff.ChangedFiles!.Single(f => f.Path == "New.cs").Additions.Should().Be(1);
        diff.DiffText.Should().Contain("+TWO").And.NotContain("+four");
    }

    [Fact]
    public async Task TakingASnapshot_LeavesTheBranchIndexAndWorktreeUntouched()
    {
        var service = new GitWorktreeSnapshotService(NullLogger<GitWorktreeSnapshotService>.Instance);
        File.WriteAllText(Path.Combine(_dir, "A.cs"), "changed\n");
        var headBefore = Git("rev-parse", "HEAD").Trim();

        var sha = await service.CaptureAsync(_dir, Guid.NewGuid(), "r1-base");

        sha.Should().NotBeNullOrWhiteSpace();
        Git("rev-parse", "HEAD").Trim().Should().Be(headBefore);
        Git("diff", "--cached", "--name-only").Trim().Should().BeEmpty("the real index is not used");
        File.ReadAllText(Path.Combine(_dir, "A.cs")).Should().Be("changed\n");
        Git("for-each-ref", "refs/devpilot").Should().Contain(sha!, "the snapshot is pinned by a ref");
    }

    [Fact]
    public async Task ReturnsNullWhenThereIsNoRepository()
    {
        var service = new GitWorktreeSnapshotService(NullLogger<GitWorktreeSnapshotService>.Instance);

        var sha = await service.CaptureAsync(Path.Combine(_dir, "missing"), Guid.NewGuid(), "x");

        sha.Should().BeNull();
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _dir
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
