using System.Text;
using System.Text.RegularExpressions;

namespace DevPilot.Infrastructure.DeveloperAgent;

public sealed record SameRoleExemplar(string FilePath, string BoundedExcerpt);

/// <summary>
/// Selects a small same-role repository exemplar from already-available workspace
/// evidence. Discovery is deterministic filesystem/context lookup — never an extra
/// provider call, and never a multi-directory dependency scan.
/// </summary>
public static class RepositoryGenerationExemplar
{
    public const int MaxExemplarChars = 1200;
    public const int MaxExemplarLines = 24;
    private const int MaxSameDirectoryFiles = 16;
    private const int MaxSameDirectoryFileBytes = 16_384;

    public static SameRoleExemplar? SelectSameRoleExemplar(
        string targetPath,
        IReadOnlyDictionary<string, string>? repositoryContents)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || repositoryContents == null || repositoryContents.Count == 0)
        {
            return null;
        }

        var targetExt = Path.GetExtension(targetPath);
        var targetDir = ParentDirectory(targetPath);
        var targetDirName = DirectoryName(targetDir);
        var targetStem = FeatureStem(targetPath);
        var targetRemainder = RoleRemainder(targetPath);
        SameRoleExemplar? selected = null;
        var selectedScore = 0;
        string? selectedPath = null;

        foreach (var (path, content) in repositoryContents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(content) ||
                !string.Equals(Path.GetExtension(path), targetExt, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidateStem = FeatureStem(path);
            if (IsSignificantStem(targetStem) &&
                string.Equals(candidateStem, targetStem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = 0;
            var candidateDir = ParentDirectory(path);
            if (string.Equals(candidateDir, targetDir, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }
            else if (!string.IsNullOrWhiteSpace(targetDirName) &&
                     string.Equals(DirectoryName(candidateDir), targetDirName, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (!string.IsNullOrWhiteSpace(targetRemainder) &&
                string.Equals(RoleRemainder(path), targetRemainder, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (score < 3)
            {
                continue;
            }

            if (selected == null ||
                score > selectedScore ||
                (score == selectedScore && string.Compare(path, selectedPath, StringComparison.OrdinalIgnoreCase) < 0))
            {
                var excerpt = BoundStructuralExemplar(content, MaxExemplarChars);
                if (string.IsNullOrWhiteSpace(excerpt))
                {
                    continue;
                }

                selected = new SameRoleExemplar(path, excerpt);
                selectedScore = score;
                selectedPath = path;
            }
        }

        return selected;
    }

    public static string? BuildRepositorySizeHint(
        string targetPath,
        IReadOnlyDictionary<string, string>? repositoryContents)
    {
        var exemplar = SelectSameRoleExemplar(targetPath, repositoryContents);
        if (exemplar == null)
        {
            return null;
        }

        var lineCount = EstimateSourceLineCount(
            repositoryContents != null &&
            repositoryContents.TryGetValue(exemplar.FilePath, out var fullContent) &&
            !string.IsNullOrWhiteSpace(fullContent)
                ? fullContent
                : exemplar.BoundedExcerpt);

        if (lineCount <= 0)
        {
            return null;
        }

        return $"The closest repository exemplar is approximately {lineCount} source lines. Stay close to that size unless additional lines are required for correctness.";
    }

    public static int EstimateSourceLineCount(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return 0;
        }

        return content
            .Replace("\r\n", "\n")
            .Split('\n')
            .Count(line => !string.IsNullOrWhiteSpace(line));
    }

    public static string BoundStructuralExemplar(string content, int maxChars = MaxExemplarChars)
    {
        if (string.IsNullOrWhiteSpace(content) || maxChars <= 0)
        {
            return string.Empty;
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var selected = new List<string>();
        var used = 0;
        var followOn = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var structural = LooksLikeStructuralExemplarLine(line);
            if (!structural && followOn <= 0)
            {
                continue;
            }

            var addition = selected.Count == 0 ? line.Trim() : "\n" + line.Trim();
            if (used + addition.Length > maxChars || selected.Count >= MaxExemplarLines)
            {
                break;
            }

            selected.Add(line.Trim());
            used += addition.Length;
            followOn = structural ? 2 : followOn - 1;
        }

        if (selected.Count == 0)
        {
            return DeveloperAgent.BoundPeerContractExcerpt(content, Math.Min(maxChars, 400));
        }

        return string.Join('\n', selected);
    }

    public static IReadOnlyDictionary<string, string> CollectAvailableRepositoryContents(
        string targetPath,
        string? workspacePath,
        IReadOnlyDictionary<string, string>? alreadyLoaded)
    {
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (alreadyLoaded != null)
        {
            foreach (var (path, content) in alreadyLoaded)
            {
                if (!string.IsNullOrWhiteSpace(content) &&
                    !string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    contents[path] = content;
                }
            }
        }

        CollectSameDirectorySiblings(targetPath, workspacePath, contents);
        return contents;
    }

    internal static string FeatureStem(string filePath)
    {
        var tokens = TokenizeFileName(filePath);
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        if (tokens.Count == 1)
        {
            return tokens[0];
        }

        return string.Concat(tokens.Take(tokens.Count - 1));
    }

    internal static string RoleRemainder(string filePath)
    {
        var tokens = TokenizeFileName(filePath);
        return tokens.Count >= 2 ? tokens[^1] : string.Empty;
    }

    private static void CollectSameDirectorySiblings(
        string targetPath,
        string? workspacePath,
        IDictionary<string, string> contents)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return;
        }

        var targetDir = ParentDirectory(targetPath);
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return;
        }

        string resolvedDir;
        try
        {
            resolvedDir = WorktreeEditApplier.ValidateAndResolvePath(workspacePath, targetDir);
        }
        catch
        {
            return;
        }

        if (!Directory.Exists(resolvedDir))
        {
            return;
        }

        var targetExt = Path.GetExtension(targetPath);
        string[] files;
        try
        {
            files = Directory.GetFiles(resolvedDir, "*" + targetExt, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        foreach (var fullPath in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(MaxSameDirectoryFiles))
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
                string.Equals(relative, targetPath, StringComparison.OrdinalIgnoreCase) ||
                contents.ContainsKey(relative))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxSameDirectoryFileBytes)
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(fullPath);
                if (WorktreeEditApplier.IsBinaryContent(bytes))
                {
                    continue;
                }

                var content = WorktreeEditApplier.DecodeUtf8Text(bytes, out _);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    contents[relative] = content;
                }
            }
            catch
            {
                // Best-effort local evidence only; generation proceeds without an exemplar.
            }
        }
    }

    private static IReadOnlyList<string> TokenizeFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
        if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]))
        {
            name = name[1..];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch is '.' or '_' or '-' or ' ')
            {
                FlushToken(tokens, current);
                continue;
            }

            if (char.IsUpper(ch) && current.Length > 0)
            {
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                var currentIsLower = char.IsLower(current[^1]);
                if (currentIsLower || nextIsLower)
                {
                    FlushToken(tokens, current);
                }
            }

            current.Append(ch);
        }

        FlushToken(tokens, current);
        return tokens;
    }

    private static void FlushToken(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }

    private static bool IsSignificantStem(string stem) =>
        !string.IsNullOrWhiteSpace(stem) && stem.Length > 2;

    private static string ParentDirectory(string path)
    {
        var normalized = DeveloperAgent.NormalizeFocusedRepairPath(path);
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? string.Empty : normalized[..slash];
    }

    private static string DirectoryName(string directory)
    {
        var normalized = DeveloperAgent.NormalizeFocusedRepairPath(directory);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    private static bool LooksLikeStructuralExemplarLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("//") || trimmed.StartsWith('#') || trimmed.StartsWith('*'))
        {
            return false;
        }

        return trimmed.StartsWith("import ", StringComparison.Ordinal) ||
               trimmed.StartsWith("export ", StringComparison.Ordinal) ||
               trimmed.StartsWith("from ", StringComparison.Ordinal) ||
               trimmed.StartsWith("using ", StringComparison.Ordinal) ||
               trimmed.StartsWith("package ", StringComparison.Ordinal) ||
               trimmed.Contains("require(") ||
               trimmed.Contains("constructor(") ||
               Regex.IsMatch(trimmed, @"\b(class|interface|function|def|func|public|internal|export)\b");
    }
}
