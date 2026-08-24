using System.Text.RegularExpressions;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.Executions;

namespace DevPilot.Infrastructure.DeveloperAgent;

/// <summary>
/// Builds a conservative generation prerequisite graph from explicit manifest
/// dependencies, feature/name affinity, and repository-native import/wiring evidence.
/// Does not guess from filename allowlists.
/// </summary>
public static class GenerationDependencyAnalyzer
{
    private static readonly string[] FeatureSuffixes =
    {
        "Controller", "Service", "Repository", "Routes", "Route", "Middleware",
        "Handler", "Tests", "Test", "Dtos", "Dto", "Interface"
    };

    private static readonly Regex JsImportRegex = new(
        @"(?:import|export)\s+(?:[^'""]+from\s+)?['""](\.\.?/[^'""]+)['""]|require\s*\(\s*['""](\.\.?/[^'""]+)['""]\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CsUsingRegex = new(
        @"^\s*using\s+(?:static\s+)?([A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex WiringEvidenceRegex = new(
        @"\b(app\.use|app\.map|app\.listen|express\s*\(|createServer|router\.|register\w*Route|UseMiddleware|UseRouting|UseEndpoints|MapControllers|MapGet|MapPost|builder\.Services|AddSingleton|AddScoped|AddTransient|IApplicationBuilder|WebApplication)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildPrerequisiteMap(
        IReadOnlyList<ManifestFileEntry> files,
        IReadOnlyDictionary<string, string>? existingFileContents = null)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (files == null || files.Count == 0)
        {
            return map.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var file in files)
        {
            map[file.FilePath] = new List<string>();
        }

        for (var i = 0; i < files.Count; i++)
        {
            var consumer = files[i];
            for (var j = 0; j < files.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var producer = files[j];
                if (DependsOn(consumer, producer, files, existingFileContents))
                {
                    map[consumer.FilePath].Add(producer.FilePath);
                }
            }
        }

