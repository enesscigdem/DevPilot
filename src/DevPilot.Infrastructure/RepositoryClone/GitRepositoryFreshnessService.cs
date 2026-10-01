using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DevPilot.Application.RepositoryClone;
using DevPilot.Infrastructure.Executions;
using DevPilot.Infrastructure.GitProviders;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.RepositoryClone;

/// <summary>
/// Fetches origin into the remote-tracking ref only, then fast-forwards the managed clone when (and only when)
/// the checked-out branch is strictly behind origin and has no tracked local modifications.
/// Diverged, ahead, dirty or wrong-branch clones are never modified.
/// </summary>
public sealed class GitRepositoryFreshnessService : IRepositoryFreshnessService
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LocalGitTimeout = TimeSpan.FromSeconds(30);

    // Serializes freshness operations per clone so concurrent analyses/executions never race on git locks.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly IGitHubAppTokenService? _tokenService;
    private readonly ILogger<GitRepositoryFreshnessService> _logger;

    public GitRepositoryFreshnessService(
        ILogger<GitRepositoryFreshnessService> logger,
        IGitHubAppTokenService? tokenService = null)
    {
        _logger = logger;
        _tokenService = tokenService;
    }

    public async Task<RepositoryFreshnessResult> RefreshAsync(
        RepositoryFreshnessRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.LocalPath) || !Directory.Exists(request.LocalPath))
        {
            return NotApplicable("Local repository path does not exist.");
        }

        var path = Path.GetFullPath(request.LocalPath);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(path, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Repository freshness check failed unexpectedly for '{Path}'.", path);
            return new RepositoryFreshnessResult(
                RepositoryFreshnessStatus.FetchFailed, request.Branch, null, null, 0, 0, null,
                "Repository freshness could not be determined.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RepositoryFreshnessResult> RefreshCoreAsync(
        string path,
        RepositoryFreshnessRequest request,
        CancellationToken cancellationToken)
    {
        var inside = await GitAsync(path, null, LocalGitTimeout, cancellationToken, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
        if (!inside.Success || inside.StdOut.Trim() != "true")
        {
            return NotApplicable("Not a git repository.");
        }

        var branchResult = await GitAsync(path, null, LocalGitTimeout, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        var currentBranch = branchResult.StdOut.Trim();
        if (!branchResult.Success || string.IsNullOrWhiteSpace(currentBranch) || currentBranch == "HEAD")
        {
            return NotApplicable("Repository is on a detached HEAD; base freshness was not checked.");
        }

        var branch = currentBranch;
        var localSha = await RevParseAsync(path, "HEAD", cancellationToken).ConfigureAwait(false);

        var originUrl = await GitAsync(path, null, LocalGitTimeout, cancellationToken, "remote", "get-url", "origin").ConfigureAwait(false);
        if (!originUrl.Success || string.IsNullOrWhiteSpace(originUrl.StdOut))
        {
            return new RepositoryFreshnessResult(
                RepositoryFreshnessStatus.NotApplicable, branch, localSha, null, 0, 0, null, "No origin remote is configured.");
        }

        var parsed = GitRemoteUrlNormalizer.ParseGitHubUrl(originUrl.StdOut.Trim());
        if (parsed is { } remote &&
            (!string.Equals(remote.Owner, request.Owner, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(remote.Repo, request.Repository, StringComparison.OrdinalIgnoreCase)))
        {
            return new RepositoryFreshnessResult(
                RepositoryFreshnessStatus.NotApplicable, branch, localSha, null, 0, 0, null,
                "Origin does not match the configured repository; base freshness was not checked.");
        }

        // Fetch into the remote-tracking ref only; the working tree and local branches are not touched.
        var remoteRef = $"refs/remotes/origin/{branch}";
        string? tempHome = null;
        Dictionary<string, string>? env = null;
        try
        {
            if (_tokenService != null)
            {
                try
                {
                    var token = await _tokenService
                        .GetTokenForRepositoryAsync(request.Owner, request.Repository, cancellationToken)
                        .ConfigureAwait(false);
                    if (token.IsSuccess && !string.IsNullOrWhiteSpace(token.Token))
                    {
                        tempHome = GitAuthenticationHelper.CreateTransientHomeDirectory(token.Token);
                        env = new Dictionary<string, string>
                        {
                            ["HOME"] = tempHome,
                            ["USERPROFILE"] = tempHome,
                        };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "No GitHub token available for freshness fetch; trying unauthenticated fetch.");
                }
            }

            var fetch = await GitAsync(
                path,
                env,
                FetchTimeout,
                cancellationToken,
                "fetch", "--no-tags", "--quiet", "origin", $"+refs/heads/{branch}:{remoteRef}").ConfigureAwait(false);

            if (!fetch.Success)
            {
                var message = $"Could not fetch origin: {GitRemoteUrlNormalizer.SanitizeOutput(fetch.StdErr)}".Trim();
                _logger.LogWarning("Repository freshness fetch failed for '{Path}': {Message}", path, message);
                return new RepositoryFreshnessResult(
                    RepositoryFreshnessStatus.FetchFailed, branch, localSha, null, 0, 0, null, message);
            }
        }
        finally
        {
            GitAuthenticationHelper.TryDeleteDirectory(tempHome);
        }

        var remoteSha = await RevParseAsync(path, remoteRef, cancellationToken).ConfigureAwait(false);
        if (remoteSha == null)
        {
            return new RepositoryFreshnessResult(
                RepositoryFreshnessStatus.FetchFailed, branch, localSha, null, 0, 0, null,
                $"Origin has no branch '{branch}'.");
        }

        var counts = await GitAsync(
            path, null, LocalGitTimeout, cancellationToken,
            "rev-list", "--left-right", "--count", $"HEAD...{remoteRef}").ConfigureAwait(false);
        if (!counts.Success || !TryParseCounts(counts.StdOut, out var ahead, out var behind))
        {
            return new RepositoryFreshnessResult(
                RepositoryFreshnessStatus.FetchFailed, branch, localSha, remoteSha, 0, 0, null,
                "Could not compare local branch with origin.");
        }

        if (behind == 0)
        {
            return new RepositoryFreshnessResult(
                ahead > 0 ? RepositoryFreshnessStatus.Ahead : RepositoryFreshnessStatus.UpToDate,
                branch, localSha, remoteSha, 0, ahead, null,
                ahead > 0 ? $"Local branch is {ahead} commit(s) ahead of origin." : "Base is up to date with origin.");
        }

        if (ahead > 0)
        {
            return new RepositoryFreshnessResult(
                RepositoryFreshnessStatus.Diverged, branch, localSha, remoteSha, behind, ahead, null,
                $"Local branch diverged from origin ({ahead} ahead, {behind} behind); not updated.");
        }

        // Strictly behind: fast-forward only if no tracked local modifications exist.
        var dirty = await GitAsync(path, null, LocalGitTimeout, cancellationToken, "status", "--porcelain", "--untracked-files=no").ConfigureAwait(false);
        if (!dirty.Success || !string.IsNullOrWhiteSpace(dirty.StdOut))
        {
            return new RepositoryFreshnessResult(
                RepositoryFreshnessStatus.Behind, branch, localSha, remoteSha, behind, 0, null,
                $"Base is {behind} commit(s) behind origin but local changes exist; not updated.");
        }

        var merge = await GitAsync(path, null, LocalGitTimeout, cancellationToken, "merge", "--ff-only", "--quiet", remoteRef).ConfigureAwait(false);
        if (!merge.Success)
        {
            return new RepositoryFreshnessResult(
                RepositoryFreshnessStatus.Behind, branch, localSha, remoteSha, behind, 0, null,
                $"Base is {behind} commit(s) behind origin; fast-forward was refused and nothing was changed.");
        }

        var newSha = await RevParseAsync(path, "HEAD", cancellationToken).ConfigureAwait(false) ?? remoteSha;
        return new RepositoryFreshnessResult(
            RepositoryFreshnessStatus.FastForwarded, branch, newSha, remoteSha, 0, 0, localSha,
            $"Base fast-forwarded {behind} commit(s) to match origin.");
    }

    private static RepositoryFreshnessResult NotApplicable(string message) =>
        new(RepositoryFreshnessStatus.NotApplicable, null, null, null, 0, 0, null, message);

    private static bool TryParseCounts(string output, out int ahead, out int behind)
    {
        ahead = 0;
        behind = 0;
        var parts = output.Split(new[] { '\t', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ahead) &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out behind);
    }

    private static async Task<string?> RevParseAsync(string path, string rev, CancellationToken cancellationToken)
    {
        var result = await GitAsync(path, null, LocalGitTimeout, cancellationToken, "rev-parse", "--verify", "--quiet", rev).ConfigureAwait(false);
        var sha = result.StdOut.Trim();
        return result.Success && !string.IsNullOrWhiteSpace(sha) ? sha : null;
    }

    private sealed record GitResult(bool Success, string StdOut, string StdErr);

    private static async Task<GitResult> GitAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        GitAuthenticationHelper.ApplyEnvironment(psi, null);
        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                psi.EnvironmentVariables[key] = value;
            }
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new GitResult(false, string.Empty, $"Git executable not found: {ex.Message}");
        }

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new GitResult(false, string.Empty, "Git operation timed out.");
        }

        return new GitResult(process.ExitCode == 0, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }
}
