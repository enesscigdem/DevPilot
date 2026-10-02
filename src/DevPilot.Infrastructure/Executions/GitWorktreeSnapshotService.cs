using System.Diagnostics;
using System.Text;
using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class GitWorktreeSnapshotService : IExecutionWorktreeSnapshotService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger<GitWorktreeSnapshotService> _logger;

    public GitWorktreeSnapshotService(ILogger<GitWorktreeSnapshotService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> CaptureAsync(
        string workspacePath,
        Guid executionId,
        string label,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return null;
        }

        var workspace = Path.GetFullPath(workspacePath);
        var tempIndex = Path.Combine(Path.GetTempPath(), $"devpilot_snapshot_index_{Guid.NewGuid():N}.tmp");
        var indexEnv = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };

        try
        {
            // An isolated index keeps the real index and the branch untouched.
            if (!(await RunAsync(workspace, indexEnv, cancellationToken, "read-tree", "HEAD").ConfigureAwait(false)).Ok ||
                !(await RunAsync(workspace, indexEnv, cancellationToken, new[] { "add", "-A", "--" }.Concat(WorkspaceChangeScope.WorktreePathspecs()).ToArray()).ConfigureAwait(false)).Ok)
            {
                return null;
            }

            var tree = await RunAsync(workspace, indexEnv, cancellationToken, "write-tree").ConfigureAwait(false);
            if (!tree.Ok || string.IsNullOrWhiteSpace(tree.StdOut))
            {
                return null;
            }

            var identity = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "DevPilot",
                ["GIT_AUTHOR_EMAIL"] = "devpilot@localhost",
                ["GIT_COMMITTER_NAME"] = "DevPilot",
                ["GIT_COMMITTER_EMAIL"] = "devpilot@localhost",
            };
            var commit = await RunAsync(
                workspace,
                identity,
                cancellationToken,
                "commit-tree", tree.StdOut.Trim(), "-m", $"devpilot snapshot {label} of execution {executionId}").ConfigureAwait(false);
            if (!commit.Ok || string.IsNullOrWhiteSpace(commit.StdOut))
            {
                return null;
            }

            var sha = commit.StdOut.Trim();
            // A ref keeps the otherwise unreachable commit from being garbage collected.
            var pinned = await RunAsync(
                workspace,
                null,
                cancellationToken,
                "update-ref", $"refs/devpilot/snapshots/{executionId:N}/{label}", sha).ConfigureAwait(false);
            return pinned.Ok ? sha : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Worktree snapshot '{Label}' failed for execution {ExecutionId}.", label, executionId);
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tempIndex))
                {
                    File.Delete(tempIndex);
                }
            }
            catch (IOException)
            {
                // Temporary index cleanup is best effort.
            }
        }
    }

    private static async Task<(bool Ok, string StdOut)> RunAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var timeout = new CancellationTokenSource(GitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (environment != null)
        {
            foreach (var pair in environment)
            {
                psi.EnvironmentVariables[pair.Key] = pair.Value;
            }
        }

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        await stderr.ConfigureAwait(false);
        return (process.ExitCode == 0, await stdout.ConfigureAwait(false));
    }
}
