using System.Text.RegularExpressions;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.Executions;

namespace DevPilot.Infrastructure.DeveloperAgent;

public sealed record SameRoleExemplar(string FilePath, string BoundedExcerpt);

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

    private static readonly Regex PythonImportRegex = new(
        @"^\s*(?:from\s+(\.*[A-Za-z0-9_./]+)\s+import|import\s+(\.*[A-Za-z0-9_./]+))",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex GoImportRegex = new(
        @"import\s+(?:\w+\s+)?""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex JavaImportRegex = new(
        @"^\s*import\s+(?:static\s+)?([A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".cs", ".py", ".go", ".java", ".kt"
    };

    public static IReadOnlyCollection<string> SourceExtensionsPublic => SourceExtensions;

    public const int MaxExemplarChars = 1200;
    public const int MaxExemplarLines = 24;
    private const int MaxNeighborFilesPerDirectory = 16;
    private const int MaxNeighborDirectories = 12;
    private const int MaxNeighborFileBytes = 16_384;

    private static readonly Regex WiringEvidenceRegex = new(
        @"\b(app\.use|app\.map|app\.listen|express\s*\(|createServer|router\.|register\w*Route|UseMiddleware|UseRouting|UseEndpoints|MapControllers|MapGet|MapPost|builder\.Services|AddSingleton|AddScoped|AddTransient|IApplicationBuilder|WebApplication)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildPrerequisiteMap(
        IReadOnlyList<ManifestFileEntry> files,
        IReadOnlyDictionary<string, string>? existingFileContents = null,
        string? workspacePath = null)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (files == null || files.Count == 0)
        {
            return map.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value, StringComparer.OrdinalIgnoreCase);
        }

        var repositoryContents = MergeContents(
            existingFileContents,
            CollectNeighborFileContents(workspacePath, files.Select(file => file.FilePath)));

        foreach (var file in files)
        {
            map[file.FilePath] = new List<string>();
        }

        var hardEdges = new HashSet<(string Consumer, string Producer)>(StringPairComparer.Instance);
        var layerEdges = new HashSet<(string Consumer, string Producer)>(StringPairComparer.Instance);
        var inferredEdges = new HashSet<(string Consumer, string Producer)>(StringPairComparer.Instance);

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
                if (HasHardDependency(consumer, producer, existingFileContents))
                {
                    hardEdges.Add((consumer.FilePath, producer.FilePath));
                }
                else if (HasAnalogousRepositoryDependency(consumer, producer, files, repositoryContents))
                {
                    inferredEdges.Add((consumer.FilePath, producer.FilePath));
                }
                else if (HasLayerAffinityDependency(consumer, producer))
                {
                    layerEdges.Add((consumer.FilePath, producer.FilePath));
                }
            }
        }

        foreach (var (consumer, producer) in inferredEdges)
        {
            layerEdges.Remove((producer, consumer));
        }

        SuppressSameStemLayerEdgesWhenAnalogEvidenceExists(inferredEdges, layerEdges);

        var explicitEdges = new HashSet<(string Consumer, string Producer)>(hardEdges, StringPairComparer.Instance);
        foreach (var layerEdge in layerEdges)
        {
            explicitEdges.Add(layerEdge);
        }

        inferredEdges = RemoveCyclicInferredEdges(files.Select(file => file.FilePath), explicitEdges, inferredEdges);

        foreach (var (consumer, producer) in explicitEdges.Concat(inferredEdges))
        {
            if (map.TryGetValue(consumer, out var producers) &&
                !producers.Contains(producer, StringComparer.OrdinalIgnoreCase))
            {
                producers.Add(producer);
            }
        }

        return map.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<string>)item.Value,
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

        return HasHardDependency(consumer, producer, existingFileContents) ||
               HasLayerAffinityDependency(consumer, producer);
    }

    internal static bool HasHardDependency(
        ManifestFileEntry consumer,
        ManifestFileEntry producer,
        IReadOnlyDictionary<string, string>? existingFileContents)
    {
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

        if (IsInterfaceImplementationPair(consumer.FilePath, producer.FilePath) &&
            DeveloperAgent.GetSemanticLayerScore(consumer.FilePath) >= DeveloperAgent.GetSemanticLayerScore(producer.FilePath))
        {
            return true;
        }

        return HasRepositoryWiringDependency(consumer, producer, existingFileContents);
    }

    internal static bool HasLayerAffinityDependency(ManifestFileEntry consumer, ManifestFileEntry producer)
    {
        var consumerLayer = DeveloperAgent.GetSemanticLayerScore(consumer.FilePath);
        var producerLayer = DeveloperAgent.GetSemanticLayerScore(producer.FilePath);
        return consumerLayer > producerLayer &&
               HasFeatureAffinity(
                   FeatureStem(consumer.FilePath),
                   FeatureStem(producer.FilePath),
                   consumer.FilePath,
                   producer.FilePath);
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

    public static bool HasAnalogousRepositoryDependency(
        ManifestFileEntry consumer,
        ManifestFileEntry producer,
        IReadOnlyList<ManifestFileEntry>? allFiles = null,
        IReadOnlyDictionary<string, string>? repositoryContents = null)
    {
        if (consumer == null || producer == null || repositoryContents == null || repositoryContents.Count == 0)
        {
            return false;
        }

        if (string.Equals(consumer.FilePath, producer.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var consumerStem = GetFeatureStem(consumer.FilePath);
        var producerStem = GetFeatureStem(producer.FilePath);
        if (!HasFeatureAffinity(consumerStem, producerStem, consumer.FilePath, producer.FilePath))
        {
            return false;
        }

        var consumerDir = ParentDirectory(consumer.FilePath);
        var producerDir = ParentDirectory(producer.FilePath);
        if (string.IsNullOrWhiteSpace(consumerDir) ||
            string.IsNullOrWhiteSpace(producerDir) ||
            string.Equals(consumerDir, producerDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var planned = new HashSet<string>(
            (allFiles ?? Array.Empty<ManifestFileEntry>()).Select(file => Normalize(file.FilePath)),
            StringComparer.OrdinalIgnoreCase)
        {
            Normalize(consumer.FilePath),
            Normalize(producer.FilePath)
        };

        var forward = CountAnalogousDirectoryEvidence(consumerDir, producerDir, planned, repositoryContents);
        var reverse = CountAnalogousDirectoryEvidence(producerDir, consumerDir, planned, repositoryContents);
        return forward > 0 && reverse == 0;
    }

    public static IReadOnlyDictionary<string, string> CollectNeighborFileContents(
        string? workspacePath,
        IEnumerable<string> filePaths)
    {
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath) || filePaths == null)
        {
            return contents;
        }

        var directories = filePaths
            .Select(ParentDirectory)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxNeighborDirectories)
            .ToList();

        foreach (var relativeDir in directories)
        {
            var absoluteDir = Path.Combine(workspacePath, relativeDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(absoluteDir))
            {
                continue;
            }

            var candidates = new List<(string AbsolutePath, int ImportScore, byte[] Bytes)>();
            foreach (var absolutePath in Directory.GetFiles(absoluteDir))
            {
                if (!SourceExtensions.Contains(Path.GetExtension(absolutePath)))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(absolutePath);
                    if (info.Length <= 0 || info.Length > MaxNeighborFileBytes)
                    {
                        continue;
                    }

                    var bytes = File.ReadAllBytes(absolutePath);
                    if (WorktreeEditApplier.IsBinaryContent(bytes))
                    {
                        continue;
                    }

                    var text = WorktreeEditApplier.DecodeUtf8Text(bytes, out _);
                    candidates.Add((absolutePath, LooksLikeImportEvidence(text) ? 1 : 0, bytes));
                }
                catch
                {
                    // Neighbor scan is best-effort and must not fail generation.
                }
            }

            foreach (var (absolutePath, _, bytes) in candidates
                         .OrderByDescending(item => item.ImportScore)
                         .ThenBy(item => item.AbsolutePath, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxNeighborFilesPerDirectory))
            {
                var relative = DeveloperAgent.NormalizeFocusedRepairPath(
                    Path.GetRelativePath(workspacePath, absolutePath));
                if (contents.ContainsKey(relative))
                {
                    continue;
                }

                var text = WorktreeEditApplier.DecodeUtf8Text(bytes, out _);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    contents[relative] = text;
                }
            }
        }

        return contents;
    }

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
        var targetStem = GetFeatureStem(targetPath);
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

            var candidateStem = GetFeatureStem(path);
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
            else if (string.Equals(DirectoryName(candidateDir), targetDirName, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(targetDirName))
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

        return $"The closest same-role repository exemplar is approximately {lineCount} source lines. Stay close to that size unless additional lines are required for correctness.";
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

    public static IReadOnlyList<string> ExtractLocalImportSpecs(string content, string consumerPath)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return results;
        }

        foreach (Match match in JsImportRegex.Matches(content))
        {
            var spec = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(spec) && spec.StartsWith(".", StringComparison.Ordinal))
            {
                results.Add(spec);
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ParentDirectoryPublic(string path) => ParentDirectory(path);

    public static string ResolveRelativeImportPublic(string consumerDir, string spec) =>
        ResolveRelativeImport(consumerDir, spec);

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

    public static string GetFeatureStem(string filePath) => FeatureStem(filePath);

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

        foreach (Match match in PythonImportRegex.Matches(content))
        {
            var spec = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (spec.StartsWith('.'))
            {
                var resolved = ResolveRelativeImport(consumerDir, spec.Replace('.', '/'));
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    results.Add(resolved);
                }
            }
            else
            {
                results.Add(spec.Replace('.', '/'));
            }
        }

        foreach (Match match in GoImportRegex.Matches(content))
        {
            results.Add(match.Groups[1].Value.TrimStart('/'));
        }

        foreach (Match match in JavaImportRegex.Matches(content))
        {
            results.Add(match.Groups[1].Value.Replace('.', '/'));
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
            producerWithoutExt.EndsWith('/' + normalizedImport.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
            producerWithoutExt.EndsWith('/' + importWithoutExt.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
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
        foreach (var ext in new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".cs", ".py", ".go", ".java", ".kt" })
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^ext.Length];
            }
        }

        return path;
    }

    private static int CountAnalogousDirectoryEvidence(
        string consumerDir,
        string producerDir,
        ISet<string> plannedFiles,
        IReadOnlyDictionary<string, string> repositoryContents)
    {
        var evidence = 0;
        foreach (var (path, content) in repositoryContents)
        {
            if (plannedFiles.Contains(Normalize(path)) ||
                !string.Equals(ParentDirectory(path), consumerDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var import in ExtractImportSpecs(content, path))
            {
                var resolved = ResolveExistingImport(import, producerDir, repositoryContents);
                if (resolved == null || plannedFiles.Contains(Normalize(resolved)))
                {
                    continue;
                }

                if (HasFeatureAffinity(GetFeatureStem(path), GetFeatureStem(resolved), path, resolved))
                {
                    evidence++;
                }
            }
        }

        return evidence;
    }

    private static string? ResolveExistingImport(
        string importSpec,
        string expectedProducerDir,
        IReadOnlyDictionary<string, string> repositoryContents)
    {
        foreach (var existing in repositoryContents.Keys)
        {
            if (!string.Equals(ParentDirectory(existing), expectedProducerDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PathsReferToSameSource(importSpec, existing))
            {
                return existing;
            }
        }

        var normalized = Normalize(importSpec);
        if (string.Equals(ParentDirectory(normalized), expectedProducerDir, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(DirectoryName(normalized), DirectoryName(expectedProducerDir), StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return null;
    }

    private static HashSet<(string Consumer, string Producer)> RemoveCyclicInferredEdges(
        IEnumerable<string> files,
        HashSet<(string Consumer, string Producer)> explicitEdges,
        HashSet<(string Consumer, string Producer)> inferredEdges)
    {
        var kept = new HashSet<(string Consumer, string Producer)>(inferredEdges, StringPairComparer.Instance);
        if (kept.Count == 0)
        {
            return kept;
        }

        foreach (var inferred in inferredEdges.ToList())
        {
            var trial = new HashSet<(string Consumer, string Producer)>(explicitEdges, StringPairComparer.Instance);
            foreach (var edge in kept)
            {
                trial.Add(edge);
            }

            if (CreatesCycle(files, trial))
            {
                kept.Remove(inferred);
            }
        }

        return kept;
    }

    private static void SuppressSameStemLayerEdgesWhenAnalogEvidenceExists(
        HashSet<(string Consumer, string Producer)> inferredEdges,
        HashSet<(string Consumer, string Producer)> layerEdges)
    {
        if (inferredEdges.Count == 0 || layerEdges.Count == 0)
        {
            return;
        }

        var inferredStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (consumer, _) in inferredEdges)
        {
            var stem = GetFeatureStem(consumer);
            if (IsSignificantStem(stem))
            {
                inferredStems.Add(stem);
            }
        }

        if (inferredStems.Count == 0)
        {
            return;
        }

        foreach (var layerEdge in layerEdges.ToList())
        {
            var consumerStem = GetFeatureStem(layerEdge.Consumer);
            if (!IsSignificantStem(consumerStem) ||
                !inferredStems.Contains(consumerStem) ||
                !string.Equals(consumerStem, GetFeatureStem(layerEdge.Producer), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            layerEdges.Remove(layerEdge);
        }
    }

    private static bool LooksLikeImportEvidence(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.Contains("import ", StringComparison.Ordinal) ||
               content.Contains("from ", StringComparison.Ordinal) ||
               content.Contains("require(", StringComparison.Ordinal) ||
               content.Contains("using ", StringComparison.Ordinal) ||
               content.Contains("package ", StringComparison.Ordinal);
    }

    private static bool CreatesCycle(
        IEnumerable<string> files,
        IEnumerable<(string Consumer, string Producer)> edges)
    {
        var adjacency = files.ToDictionary(file => Normalize(file), _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (consumer, producer) in edges)
        {
            if (!adjacency.ContainsKey(consumer))
            {
                adjacency[consumer] = new List<string>();
            }

            adjacency[consumer].Add(producer);
        }

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in adjacency.Keys)
        {
            if (HasCycle(node, adjacency, state))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCycle(
        string node,
        IReadOnlyDictionary<string, List<string>> adjacency,
        Dictionary<string, int> state)
    {
        if (state.TryGetValue(node, out var current))
        {
            return current == 1;
        }

        state[node] = 1;
        if (adjacency.TryGetValue(node, out var next))
        {
            foreach (var child in next)
            {
                if (HasCycle(child, adjacency, state))
                {
                    return true;
                }
            }
        }

        state[node] = 2;
        return false;
    }

    private static IReadOnlyDictionary<string, string> MergeContents(
        IReadOnlyDictionary<string, string>? first,
        IReadOnlyDictionary<string, string>? second)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (first != null)
        {
            foreach (var (path, content) in first)
            {
                if (!string.IsNullOrWhiteSpace(content))
                {
                    merged[path] = content;
                }
            }
        }

        if (second != null)
        {
            foreach (var (path, content) in second)
            {
                if (!string.IsNullOrWhiteSpace(content))
                {
                    merged.TryAdd(path, content);
                }
            }
        }

        return merged;
    }

    private static string RoleRemainder(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
        var stem = FeatureStem(filePath);
        if (IsSignificantStem(stem) &&
            name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) &&
            name.Length > stem.Length)
        {
            return name[stem.Length..];
        }

        return string.Empty;
    }

    private static string DirectoryName(string directory)
    {
        var normalized = Normalize(directory);
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

    private static string Normalize(string path) => DeveloperAgent.NormalizeFocusedRepairPath(path);

    private sealed class StringPairComparer : IEqualityComparer<(string Consumer, string Producer)>
    {
        public static readonly StringPairComparer Instance = new();

        public bool Equals((string Consumer, string Producer) x, (string Consumer, string Producer) y) =>
            string.Equals(x.Consumer, y.Consumer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Producer, y.Producer, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Consumer, string Producer) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Consumer),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Producer));
    }
}
