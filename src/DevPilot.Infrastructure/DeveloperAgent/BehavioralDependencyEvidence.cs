using System.Text.RegularExpressions;
using DevPilot.Application.DeveloperAgent.Models;

namespace DevPilot.Infrastructure.DeveloperAgent;

/// <summary>
/// Bounded fresh implementation excerpts from exact strong production dependencies
/// of a test/helper/fixture. Uses only ManifestDependency, DirectLocalReference, or a
/// unique type-owner relationship. Never infers fuzzy/semantic neighbors.
/// </summary>
public static class BehavioralDependencyEvidence
{
    public const int MaxFiles = 2;
    public const int MaxExcerptChars = 1600;
    public const int MaxReasonChars = 240;
    private const int MaxFileBytes = 32_768;

    private static readonly Regex IdentifierRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled);

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "have", "been", "were",
        "was", "are", "not", "but", "error", "failed", "exception", "system",
        "public", "private", "protected", "internal", "static", "void", "class",
        "return", "using", "namespace", "new", "var", "int", "string", "bool",
        "true", "false", "null", "async", "await", "task", "test", "assert",
        "object", "void", "get", "set", "out", "ref", "in", "if", "else", "when"
    };

    public static IReadOnlyList<VerificationContractExcerpt> Collect(
        string targetPath,
        string? targetSource,
        IEnumerable<string>? candidatePaths,
        IReadOnlyDictionary<string, string>? freshSources = null,
        IReadOnlyDictionary<string, string>? originalSnapshots = null,
        string? workspacePath = null,
        string? diagnosticEvidence = null,
        IReadOnlyList<string>? manifestDependencies = null)
    {
        var excerpts = new List<VerificationContractExcerpt>();
        if (string.IsNullOrWhiteSpace(targetPath) ||
            !VerificationContractEvidence.IsVerificationFile(targetPath))
        {
            return excerpts;
        }

        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateCandidatePaths(targetPath, candidatePaths, freshSources, originalSnapshots, manifestDependencies))
        {
            if (available.ContainsKey(path) ||
                VerificationContractEvidence.IsVerificationFile(path))
            {
                continue;
            }

            if (TryResolveFreshSource(path, freshSources, originalSnapshots, workspacePath, out var source) &&
                !string.IsNullOrWhiteSpace(source))
            {
                available[path] = source;
            }
        }

        if (available.Count == 0)
        {
            return excerpts;
        }

        var fileEntry = new ManifestFileEntry(
            targetPath,
            FileEditAction.Modify,
            Dependencies: manifestDependencies);
        var strongPaths = DeveloperAgent.CollectStrongDependencyPaths(
            fileEntry,
            targetSource,
            available.Keys,
            available);
        if (strongPaths.Count == 0)
        {
            return excerpts;
        }

        var tokens = CollectOverlapTokens(diagnosticEvidence, targetSource, strongPaths);
        var ranked = new List<(string Path, string Source, int Score, int FocusLine)>();
        foreach (var path in strongPaths)
        {
            if (!available.TryGetValue(path, out var source) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var focusLine = FindBestOverlapLine(source, tokens);
            var score = ScoreSource(source, tokens, focusLine);
            if (score <= 0 && tokens.Count > 0)
            {
                focusLine = 0;
            }

            ranked.Add((path, source, score, focusLine));
        }

        foreach (var item in ranked
                     .OrderByDescending(item => item.Score)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                     .Take(MaxFiles))
        {
            var excerpt = BuildExcerpt(item.Source, item.FocusLine, tokens);
            if (string.IsNullOrWhiteSpace(excerpt))
            {
                continue;
            }

            excerpts.Add(new VerificationContractExcerpt(item.Path, excerpt));
        }

        return excerpts;
    }

    public static IReadOnlyDictionary<string, string> CollectLockedSignatures(
        IReadOnlyList<VerificationContractExcerpt> excerpts,
        IReadOnlyDictionary<string, string>? freshSources = null,
        IReadOnlyDictionary<string, string>? originalSnapshots = null,
        string? workspacePath = null)
    {
        var signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (excerpts == null)
        {
            return signatures;
        }

        foreach (var excerpt in excerpts.Take(MaxFiles))
        {
            if (!TryResolveFreshSource(excerpt.FilePath, freshSources, originalSnapshots, workspacePath, out var source) ||
                string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var contract = PlannedFileDependencyResolver.ExtractLockedContractExcerpt(excerpt.FilePath, source, 800);
            if (!string.IsNullOrWhiteSpace(contract))
            {
                signatures[excerpt.FilePath] = contract;
            }
        }

        return signatures;
    }

    public static void AppendPromptSection(
        System.Text.StringBuilder sb,
        IReadOnlyList<VerificationContractExcerpt> excerpts,
        IReadOnlyDictionary<string, string>? lockedSignatures = null)
    {
        if (sb == null || excerpts == null || excerpts.Count == 0)
        {
            return;
        }

        sb.AppendLine("=== Fresh Behavioral Dependency Evidence ===");
        sb.AppendLine("Use this only to understand the exact existing/generated runtime wiring that");
        sb.AppendLine("the repair must remain compatible with.");
        sb.AppendLine("Fix the production/test-fixture behavioral incompatibility shown by the failure. Do not merely make the file compile. Preserve the fresh dependency behavior shown below.");
        foreach (var excerpt in excerpts.Take(MaxFiles))
        {
            sb.AppendLine($"--- Behavioral Dependency: {excerpt.FilePath} ---");
            sb.AppendLine(excerpt.Excerpt);
            sb.AppendLine("--- End Behavioral Dependency ---");
        }

        sb.AppendLine();

        if (lockedSignatures == null || lockedSignatures.Count == 0)
        {
            return;
        }

        sb.AppendLine("=== Relevant Locked Signatures ===");
        foreach (var (path, signature) in lockedSignatures.Take(MaxFiles))
        {
            sb.AppendLine($"--- Locked Signature: {path} ---");
            sb.AppendLine(signature);
            sb.AppendLine("--- End Locked Signature ---");
        }

        sb.AppendLine();
    }

    public static string BoundReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "No matching change is required in the current target.";
        }

        var trimmed = reason.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= MaxReasonChars ? trimmed : trimmed[..MaxReasonChars];
    }

    private static IEnumerable<string> EnumerateCandidatePaths(
        string targetPath,
        IEnumerable<string>? candidatePaths,
        IReadOnlyDictionary<string, string>? freshSources,
        IReadOnlyDictionary<string, string>? originalSnapshots,
        IReadOnlyList<string>? manifestDependencies)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetPath };
        foreach (var path in (candidatePaths ?? Array.Empty<string>())
                     .Concat(manifestDependencies ?? Array.Empty<string>())
                     .Concat(freshSources?.Keys ?? Array.Empty<string>())
                     .Concat(originalSnapshots?.Keys ?? Array.Empty<string>()))
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
            {
                continue;
            }

            yield return path;
        }
    }

    private static bool TryResolveFreshSource(
        string path,
        IReadOnlyDictionary<string, string>? freshSources,
        IReadOnlyDictionary<string, string>? originalSnapshots,
        string? workspacePath,
        out string source)
    {
        source = string.Empty;
        if (freshSources != null &&
            freshSources.TryGetValue(path, out var fresh) &&
            !string.IsNullOrWhiteSpace(fresh))
        {
            source = BoundSource(fresh);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            try
            {
                var resolved = WorktreeEditApplier.ValidateAndResolvePath(workspacePath, path);
                var info = new FileInfo(resolved);
                if (info.Exists && info.Length > 0 && info.Length <= MaxFileBytes)
                {
                    var bytes = File.ReadAllBytes(resolved);
                    if (!WorktreeEditApplier.IsBinaryContent(bytes))
                    {
                        var disk = WorktreeEditApplier.DecodeUtf8Text(bytes, out _);
                        if (!string.IsNullOrWhiteSpace(disk))
                        {
                            source = BoundSource(disk);
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Best-effort evidence only.
            }
        }

        if (originalSnapshots != null &&
            originalSnapshots.TryGetValue(path, out var original) &&
            !string.IsNullOrWhiteSpace(original))
        {
            source = BoundSource(original);
            return true;
        }

        return false;
    }

    private static HashSet<string> CollectOverlapTokens(
        string? diagnosticEvidence,
        string? targetSource,
        IReadOnlyList<string> strongPaths)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTokens(tokens, diagnosticEvidence);
        AddTokens(tokens, targetSource);
        foreach (var path in strongPaths)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (IsUsefulToken(stem))
            {
                tokens.Add(stem);
            }
        }

        return tokens;
    }

    private static void AddTokens(HashSet<string> tokens, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match match in IdentifierRegex.Matches(text))
        {
            var value = match.Value;
            if (IsUsefulToken(value))
            {
                tokens.Add(value);
            }

            var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                var last = parts[^1];
                if (IsUsefulToken(last))
                {
                    tokens.Add(last);
                }
            }
        }
    }

    private static int FindBestOverlapLine(string source, IReadOnlyCollection<string> tokens)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var bestLine = 0;
        var bestScore = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var score = ScoreLine(lines[i], tokens);
            if (score > bestScore)
            {
                bestScore = score;
                bestLine = i;
            }
        }

        return bestLine;
    }

    private static int ScoreSource(string source, IReadOnlyCollection<string> tokens, int focusLine)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0)
        {
            return 0;
        }

        var start = Math.Max(0, focusLine - 8);
        var end = Math.Min(lines.Length - 1, focusLine + 12);
        var score = 1;
        for (var i = start; i <= end; i++)
        {
            score += ScoreLine(lines[i], tokens);
        }

        return score;
    }

    private static int ScoreLine(string line, IReadOnlyCollection<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(line) || tokens.Count == 0)
        {
            return 0;
        }

        var score = 0;
        foreach (var token in tokens)
        {
            if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score++;
            }
        }

        return score;
    }

    private static string BuildExcerpt(string source, int focusLine, IReadOnlyCollection<string> tokens)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var focus = Math.Clamp(focusLine, 0, lines.Length - 1);
        var start = focus;
        while (start > 0 && !IsSectionBoundary(lines[start]) && focus - start < 28)
        {
            start--;
        }

        if (IsSectionBoundary(lines[start]) && start < focus)
        {
            start++;
        }

        var selected = new List<string>();
        var used = 0;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var addition = selected.Count == 0 ? line : "\n" + line;
            if (used + addition.Length > MaxExcerptChars || selected.Count >= 48)
            {
                break;
            }

            selected.Add(line);
            used += addition.Length;
            if (i > focus + 10 && IsSectionBoundary(line) && selected.Count > 4)
            {
                break;
            }
        }

        if (selected.Count == 0)
        {
            return BoundSource(source);
        }

        var excerpt = string.Join('\n', selected).Trim();
        if (excerpt.Length > MaxExcerptChars)
        {
            excerpt = excerpt[..MaxExcerptChars];
        }

        if (tokens.Count > 0 && !tokens.Any(token => excerpt.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            var overlapWindow = BuildOverlapOnlyWindow(lines, tokens);
            if (!string.IsNullOrWhiteSpace(overlapWindow))
            {
                return overlapWindow;
            }
        }

        return excerpt;
    }

    private static string BuildOverlapOnlyWindow(IReadOnlyList<string> lines, IReadOnlyCollection<string> tokens)
    {
        var selected = new List<string>();
        var used = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (ScoreLine(lines[i], tokens) == 0)
            {
                continue;
            }

            var start = Math.Max(0, i - 2);
            var end = Math.Min(lines.Count - 1, i + 4);
            for (var j = start; j <= end; j++)
            {
                var line = lines[j].TrimEnd();
                var addition = selected.Count == 0 ? line : "\n" + line;
                if (used + addition.Length > MaxExcerptChars)
                {
                    return string.Join('\n', selected).Trim();
                }

                if (selected.Count == 0 || !string.Equals(selected[^1], line, StringComparison.Ordinal))
                {
                    selected.Add(line);
                    used += addition.Length;
                }
            }
        }

        var excerpt = string.Join('\n', selected).Trim();
        return excerpt.Length > MaxExcerptChars ? excerpt[..MaxExcerptChars] : excerpt;
    }

    private static bool IsSectionBoundary(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 ||
               trimmed.StartsWith("public ", StringComparison.Ordinal) ||
               trimmed.StartsWith("internal ", StringComparison.Ordinal) ||
               trimmed.StartsWith("class ", StringComparison.Ordinal) ||
               trimmed.StartsWith("export ", StringComparison.Ordinal) ||
               trimmed.StartsWith("describe(", StringComparison.Ordinal);
    }

    private static bool IsUsefulToken(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length > 2 &&
        !NoiseTokens.Contains(value);

    private static string BoundSource(string source) =>
        source.Length <= MaxFileBytes ? source : source[..MaxFileBytes];
}
