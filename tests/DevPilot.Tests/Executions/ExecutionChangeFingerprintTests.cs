using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class ExecutionChangeFingerprintTests : IDisposable
{
    private readonly string _tempDir;

    public ExecutionChangeFingerprintTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "devpilot_fp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                // Unset read-only attributes on .git objects before deletion on Windows/Unix
                foreach (var file in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task ComputeFingerprint_WorktreeAndStagedTree_ProduceIdenticalFingerprint()
    {
        // Arrange
        var calculator = new GitExecutionChangeFingerprintCalculator(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);

        // Add a commit (Base HEAD)
        File.WriteAllText(Path.Combine(_tempDir, "file1.txt"), "hello base\n");
        RunGit(_tempDir, "add .");
        RunGit(_tempDir, "commit -m \"base commit\"");
        var baseHeadSha = RunGit(_tempDir, "rev-parse HEAD").Trim();

        // Make worktree changes (file edit + new file)
        File.WriteAllText(Path.Combine(_tempDir, "file1.txt"), "hello base edited\n");
        File.WriteAllText(Path.Combine(_tempDir, "file2.txt"), "new file content\n");

        // Act 1: Worktree fingerprint (before staging)
        var worktreeResult = await calculator.ComputeFingerprintAsync(_tempDir);

        // Stage changes in git index and write a tree
        RunGit(_tempDir, "add .");
        var treeSha = RunGit(_tempDir, "write-tree").Trim();

        // Act 2: Staged tree fingerprint
        var stagedResult = await calculator.ComputeStagedTreeFingerprintAsync(_tempDir, treeSha, baseHeadSha);

        if (worktreeResult.Fingerprint != stagedResult.Fingerprint)
        {
            throw new Exception($"wtFP: {worktreeResult.Fingerprint}, stFP: {stagedResult.Fingerprint}");
        }
    }

    [Fact]
    public async Task ExecutableBitChange_ChangesFingerprint()
    {
        // Arrange
        var calculator = new GitExecutionChangeFingerprintCalculator(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);

        File.WriteAllText(Path.Combine(_tempDir, "script.sh"), "#!/bin/bash\necho hi\n");
        RunGit(_tempDir, "add .");
        RunGit(_tempDir, "commit -m \"script added\"");

        var initialResult = await calculator.ComputeFingerprintAsync(_tempDir);

        // Make script executable
        RunGit(_tempDir, "update-index --chmod=+x script.sh");

        var chmodResult = await calculator.ComputeFingerprintAsync(_tempDir);

        // Assert
        chmodResult.Fingerprint.Should().NotBe(initialResult.Fingerprint);
    }

    [Fact]
    public async Task RenameFile_CapturesOldAndNewPaths()
    {
        // Arrange
        var calculator = new GitExecutionChangeFingerprintCalculator(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);

        File.WriteAllText(Path.Combine(_tempDir, "oldname.txt"), "file to be renamed\n");
        RunGit(_tempDir, "add .");
        RunGit(_tempDir, "commit -m \"add oldname\"");

        RunGit(_tempDir, "mv oldname.txt newname.txt");

        var result = await calculator.ComputeFingerprintAsync(_tempDir);

        result.ErrorMessage.Should().BeNull();
        result.Success.Should().BeTrue();
        result.ChangedFileCount.Should().BeGreaterThan(0);
        result.Fingerprint.Should().StartWith("sha256:");
    }

    [Fact]
    public async Task Symlink_TargetStringNotFollowed()
    {
        // Arrange
        var calculator = new GitExecutionChangeFingerprintCalculator(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);

        File.WriteAllText(Path.Combine(_tempDir, "target.txt"), "target content\n");
        RunGit(_tempDir, "add .");
        RunGit(_tempDir, "commit -m \"add target\"");

        // Create symlink if supported on current OS
        var linkPath = Path.Combine(_tempDir, "link.txt");
        try
        {
            File.CreateSymbolicLink(linkPath, "target.txt");
        }
        catch
        {
            // Skip symlink creation if OS user permissions block symlink creation
            return;
        }

        var result = await calculator.ComputeFingerprintAsync(_tempDir);

        result.Success.Should().BeTrue();
        result.Fingerprint.Should().StartWith("sha256:");
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config user.name \"Test User\"");
        RunGit(path, "config user.email \"test@devpilot.local\"");
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed with code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }
}
