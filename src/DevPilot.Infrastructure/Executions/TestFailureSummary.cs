using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DevPilot.Infrastructure.Executions;

public sealed record TestFailureGroup(string Reason, IReadOnlyList<string> TestNames);

/// <summary>
/// Every failing test of one verification run, grouped by the reason it failed. Repair works from this instead of the
/// first failure only, so a root cause shared by many tests is fixed in one round.
/// </summary>
public sealed record TestFailureSummary(int FailureCount, IReadOnlyList<TestFailureGroup> Groups)
{
    private const int MaxNamesPerGroup = 6;
    private const int MaxGroups = 8;

    public bool HasSharedCause => Groups.Any(group => group.TestNames.Count > 1);

    /// <summary>Stable across reruns when the same tests fail for the same reasons; changes when any failure is fixed.</summary>
    public string Fingerprint
    {
        get
        {
            var identity = string.Join(
                "\n",
                Groups
                    .OrderBy(group => group.Reason, StringComparer.Ordinal)
                    .Select(group => $"{group.Reason}|{group.TestNames.Count}"));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{FailureCount}\n{identity}"));
            return Convert.ToHexString(hash)[..16];
        }
    }

    /// <summary>One short line per cause group for the verdict, largest group first.</summary>
    public IReadOnlyList<string> ToDisplayLines() =>
        Groups
            .OrderByDescending(group => group.TestNames.Count)
            .Take(MaxGroups)
            .Select(group => $"{group.TestNames.Count} × {group.Reason}")
            .ToList();

    /// <summary>
    /// Feedback text a reviewer could send with "request changes" to have the AI fix exactly these failures.
    /// Bounded below the request-changes limit.
    /// </summary>
    public string ToSuggestedFix(IReadOnlyList<string>? sourceHints = null, int maxChars = 1800)
    {
        const string intro =
            "Fix the failing tests so they match how the application behaves now. Update the test expectations; " +
            "do not delete tests, skip them, or weaken their assertions.\n\n";
        var text = intro + ToPromptText(sourceHints);
        return text.Length <= maxChars ? text : text[..(maxChars - 1)].TrimEnd() + "…";
    }

