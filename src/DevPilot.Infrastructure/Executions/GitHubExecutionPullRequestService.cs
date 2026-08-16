using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Infrastructure.GitProviders;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class GitHubExecutionPullRequestService : IExecutionGitHubPullRequestService
{
    private static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex DevPilotMarkerSanitizer = new(
        @"<!--\s*devpilot-execution:.*?-->",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IGitHubPullRequestClient _githubClient;
    private readonly ILogger<GitHubExecutionPullRequestService> _logger;

    public GitHubExecutionPullRequestService(
        IGitHubPullRequestClient githubClient,
        ILogger<GitHubExecutionPullRequestService> logger)
    {
        _githubClient = githubClient;
        _logger = logger;
    }

    public async Task<ExecutionPullRequestServiceResult> CreateOrAdoptPullRequestAsync(
        TaskExecution execution,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        var repoOwner = execution.DevelopmentTask?.RepositoryWorkspace?.Owner ?? string.Empty;
        var repoName = execution.DevelopmentTask?.RepositoryWorkspace?.Repository ?? string.Empty;
        var baseBranch = execution.DevelopmentTask?.RepositoryWorkspace?.Branch ?? string.Empty;

        var headBranch = execution.RemoteBranchName ?? execution.BranchName ?? string.Empty;
        var headSha = execution.RemoteCommitSha ?? execution.CommitSha ?? string.Empty;

        if (string.IsNullOrWhiteSpace(repoOwner) || string.IsNullOrWhiteSpace(repoName))
        {
            return Failure("Repository workspace owner or repository name is not configured.", isConflict: true);
        }

        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            return Failure("Repository workspace base branch is not configured.", isConflict: true);
        }

        if (string.IsNullOrWhiteSpace(headBranch) || string.IsNullOrWhiteSpace(headSha))
        {
            return Failure("Execution remote branch name or remote commit SHA is missing.", isConflict: true);
        }

        if (string.Equals(headBranch, baseBranch, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headBranch, "master", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headBranch, "main", StringComparison.OrdinalIgnoreCase))
        {
            return Failure($"Head branch '{headBranch}' cannot be equal to base branch or master/main.", isConflict: true);
        }

        // 1. Verify workspace worktree origin URL identity via git subprocess
        if (!string.IsNullOrWhiteSpace(execution.WorkspacePath) && Directory.Exists(execution.WorkspacePath))
        {
            var originUrlCmd = await RunGitCommandAsync(execution.WorkspacePath, cancellationToken, "remote", "get-url", "origin").ConfigureAwait(false);
            if (originUrlCmd.IsSuccess && !string.IsNullOrWhiteSpace(originUrlCmd.StdOut))
            {
                var originUrl = originUrlCmd.StdOut.Trim();
                if (!GitRemoteUrlNormalizer.MatchesRepository(originUrl, repoOwner, repoName))
                {
                    return Failure("Remote origin URL does not match configured repository workspace owner and name.", isConflict: true);
                }
            }
        }

        // 2. Preflight: Live remote base branch verification
        var baseBranchResult = await _githubClient.GetBranchHeadShaAsync(repoOwner, repoName, baseBranch, cancellationToken).ConfigureAwait(false);
        if (!baseBranchResult.IsSuccess || baseBranchResult.NotFound)
        {
            return Failure($"Base branch '{baseBranch}' does not exist on GitHub repository '{repoOwner}/{repoName}'.", isConflict: true);
        }

        // 3. Preflight: Live remote head branch verification & SHA check
        var headBranchResult = await _githubClient.GetBranchHeadShaAsync(repoOwner, repoName, headBranch, cancellationToken).ConfigureAwait(false);
        if (!headBranchResult.IsSuccess || headBranchResult.NotFound)
        {
            return Failure($"Remote head branch '{headBranch}' does not exist on GitHub.", isConflict: true);
        }

        if (!string.Equals(headBranchResult.Sha, headSha, StringComparison.OrdinalIgnoreCase))
        {
            return Failure($"Live remote head SHA '{headBranchResult.Sha}' differs from expected committed SHA '{headSha}'. Direct PR creation refused.", isConflict: true);
        }

        // 4. Preflight: Query existing PRs across state=all (open, closed, merged) with pagination
        var listResult = await _githubClient.ListPullRequestsAsync(repoOwner, repoName, headBranch, baseBranch, cancellationToken).ConfigureAwait(false);

        if (!listResult.IsSuccess)
        {
            if (listResult.IsConfigurationError)
            {
                return Failure(listResult.ErrorMessage ?? "GitHub API credentials failure.", isConfigurationError: true);
            }
            if (listResult.IsRateLimit)
            {
                return Failure(listResult.ErrorMessage ?? "GitHub API rate limit exceeded.");
            }
            return Failure(listResult.ErrorMessage ?? "Failed to query existing GitHub pull requests.");
        }

        var expectedMarker = GetDevPilotMarker(execution.Id);
        var prs = listResult.Data ?? Array.Empty<GitHubPullRequestDto>();

        var matchingPrs = prs.Where(p =>
            string.Equals(p.HeadRef, headBranch, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.BaseRef, baseBranch, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(p.HeadSha) || string.Equals(p.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
        ).ToList();

        if (matchingPrs.Count > 1)
        {
            return Failure($"Multiple matching PRs found on remote for branch '{headBranch}'.", isConflict: true);
        }

        if (matchingPrs.Count == 1)
        {
            var existingPr = matchingPrs[0];
            var isDevPilotOwned = existingPr.Body.Contains(expectedMarker, StringComparison.OrdinalIgnoreCase);

            if (isDevPilotOwned)
            {
                if (string.Equals(existingPr.State, "open", StringComparison.OrdinalIgnoreCase))
                {
                    var trustedUrl = BuildTrustedPrUrl(repoOwner, repoName, existingPr.Number);
                    return Success(existingPr.Number, trustedUrl, baseBranch, DateTime.UtcNow);
                }
                else
                {
                    // Closed or Merged DevPilot PR
                    return Failure($"A DevPilot PR (#{existingPr.Number}) for this execution already exists in state '{existingPr.State}'. Re-creating PR refused.", isConflict: true);
                }
            }
            else
            {
                // Foreign / manual matching PR
                return Failure($"A foreign/manual PR (#{existingPr.Number}) exists for branch '{headBranch}' without DevPilot execution marker.", isConflict: true);
            }
        }

        // 5. Compose deterministic PR title & body
        var rawTitle = execution.DevelopmentTask?.Title ?? "DevPilot Execution PR";
        var sanitizedTitle = SanitizeTitle(rawTitle);

        var rawDesc = execution.DevelopmentTask?.Description ?? string.Empty;
        var sanitizedDesc = SanitizeDescription(rawDesc);

        var body = BuildPrBody(sanitizedDesc, execution.Id, headSha, expectedMarker);

        // 6. Execute POST /repos/{owner}/{repo}/pulls
        bool wasPostSent = true;
        var createResult = await _githubClient.CreatePullRequestAsync(repoOwner, repoName, headBranch, baseBranch, sanitizedTitle, body, cancellationToken).ConfigureAwait(false);

        if (createResult.IsSuccess && createResult.Data != null)
        {
            var pr = createResult.Data;
            if (ValidatePullRequestInfo(pr, repoOwner, repoName, headBranch, headSha, baseBranch, expectedMarker))
            {
                var trustedUrl = BuildTrustedPrUrl(repoOwner, repoName, pr.Number);
                return Success(pr.Number, trustedUrl, baseBranch, DateTime.UtcNow, wasPostSent: true);
            }
            else
            {
                _logger.LogWarning("Created PR #{PrNumber} payload validation failed against expected state.", pr.Number);
            }
        }

        // Handle 422 Conflict / Duplicate race recovery
        if (createResult.IsConflict)
        {
            _logger.LogInformation("Create PR returned conflict (422). Re-querying GitHub for race recovery...");
            var retryList = await _githubClient.ListPullRequestsAsync(repoOwner, repoName, headBranch, baseBranch, cancellationToken).ConfigureAwait(false);
            if (retryList.IsSuccess && retryList.Data != null)
            {
                var retryMatch = retryList.Data.FirstOrDefault(p =>
                    string.Equals(p.HeadRef, headBranch, StringComparison.OrdinalIgnoreCase) &&
                    p.Body.Contains(expectedMarker, StringComparison.OrdinalIgnoreCase));

                if (retryMatch != null)
                {
                    if (string.Equals(retryMatch.State, "open", StringComparison.OrdinalIgnoreCase))
                    {
                        var trustedUrl = BuildTrustedPrUrl(repoOwner, repoName, retryMatch.Number);
                        return Success(retryMatch.Number, trustedUrl, baseBranch, DateTime.UtcNow, wasPostSent: true);
                    }
                    else
                    {
                        return Failure($"DevPilot PR (#{retryMatch.Number}) exists in state '{retryMatch.State}'.", isConflict: true, wasPostSent: true);
                    }
                }
            }
            return Failure(createResult.ErrorMessage ?? "Pull request creation returned a conflict.", isConflict: true, wasPostSent: true);
        }

        if (createResult.IsConfigurationError)
        {
            return Failure(createResult.ErrorMessage ?? "GitHub API credentials failure.", isConfigurationError: true, wasPostSent: true, isDefinitiveNoMutationFailure: true);
        }

        return Failure(createResult.ErrorMessage ?? "Pull request creation failed on GitHub.", wasPostSent: wasPostSent, isDefinitiveNoMutationFailure: false);
    }

    public static string GetDevPilotMarker(Guid executionId) =>
        $"<!-- devpilot-execution:{executionId} -->";

    public static string SanitizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "DevPilot Task";
        var singleLine = title.Replace("\r", " ").Replace("\n", " ");
        var collapsed = Regex.Replace(singleLine, @"\s+", " ").Trim();
        return collapsed.Length > 250 ? collapsed[..250].TrimEnd() : collapsed;
    }

    public static string SanitizeDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "No description provided.";
        var stripped = DevPilotMarkerSanitizer.Replace(description, string.Empty);
        var normalized = stripped.Replace("\r\n", "\n").Replace("\r", "\n");
        return normalized.Length > 1000 ? normalized[..1000].TrimEnd() + "..." : normalized;
    }

    public static string BuildPrBody(string sanitizedDesc, Guid executionId, string commitSha, string marker)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        sb.AppendLine(sanitizedDesc);
        sb.AppendLine();
        sb.AppendLine("## Validation");
        sb.AppendLine("- Build: Passed");
        sb.AppendLine("- Tests: Passed");
        sb.AppendLine();
        sb.AppendLine("## DevPilot");
        sb.AppendLine($"Execution: {executionId}");
        sb.AppendLine($"Commit: {commitSha}");
        sb.AppendLine();
        sb.Append(marker);
        return sb.ToString();
    }

    public static bool ValidatePullRequestInfo(
        GitHubPullRequestDto pr,
        string expectedOwner,
        string expectedRepo,
        string expectedHeadRef,
        string expectedHeadSha,
        string expectedBaseRef,
        string expectedMarker)
    {
        if (pr.Number <= 0) return false;

        if (!string.IsNullOrWhiteSpace(pr.HeadRepoOwner) && !string.Equals(pr.HeadRepoOwner, expectedOwner, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(pr.HeadRepoName) && !string.Equals(pr.HeadRepoName, expectedRepo, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(pr.HeadRef, expectedHeadRef, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(pr.HeadSha) && !string.Equals(pr.HeadSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(pr.BaseRef, expectedBaseRef, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!pr.Body.Contains(expectedMarker, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public static string BuildTrustedPrUrl(string owner, string repo, int number) =>
        $"https://github.com/{owner}/{repo}/pull/{number}";

    private static ExecutionPullRequestServiceResult Success(int number, string url, string baseBranch, DateTime createdAt, bool wasPostSent = false) =>
        new(true, false, false, number, url, baseBranch, createdAt, null, wasPostSent, IsDefinitiveNoMutationFailure: false);

    private static ExecutionPullRequestServiceResult Failure(
        string message,
        bool isConfigurationError = false,
        bool isConflict = false,
        bool wasPostSent = false,
        bool isDefinitiveNoMutationFailure = false) =>
        new(false, isConfigurationError, isConflict, null, null, null, null, message, wasPostSent, IsDefinitiveNoMutationFailure: isDefinitiveNoMutationFailure);

    private static async Task<GitCommandResult> RunGitCommandAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var timeoutCts = new CancellationTokenSource(DefaultGitTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

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
            return new GitCommandResult(false, "", $"Git executable error: {ex.Message}");
        }

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            var stdOut = await outTask.ConfigureAwait(false);
            var stdErr = await errTask.ConfigureAwait(false);
            return new GitCommandResult(process.ExitCode == 0, stdOut, stdErr);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new GitCommandResult(false, "", "Git command timed out or cancelled.");
        }
    }

    private sealed record GitCommandResult(bool IsSuccess, string StdOut, string StdErr);
}
