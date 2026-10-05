using System.Text.RegularExpressions;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Automation;

public sealed record AutomationGateResult(bool Allowed, IReadOnlyList<string> Reasons)
{
    public static AutomationGateResult Pass { get; } = new(true, Array.Empty<string>());

    public string Summary => string.Join(" ", Reasons);
}

public enum AutomationMergeReadiness
{
    Ready,

    /// <summary>Nothing is wrong yet; CI is still running.</summary>
    Waiting,

    /// <summary>A person has to look at it.</summary>
    Blocked
}

public sealed record AutomationMergeDecision(AutomationMergeReadiness Readiness, string Reason);

/// <summary>
/// The safety rules automation must satisfy before it acts. Pure: everything it needs is passed in, so every rule
/// is covered by a unit test and a decision can always be explained to the person who owns the repository.
/// </summary>
public static class AutomationGate
{
    /// <summary>Can this finished execution be approved and delivered without a person looking at it?</summary>
    public static AutomationGateResult EvaluateDelivery(
        AutomationPolicy policy,
        ExecutionVerificationOutcome outcome,
        ExecutionVerdictDto? verdict,
        IReadOnlyList<ExecutionReviewFileDto> changedFiles,
        bool hasSensitiveFiles,
        bool visualReviewRequired)
    {
        var reasons = new List<string>();

        if (outcome is not (ExecutionVerificationOutcome.Verified or ExecutionVerificationOutcome.NoNewRegressions))
        {
            reasons.Add($"Verification outcome is '{outcome}'; only fully verified changes are delivered automatically.");
        }

        if (verdict is { TestWeakeningSuspected: true })
        {
            reasons.Add("Existing tests may have been weakened to make the change pass.");
        }

        if (verdict is { StaleBase: true })
        {
            reasons.Add("The base branch moved on while the change was being made.");
        }

        if (hasSensitiveFiles)
        {
            reasons.Add("The change contains sensitive files.");
        }

        if (visualReviewRequired)
        {
            reasons.Add("The change alters the UI and needs a person to look at it.");
        }

        if (changedFiles.Count == 0)
        {
            reasons.Add("The change has no changed files.");
        }

        if (changedFiles.Count > policy.MaxFilesChanged)
        {
            reasons.Add($"The change touches {changedFiles.Count} files; the limit is {policy.MaxFilesChanged}.");
        }

        var lines = changedFiles.Sum(file => (long)(file.Additions ?? 0) + (file.Deletions ?? 0));
        if (lines > policy.MaxLinesChanged)
        {
            reasons.Add($"The change touches {lines} lines; the limit is {policy.MaxLinesChanged}.");
        }

        var protectedHits = FindProtectedPaths(policy.ProtectedPaths, changedFiles.Select(file => file.Path));
        if (protectedHits.Count > 0)
        {
            reasons.Add($"The change touches protected paths: {string.Join(", ", protectedHits.Take(3))}.");
        }

        return reasons.Count == 0 ? AutomationGateResult.Pass : new AutomationGateResult(false, reasons);
    }

    /// <summary>May the open pull request be merged now?</summary>
    public static AutomationMergeDecision EvaluateMerge(
        AutomationPolicy policy,
        ExecutionPullRequestRemoteState remoteState,
        ExecutionPullRequestIntegrityStatus integrity,
        ExecutionCiStatus ci)
    {
        if (remoteState != ExecutionPullRequestRemoteState.Open)
        {
            return new(AutomationMergeReadiness.Blocked, $"The pull request is '{remoteState}' on GitHub.");
        }

        if (integrity != ExecutionPullRequestIntegrityStatus.Valid)
        {
            return new(AutomationMergeReadiness.Blocked,
                $"The pull request no longer matches the approved change ({integrity}).");
        }

        return ci switch
        {
            ExecutionCiStatus.Success => new(AutomationMergeReadiness.Ready, "CI checks passed."),
            ExecutionCiStatus.Pending or ExecutionCiStatus.Unknown =>
                new(AutomationMergeReadiness.Waiting, "Waiting for CI checks."),
            ExecutionCiStatus.Failure => new(AutomationMergeReadiness.Blocked, "CI checks failed."),
            ExecutionCiStatus.NoChecks or ExecutionCiStatus.Neutral when policy.RequireGreenCiForMerge =>
                new(AutomationMergeReadiness.Blocked,
                    "The repository reports no passing CI checks and the policy requires them."),
            _ => new(AutomationMergeReadiness.Ready, "No CI checks are required by the policy.")
        };
    }

    public static IReadOnlyList<string> FindProtectedPaths(string? patterns, IEnumerable<string> paths)
    {
        var matchers = ParsePatterns(patterns);
        if (matchers.Count == 0)
        {
            return Array.Empty<string>();
        }

        return paths
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .Where(path => matchers.Any(matcher => matcher.IsMatch(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<Regex> ParsePatterns(string? patterns) =>
        (patterns ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pattern => !pattern.StartsWith('#'))
            .Select(GlobToRegex)
            .ToList();

    /// <summary>'**' crosses directories, '*' stays within one path segment; matching ignores case.</summary>
    private static Regex GlobToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/').TrimStart('/');
        var pattern = new System.Text.StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
            {
                var followedBySlash = i + 2 < normalized.Length && normalized[i + 2] == '/';
                pattern.Append(followedBySlash ? "(?:.*/)?" : ".*");
                i += followedBySlash ? 2 : 1;
            }
            else if (c == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