    public string ToPromptText(IReadOnlyList<string>? sourceHints = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{FailureCount} test(s) failed, in {Groups.Count} distinct cause group(s):");
        var index = 1;
        foreach (var group in Groups.OrderByDescending(g => g.TestNames.Count).Take(MaxGroups))
        {
            sb.AppendLine($"{index++}. [{group.TestNames.Count} test(s)] {group.Reason}");
            foreach (var name in group.TestNames.Take(MaxNamesPerGroup))
            {
                sb.AppendLine($"     - {name}");
            }

            if (group.TestNames.Count > MaxNamesPerGroup)
            {
                sb.AppendLine($"     - ... and {group.TestNames.Count - MaxNamesPerGroup} more");
            }
        }

        if (sourceHints is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("What the application source now says (use it as the source of truth for the new expectations):");
            foreach (var hint in sourceHints)
            {
                sb.AppendLine($"- {hint}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

public static class TestFailureSummarizer
{
    private const int MaxReasonChars = 220;
    private const int MaxHintTexts = 6;
    private const int MaxScannedFiles = 500;
    private const long MaxScannedFileBytes = 300 * 1024;

    private static readonly Regex AnsiRegex = new(@"\x1B?\[[0-9;]*m", RegexOptions.Compiled);

    private static readonly Regex JsFailBlockRegex = new(
        @"^\s*FAIL\s+(?<file>\S+\.(?:test|spec)\.[cm]?[jt]sx?)\s*>\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex JsFailedListRegex = new(
        @"^\s*[×✗✕]\s+(?<name>.+?)(?:\s+\d+(?:\.\d+)?m?s)?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex JsReasonArrowRegex = new(@"^\s*→\s*(?<reason>.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex MissingTextRegex = new(
        @"(?:with the (?:text|text content|label text|placeholder text|display value)(?: of)?|and name|with the name)\s*:?\s*[""'`]?(?<text>[^""'`]+?)[""'`]?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] SourceExtensions = { ".ts", ".tsx", ".js", ".jsx", ".vue", ".svelte", ".html" };
    private static readonly string[] SkippedDirectories = { "node_modules", "dist", "build", ".git", "bin", "obj", "coverage" };

    public static TestFailureSummary? Summarize(string? stdOut, string? stdErr, string? errorMessage)
    {
        var text = AnsiRegex.Replace(string.Join("\n", new[] { stdOut, stdErr, errorMessage }.Where(s => !string.IsNullOrWhiteSpace(s))), string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var failures = ParseJsFailBlocks(lines);
        if (failures.Count == 0)
        {
            failures = ParseJsFailedList(lines);
        }

        if (failures.Count == 0)
        {
            failures = ExecutionDiagnosticEvidence.ParseAllTestFailures(stdOut, stdErr, errorMessage)
                .Where(item => !string.IsNullOrWhiteSpace(item.TestName))
                .Select(item => (Name: item.TestName!, Reason: Trim(item.ErrorSummary)))
                .ToList();
        }

        if (failures.Count == 0)
        {
            return null;
        }

        var groups = failures
            .GroupBy(failure => failure.Reason, StringComparer.Ordinal)
            .Select(group => new TestFailureGroup(
                group.Key,
                group.Select(failure => failure.Name).Distinct(StringComparer.Ordinal).ToList()))
            .ToList();

        return new TestFailureSummary(groups.Sum(group => group.TestNames.Count), groups);
    }

    /// <summary>
    /// For "unable to find the text X" failures, looks for X (ignoring trailing punctuation and case) in the
    /// non-test source, so the repair knows what the application actually renders now.
    /// </summary>
    public static IReadOnlyList<string> FindSourceHints(string? workspacePath, TestFailureSummary summary)
    {
        var hints = new List<string>();
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return hints;
        }

        var wanted = summary.Groups
            .Select(group => ExtractMissingText(group.Reason))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxHintTexts)
            .ToList();
        if (wanted.Count == 0)
        {
            return hints;
        }

        var root = Directory.Exists(Path.Combine(workspacePath, "src")) ? Path.Combine(workspacePath, "src") : workspacePath;
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var file in EnumerateSourceFiles(root))
        {
            if (++scanned > MaxScannedFiles || found.Count == wanted.Count)
            {
                break;
            }

            string[] fileLines;
            try
            {
                if (new FileInfo(file).Length > MaxScannedFileBytes)
                {
                    continue;
                }

                fileLines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var text in wanted)
            {
                if (found.ContainsKey(text))
                {
                    continue;
                }

                var loose = text.TrimEnd('.', ':', '!', '?', '…', ' ');
                if (loose.Length < 3)
                {
                    continue;
                }

                for (var i = 0; i < fileLines.Length; i++)
                {
                    if (fileLines[i].Contains(text, StringComparison.Ordinal))
                    {
                        // The exact text exists, so the missing text is not a renamed string.
                        found[text] = string.Empty;
                        break;
                    }

                    if (fileLines[i].Contains(loose, StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = Path.GetRelativePath(workspacePath, file).Replace('\\', '/');
                        found[text] = $"tests look for \"{text}\" but {relative}:{i + 1} renders \"{loose}\" (changed wording/punctuation)";
                        break;
                    }
                }
            }
        }

        hints.AddRange(found.Values.Where(hint => hint.Length > 0));
        return hints;
    }

    internal static string? ExtractMissingText(string reason)
    {
        var cut = reason.IndexOf(". This could be because", StringComparison.Ordinal);
        var head = (cut >= 0 ? reason[..cut] : reason).Trim();
        var match = MissingTextRegex.Match(head);
        return match.Success ? match.Groups["text"].Value.Trim() : null;
    }

    private static List<(string Name, string Reason)> ParseJsFailBlocks(string[] lines)
    {
        var failures = new List<(string Name, string Reason)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var match = JsFailBlockRegex.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var reason = "test failed";
            for (var j = i + 1; j < lines.Length && j <= i + 4; j++)
            {
                var candidate = lines[j].Trim();
                if (candidate.Length == 0 || candidate.StartsWith('⎯'))
                {
                    continue;
                }

                reason = Trim(candidate);
                break;
            }

            failures.Add(($"{match.Groups["file"].Value} > {match.Groups["name"].Value}", reason));
        }

        return failures;
    }

    private static List<(string Name, string Reason)> ParseJsFailedList(string[] lines)
    {
        var failures = new List<(string Name, string Reason)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var match = JsFailedListRegex.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var reason = "test failed";
            if (i + 1 < lines.Length)
            {
                var arrow = JsReasonArrowRegex.Match(lines[i + 1]);
                if (arrow.Success)
                {
                    reason = Trim(arrow.Groups["reason"].Value);
                }
            }

            failures.Add((match.Groups["name"].Value, reason));
        }

        return failures;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (Directory.Exists(child))
                {
                    if (!SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                        !name.Equals("__tests__", StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Push(child);
                    }

                    continue;
                }

                if (SourceExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) &&
                    !name.Contains(".test.", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains(".spec.", StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                }
            }
        }
    }

    private static string Trim(string value)
    {
        var cut = value.IndexOf(". This could be because", StringComparison.Ordinal);
        var text = (cut >= 0 ? value[..cut] : value).Trim();
        return text.Length > MaxReasonChars ? text[..MaxReasonChars] + "…" : text;
    }
}