        return map.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<string>)item.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    public static bool DependsOn(
        ManifestFileEntry consumer,
        ManifestFileEntry producer,
        IReadOnlyList<ManifestFileEntry>? allFiles = null,
        IReadOnlyDictionary<string, string>? existingFileContents = null)
    {
        if (consumer == null || producer == null ||
            string.Equals(consumer.FilePath, producer.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (consumer.Dependencies != null &&
            consumer.Dependencies.Contains(producer.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var consumerIsTest = ProjectGraphHelper.IsTestFileCandidate(consumer.FilePath);
        var producerIsTest = ProjectGraphHelper.IsTestFileCandidate(producer.FilePath);
        if (consumerIsTest && !producerIsTest)
        {
            return true;
        }

        var consumerLayer = DeveloperAgent.GetSemanticLayerScore(consumer.FilePath);
        var producerLayer = DeveloperAgent.GetSemanticLayerScore(producer.FilePath);
        var consumerStem = FeatureStem(consumer.FilePath);
        var producerStem = FeatureStem(producer.FilePath);

        if (consumerLayer > producerLayer && HasFeatureAffinity(consumerStem, producerStem, consumer.FilePath, producer.FilePath))
        {
            return true;
        }

        if (IsInterfaceImplementationPair(consumer.FilePath, producer.FilePath) && consumerLayer >= producerLayer)
        {
            return true;
        }

        return HasRepositoryWiringDependency(consumer, producer, existingFileContents);
    }

    public static bool HasFeatureAffinity(string consumerStem, string producerStem, string consumerPath, string producerPath)
    {
        if (IsSignificantStem(consumerStem) && IsSignificantStem(producerStem) &&
            string.Equals(consumerStem, producerStem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsSignificantStem(consumerStem) &&
            producerStem.Contains(consumerStem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsSignificantStem(producerStem) &&
            consumerStem.Contains(producerStem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var consumerName = Path.GetFileNameWithoutExtension(consumerPath);
        var producerName = Path.GetFileNameWithoutExtension(producerPath);
        return !string.IsNullOrWhiteSpace(consumerName) &&
               consumerName.Length > 3 &&
               producerName.Contains(consumerName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasRepositoryWiringDependency(
        ManifestFileEntry consumer,
        ManifestFileEntry producer,
        IReadOnlyDictionary<string, string>? existingFileContents)
    {
        if (existingFileContents == null ||
            !TryGetExistingContent(consumer.FilePath, existingFileContents, out var content) ||
            string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var imports = ExtractImportSpecs(content, consumer.FilePath);
        if (imports.Any(import => PathsReferToSameSource(import, producer.FilePath)))
        {
            return true;
        }

        var producerDirectory = ParentDirectory(producer.FilePath);
        if (!string.IsNullOrWhiteSpace(producerDirectory) &&
            imports.Any(import => string.Equals(ParentDirectory(import), producerDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (WiringEvidenceRegex.IsMatch(content))
        {
            var producerName = Path.GetFileNameWithoutExtension(producer.FilePath);
            if (!string.IsNullOrWhiteSpace(producerName) &&
                producerName.Length > 2 &&
                content.Contains(producerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> ExtractImportSpecs(string content, string consumerPath)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return results;
        }

        results.AddRange(DeveloperAgent.ExtractReferencedSourcePaths(content, consumerPath));

        var consumerDir = DeveloperAgent.NormalizeFocusedRepairPath(Path.GetDirectoryName(consumerPath) ?? string.Empty);
        foreach (Match match in JsImportRegex.Matches(content))
        {
            var spec = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var resolved = ResolveRelativeImport(consumerDir, spec);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                results.Add(resolved);
            }
        }

        foreach (Match match in CsUsingRegex.Matches(content))
        {
            var ns = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(ns))
            {
                results.Add(ns.Replace('.', '/'));
            }
        }

        return results
            .Select(DeveloperAgent.NormalizeFocusedRepairPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> SanitizeDiagnosticLinesForActivity(
        IEnumerable<string> lines,
        string? workspaceRoot = null,
        int maxLines = 5,
        int maxCharsPerLine = 240)
    {
        var sanitized = new List<string>();
        if (lines == null)
        {
            return sanitized;
        }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var line = raw.Trim();
            if (!ExecutionDiagnosticEvidence.IsCompilerDiagnosticLine(line))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                line = ExecutionDiagnosticEvidence.MakeRepositoryRelative(line, workspaceRoot);
                var normalizedRoot = workspaceRoot.Replace('\\', '/').TrimEnd('/');
                if (line.Contains(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    line = line.Replace(normalizedRoot + "/", string.Empty, StringComparison.OrdinalIgnoreCase)
                        .Replace(normalizedRoot, string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }

            line = Regex.Replace(line, @"[A-Za-z]:[\\/][^\s:(]+", match =>
            {
                var path = match.Value.Replace('\\', '/');
                var srcIndex = path.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
                return srcIndex >= 0 ? path[(srcIndex + 1)..] : Path.GetFileName(path);
            });

            if (line.Length > maxCharsPerLine)
            {
                line = line[..maxCharsPerLine].TrimEnd();
            }

            sanitized.Add(line);
            if (sanitized.Count >= Math.Max(1, maxLines))
            {
                break;
            }
        }

        return sanitized;
    }

    private static bool TryGetExistingContent(
        string filePath,
        IReadOnlyDictionary<string, string> existingFileContents,
        out string content)
    {
        if (existingFileContents.TryGetValue(filePath, out content!) && !string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        var fileName = Path.GetFileName(filePath);
        foreach (var (path, value) in existingFileContents)
        {
            if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value))
            {
                content = value;
                return true;
            }
        }

        content = string.Empty;
        return false;
    }

    private static bool PathsReferToSameSource(string importSpec, string producerPath)
    {
        var normalizedImport = DeveloperAgent.NormalizeFocusedRepairPath(importSpec);
        var normalizedProducer = DeveloperAgent.NormalizeFocusedRepairPath(producerPath);
        if (string.Equals(normalizedImport, normalizedProducer, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var producerWithoutExt = StripSourceExtension(normalizedProducer);
        var importWithoutExt = StripSourceExtension(normalizedImport);
        if (string.Equals(importWithoutExt, producerWithoutExt, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(importWithoutExt, Path.GetFileName(producerWithoutExt), StringComparison.OrdinalIgnoreCase) ||
            normalizedProducer.EndsWith('/' + normalizedImport.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
            normalizedProducer.EndsWith('/' + importWithoutExt.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(Path.GetFileNameWithoutExtension(normalizedImport), Path.GetFileNameWithoutExtension(normalizedProducer), StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(ParentDirectory(normalizedImport), ParentDirectory(normalizedProducer), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(ParentDirectory(normalizedImport)));
    }

    private static string ResolveRelativeImport(string consumerDir, string spec)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(consumerDir))
        {
            parts.AddRange(DeveloperAgent.NormalizeFocusedRepairPath(consumerDir).Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in DeveloperAgent.NormalizeFocusedRepairPath(spec).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(segment);
        }

        return string.Join('/', parts);
    }

    private static string FeatureStem(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
        if (name.Length > 2 && name[0] is 'I' && char.IsUpper(name[1]))
        {
            name = name[1..];
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in FeatureSuffixes)
            {
                if (name.Length > suffix.Length + 1 &&
                    name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^suffix.Length];
                    changed = true;
                    break;
                }
            }
        }

        return name;
    }

    private static bool IsSignificantStem(string stem) =>
        !string.IsNullOrWhiteSpace(stem) && stem.Length > 2;

    private static bool IsInterfaceImplementationPair(string consumerPath, string producerPath)
    {
        var consumer = Path.GetFileNameWithoutExtension(consumerPath);
        var producer = Path.GetFileNameWithoutExtension(producerPath);
        return producer.Length > 2 &&
               producer[0] is 'I' &&
               char.IsUpper(producer[1]) &&
               consumer.Equals(producer[1..], StringComparison.OrdinalIgnoreCase);
    }

    private static string ParentDirectory(string path)
    {
        var normalized = DeveloperAgent.NormalizeFocusedRepairPath(path);
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? string.Empty : normalized[..slash];
    }

    private static string StripSourceExtension(string path)
    {
        foreach (var ext in new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".cs" })
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^ext.Length];
            }
        }

        return path;
    }
}
