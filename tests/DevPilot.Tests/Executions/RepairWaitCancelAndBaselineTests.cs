using System.Diagnostics;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

/// <summary>
/// Regression tests for the repair that waited for ~10,000 untracked node_modules files, the cancel rule that ignored
/// an active revision, and a baseline comparison that treated an unreadable failure as pre-existing.
/// </summary>
public sealed class RepairWaitCancelAndBaselineTests : IDisposable
{
    private readonly string _tempDir;

    public RepairWaitCancelAndBaselineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "devpilot_scope_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        RunGit("init");
        RunGit("config user.name \"Test User\"");
        RunGit("config user.email \"test@devpilot.local\"");
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "base\n");
        RunGit("add .");
        RunGit("commit -m base");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static GitExecutionChangeFingerprintCalculator NewCalculator() =>
        new(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);

    [Fact]
    public async Task Fingerprint_IgnoresDependencyInstallAndBuildOutput_AndStaysFast()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "edited\n");
        var calculator = NewCalculator();
        var before = await calculator.ComputeFingerprintAsync(_tempDir);

        // What a verification build leaves behind in a repository without a .gitignore.
        var modules = Path.Combine(_tempDir, "node_modules", "pkg");
        Directory.CreateDirectory(modules);
        for (var i = 0; i < 1500; i++)
        {
            File.WriteAllText(Path.Combine(modules, $"f{i}.js"), $"// {i}\n");
        }
        File.WriteAllText(Path.Combine(_tempDir, "tsconfig.tsbuildinfo"), "{}");

        var stopwatch = Stopwatch.StartNew();
        var after = await calculator.ComputeFingerprintAsync(_tempDir);
        stopwatch.Stop();

        after.Success.Should().BeTrue();
        after.Fingerprint.Should().Be(before.Fingerprint);
        after.ChangedFileCount.Should().Be(1);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Fingerprint_HashesManyChangedFilesInOneBatch_AndMatchesTheStagedTree()
    {
        var baseHead = RunGit("rev-parse HEAD").Trim();
        for (var i = 0; i < 250; i++)
        {
            File.WriteAllText(Path.Combine(_tempDir, $"src{i}.ts"), $"export const v{i} = {i};\n");
        }

        var calculator = NewCalculator();
        var stopwatch = Stopwatch.StartNew();
        var worktree = await calculator.ComputeFingerprintAsync(_tempDir);
        stopwatch.Stop();

        RunGit("add .");
        var tree = RunGit("write-tree").Trim();
        var staged = await calculator.ComputeStagedTreeFingerprintAsync(_tempDir, tree, baseHead);

        worktree.Success.Should().BeTrue();
        worktree.ChangedFileCount.Should().Be(250);
        worktree.Fingerprint.Should().Be(staged.Fingerprint);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Fingerprint_StopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => NewCalculator().ComputeFingerprintAsync(_tempDir, cts.Token);

        // A caller cancellation is never reported as a fingerprint timeout.
        var result = await Record.ExceptionAsync(act);
        result.Should().BeNull("a cancelled git call returns a failed result instead of hanging");
        (await act()).Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Running, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed, ExecutionPullRequestStatus.Open, ExecutionMergeStatus.None, true)]
    [InlineData(TaskExecutionStatus.Running, ExecutionCommitStatus.None, ExecutionPushStatus.None, ExecutionPullRequestStatus.None, ExecutionMergeStatus.None, true)]
    [InlineData(TaskExecutionStatus.Pending, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed, ExecutionPullRequestStatus.Open, ExecutionMergeStatus.None, true)]
    [InlineData(TaskExecutionStatus.Running, ExecutionCommitStatus.InProgress, ExecutionPushStatus.None, ExecutionPullRequestStatus.None, ExecutionMergeStatus.None, false)]
    [InlineData(TaskExecutionStatus.Running, ExecutionCommitStatus.Committed, ExecutionPushStatus.InProgress, ExecutionPullRequestStatus.None, ExecutionMergeStatus.None, false)]
    [InlineData(TaskExecutionStatus.Running, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed, ExecutionPullRequestStatus.InProgress, ExecutionMergeStatus.None, false)]
    [InlineData(TaskExecutionStatus.Running, ExecutionCommitStatus.Committed, ExecutionPushStatus.Pushed, ExecutionPullRequestStatus.Open, ExecutionMergeStatus.InProgress, false)]
    [InlineData(TaskExecutionStatus.Completed, ExecutionCommitStatus.None, ExecutionPushStatus.None, ExecutionPullRequestStatus.None, ExecutionMergeStatus.None, false)]
    [InlineData(TaskExecutionStatus.Failed, ExecutionCommitStatus.None, ExecutionPushStatus.None, ExecutionPullRequestStatus.None, ExecutionMergeStatus.None, false)]
    [InlineData(TaskExecutionStatus.Cancelled, ExecutionCommitStatus.None, ExecutionPushStatus.None, ExecutionPullRequestStatus.None, ExecutionMergeStatus.None, false)]
    public void CancelPolicy_AllowsActiveRevisionAfterDelivery_ButProtectsRunningDelivery(
        TaskExecutionStatus status,
        ExecutionCommitStatus commit,
        ExecutionPushStatus push,
        ExecutionPullRequestStatus pullRequest,
        ExecutionMergeStatus merge,
        bool canCancel)
    {
        var execution = new TaskExecution
        {
            Status = status,
            CommitStatus = commit,
            PushStatus = push,
            PullRequestStatus = pullRequest,
            MergeStatus = merge,
        };

        (ExecutionCancellationPolicy.DescribeWhyCannotCancel(execution) is null).Should().Be(canCancel);
    }

    [Fact]
    public void Baseline_UnstructuredFailureIsNeverProvenPreExisting()
    {
        // "npm run build" that could not even start prints the same generic line on the task and on the base commit.
        var generic = new NormalizedFailureItem(
            FailureKey: "same-generic-key",
            TestName: null,
            ErrorSummary: "Build failed with exit code 1.",
            NormalizedDiagnostic: "build failed with exit code 1.");

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            new[] { generic },
            new[] { generic },
            baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.Unknown);
        comparison.PreExistingCount.Should().Be(0);
    }

    [Fact]
    public void Baseline_StructuredIdenticalFailureIsStillPreExisting()
    {
        var item = new NormalizedFailureItem(
            FailureKey: "k1",
            TestName: null,
            ErrorSummary: "src/a.ts(1,1): error TS2322: x",
            NormalizedDiagnostic: "src/a.ts:1:1:TS2322:x",
            Location: "src/a.ts:1");

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(new[] { item }, new[] { item }, baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.PreExisting);
    }

    private string RunGit(string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = _tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        return stdout;
    }
}
