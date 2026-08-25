using System.Text.RegularExpressions;
using DevPilot.Application.DeveloperAgent.Models;

namespace DevPilot.Infrastructure.DeveloperAgent;

/// <summary>
/// Bounded, deterministic verification-contract excerpts from existing tests/helpers/fixtures
/// that directly reference a planned Modify target. Never adds those files to the manifest.
/// </summary>
public static class VerificationContractEvidence
{
    public const int MaxFiles = 2;
    public const int MaxExcerptChars = 1200;
    private const int MaxCandidatesToInspect = 48;
    private const int MaxFileBytes = 16_384;

    private static readonly string[] HelperNameTokens = { "factory", "fixture", "helper", "startup", "webapplication" };

    public static IReadOnlyList<VerificationContractExcerpt> Collect(
        string targetPath,
        string? targetSource,
        string? workspacePath,
        IReadOnlyDictionary<string, string>? alreadyLoaded = null,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph = null)
    {
        var excerpts = new List<VerificationContractExcerpt>();
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return excerpts;
        }

        var candidates = CollectCandidateSources(targetPath, workspacePath, alreadyLoaded, projectGraph);
        if (candidates.Count == 0)
        {
            return excerpts;
        }

        var productionSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [targetPath] = targetSource ?? string.Empty
        };
        foreach (var (path, source) in alreadyLoaded ?? new Dictionary<string, string>())
        {
            if (!IsVerificationFile(path))
            {
                productionSources.TryAdd(path, source);
            }
        }

        var ranked = new List<(string Path, string Source, int Score, int ReferenceLine)>();
        foreach (var (path, source) in candidates.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryFindDirectReference(path, source, targetPath, targetSource, productionSources, out var score, out var line))
            {
                continue;
            }

            ranked.Add((path, source, score, line));
        }

        foreach (var item in ranked
                     .OrderByDescending(item => item.Score)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                     .Take(MaxFiles))
        {
            var excerpt = BuildExcerpt(item.Source, item.ReferenceLine, targetPath);
            if (string.IsNullOrWhiteSpace(excerpt))
            {
                continue;
            }

            excerpts.Add(new VerificationContractExcerpt(item.Path, excerpt));
        }

        return excerpts;
    }

    public static void AppendPromptSection(System.Text.StringBuilder sb, IReadOnlyList<VerificationContractExcerpt> excerpts)
    {
        if (sb == null || excerpts == null || excerpts.Count == 0)
        {
            return;
        }

        sb.AppendLine("=== Existing Verification Contract Evidence ===");
        sb.AppendLine("These existing tests/fixtures describe behavior that should remain compatible");
        sb.AppendLine("unless the task explicitly changes that behavior.");
        foreach (var excerpt in excerpts)
        {
            sb.AppendLine($"--- Verification Contract: {excerpt.FilePath} ---");
            sb.AppendLine(excerpt.Excerpt);
            sb.AppendLine("--- End Verification Contract ---");
        }

        sb.AppendLine();
    }

    internal static bool IsVerificationFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (ProjectGraphHelper.IsTestFileCandidate(path))
        {
            return true;
        }

        var fileName = Path.GetFileName(path);
        return HelperNameTokens.Any(token => fileName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> CollectCandidateSources(
        string targetPath,
        string? workspacePath,
        IReadOnlyDictionary<string, string>? alreadyLoaded,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph)
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (alreadyLoaded != null)
        {
            foreach (var (path, source) in alreadyLoaded)
            {
                if (!string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase) &&
                    IsVerificationFile(path) &&
                    !string.IsNullOrWhiteSpace(source))
                {
                    candidates[path] = source.Length > MaxFileBytes ? source[..MaxFileBytes] : source;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath) ||
            candidates.Count >= MaxCandidatesToInspect)
        {
            return candidates;
        }

        foreach (var relative in EnumerateVerificationCandidatePaths(workspacePath, projectGraph))
        {
            if (candidates.Count >= MaxCandidatesToInspect ||
                string.Equals(relative, targetPath, StringComparison.OrdinalIgnoreCase) ||
                candidates.ContainsKey(relative))
            {
                continue;
            }

            try
            {
                var resolved = WorktreeEditApplier.ValidateAndResolvePath(workspacePath, relative);
                var info = new FileInfo(resolved);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes)
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(resolved);
                if (WorktreeEditApplier.IsBinaryContent(bytes))
                {
                    continue;
                }

                var content = WorktreeEditApplier.DecodeUtf8Text(bytes, out _);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    candidates[relative] = content;
                }
            }
            catch
            {
                // Best-effort evidence only.
            }
        }

        return candidates;
    }

    private static IEnumerable<string> EnumerateVerificationCandidatePaths(
        string workspacePath,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();

        if (projectGraph != null)
        {
            foreach (var project in projectGraph.Where(node => node.IsTestProject))
            {
                if (!string.IsNullOrWhiteSpace(project.ProjectDirectory))
                {
                    roots.Add(project.ProjectDirectory);
                }
            }
        }

        foreach (var fallback in new[] { "tests", "test", "Tests", "Test" })
        {
            roots.Add(fallback);
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string resolved;
            try
            {
                resolved = WorktreeEditApplier.ValidateAndResolvePath(workspacePath, root);
            }
            catch
            {
                continue;
            }

            if (!Directory.Exists(resolved))
            {
                continue;
            }

            foreach (var pattern in new[] { "*.cs", "*.ts", "*.tsx", "*.js", "*.jsx", "*.py" })
            {
                foreach (var fullPath in ProjectGraphHelper.SafeFindFiles(resolved, pattern)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                             .Take(MaxCandidatesToInspect))
                {
                    string relative;
                    try
                    {
                        relative = DeveloperAgent.NormalizeFocusedRepairPath(Path.GetRelativePath(workspacePath, fullPath));
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(relative) ||
                        !IsVerificationFile(relative) ||
                        !seen.Add(relative))
                    {
                        continue;
                    }

                    yield return relative;
                }
            }
        }
    }

    private static bool TryFindDirectReference(
        string candidatePath,
        string candidateSource,
        string targetPath,
        string? targetSource,
        IReadOnlyDictionary<string, string> productionSources,
        out int score,
        out int referenceLine)
    {
        score = 0;
        referenceLine = 0;
        if (string.IsNullOrWhiteSpace(candidateSource))
        {
            return false;
        }

        if (PlannedFileDependencyResolver.HasDirectLocalReference(
                candidatePath,
                candidateSource,
                targetPath,
                new[] { targetPath }))
        {
            score += 4;
            referenceLine = FindReferenceLine(candidateSource, Path.GetFileNameWithoutExtension(targetPath), targetPath);
        }

        var typeNames = PlannedFileDependencyResolver.ExtractDeclaredTypeNames(targetPath, targetSource).ToList();
        var stem = Path.GetFileNameWithoutExtension(targetPath);
        if (IsSignificantIdentifier(stem) &&
            targetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            !typeNames.Contains(stem, StringComparer.Ordinal))
        {
            typeNames.Add(stem);
        }

        foreach (var typeName in typeNames)
        {
            var owner = PlannedFileDependencyResolver.FindUniqueDeclarationOwner(typeName, productionSources)
                        ?? (string.Equals(stem, typeName, StringComparison.Ordinal) &&
                            productionSources.Keys.Count(path =>
                                string.Equals(Path.GetFileNameWithoutExtension(path), typeName, StringComparison.OrdinalIgnoreCase)) == 1
                            ? targetPath
                            : null);
            if (!string.Equals(owner, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ContainsIdentifier(candidateSource, typeName))
            {
                continue;
            }

            score += LooksLikeFixtureOrSetup(candidatePath, candidateSource) ? 3 : 2;
            if (referenceLine == 0)
            {
                referenceLine = FindReferenceLine(candidateSource, typeName, targetPath);
            }
        }

        return score > 0;
    }

    private static bool LooksLikeFixtureOrSetup(string path, string source)
    {
        var fileName = Path.GetFileName(path);
        if (HelperNameTokens.Any(token => fileName.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return source.Contains("WebApplicationFactory", StringComparison.Ordinal) ||
               source.Contains("IClassFixture", StringComparison.Ordinal) ||
               source.Contains("ConfigureWebHost", StringComparison.Ordinal) ||
               source.Contains("CreateClient", StringComparison.Ordinal) ||
               source.Contains("setUp", StringComparison.Ordinal) ||
               source.Contains("beforeEach", StringComparison.Ordinal);
    }

    private static string BuildExcerpt(string source, int referenceLine, string targetPath)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var focus = Math.Clamp(referenceLine, 0, lines.Length - 1);
        var start = focus;
        while (start > 0 && !IsSectionBoundary(lines[start]) && focus - start < 24)
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
            if (used + addition.Length > MaxExcerptChars || selected.Count >= 40)
            {
                break;
            }

            selected.Add(line);
            used += addition.Length;
            if (i > focus + 8 && IsSectionBoundary(line) && selected.Count > 4)
            {
                break;
            }
        }

        var excerpt = string.Join('\n', selected).Trim();
        return excerpt.Length > MaxExcerptChars ? excerpt[..MaxExcerptChars] : excerpt;
    }

    private static int FindReferenceLine(string source, string token, string targetPath)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var fileName = Path.GetFileName(targetPath);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if ((!string.IsNullOrWhiteSpace(token) && ContainsIdentifier(line, token)) ||
                (!string.IsNullOrWhiteSpace(fileName) && line.Contains(fileName, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return 0;
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

    private static bool IsSignificantIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length > 2 && Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");

    private static bool ContainsIdentifier(string text, string identifier)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        return Regex.IsMatch(
            text,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
