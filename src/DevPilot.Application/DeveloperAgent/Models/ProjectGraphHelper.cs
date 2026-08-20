using System.Xml.Linq;

namespace DevPilot.Application.DeveloperAgent.Models;

public sealed class DiscoveredProjectNode
{
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectDirectory { get; set; } = string.Empty;
    public bool IsTestProject { get; set; }
    public List<string> ProjectReferences { get; set; } = new();
    public List<string> PackageReferences { get; set; } = new();
}

public static class ProjectGraphHelper
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "dist", "build", "coverage", ".gemini", ".vscode"
    };

    public static string NormalizeAndValidateRelativePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(rawPath));
        }

        var trimmed = rawPath.Trim();

        if (Path.IsPathRooted(trimmed) || trimmed.StartsWith('\\') || trimmed.StartsWith('/') ||
            (trimmed.Length > 1 && trimmed[1] == ':'))
        {
            throw new InvalidOperationException($"Absolute file paths are forbidden: '{rawPath}'.");
        }

        var normalized = trimmed.Replace('\\', '/');

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                throw new InvalidOperationException($"Directory traversal '..' is forbidden: '{rawPath}'.");
            }
        }

        return string.Join('/', segments);
    }

    public static bool IsCsFileInProjectRoot(string relativeFilePath, IReadOnlyList<string> projectRoots)
    {
        if (projectRoots == null || projectRoots.Count == 0)
        {
            return true;
        }

        var normalizedPath = relativeFilePath.Replace('\\', '/').TrimStart('/');

        foreach (var projRoot in projectRoots)
        {
            if (string.IsNullOrEmpty(projRoot))
            {
                return true;
            }

            var projPrefix = projRoot.TrimEnd('/') + "/";
            if (normalizedPath.StartsWith(projPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsTestFileCandidate(string relativeFilePath)
    {
        if (string.IsNullOrWhiteSpace(relativeFilePath)) return false;
        var normalized = relativeFilePath.Replace('\\', '/').ToLowerInvariant();
        var fileName = Path.GetFileName(normalized);
        return fileName.EndsWith("tests.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith("test.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith("spec.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith("specs.cs", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("test/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryRemapTestFileToSingleTestProject(
        string relativeFilePath,
        IReadOnlyList<DiscoveredProjectNode> projectGraph,
        out string remappedPath,
        out string? failureReason)
    {
        remappedPath = relativeFilePath;
        failureReason = null;

        if (projectGraph == null || projectGraph.Count == 0)
        {
            failureReason = "No discovered .NET projects found in workspace.";
            return false;
        }

        var testProjects = projectGraph.Where(p => p.IsTestProject).ToList();
        if (testProjects.Count == 0)
        {
            failureReason = $"Impacted C# file '{relativeFilePath}' is outside all project roots, and no test project was discovered.";
            return false;
        }

        if (testProjects.Count > 1)
        {
            var testDirs = string.Join(", ", testProjects.Select(p => p.ProjectDirectory));
            failureReason = $"Impacted C# test file '{relativeFilePath}' is outside all project roots, and multiple candidate test projects exist ({testDirs}). Ambiguous mapping cannot be resolved safely.";
            return false;
        }

        var singleTestDir = testProjects[0].ProjectDirectory.TrimEnd('/');
        var normalized = relativeFilePath.Replace('\\', '/').TrimStart('/');

        // Extract the subpath within the bogus/imaginary test project directory
        string subPath;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            subPath = segments.Length == 1 ? segments[0] : Path.GetFileName(normalized);
        }
        else
        {
            // Find the last segment that contains "test" among the leading directory segments
            int fileIndex = segments.Length - 1;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                if (seg.Contains("test", StringComparison.OrdinalIgnoreCase))
                {
                    fileIndex = i + 1;
                }
            }
            subPath = string.Join('/', segments.Skip(fileIndex));
            if (string.IsNullOrWhiteSpace(subPath))
            {
                subPath = segments[^1];
            }
        }

        remappedPath = $"{singleTestDir}/{subPath}";
        return true;
    }

    public static bool TryResolveModifyTarget(
        string relativeFilePath,
        string workspacePath,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph,
        IReadOnlyList<string>? projectRoots,
        out string resolvedRelativePath,
        out string? failureReason)
    {
        resolvedRelativePath = relativeFilePath;
        failureReason = null;

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            // If workspace path is not on disk, keep path as-is
            return true;
        }

        var normalized = relativeFilePath.Replace('\\', '/').TrimStart('/');
        var fullPath = Path.Combine(workspacePath, normalized.Replace('/', Path.DirectorySeparatorChar));

        // 1. If the file already exists on disk, it is grounded and valid.
        if (File.Exists(fullPath))
        {
            resolvedRelativePath = normalized;
            return true;
        }

        // 2. Discover existing files in the workspace (excluding build/artifact dirs)
        var canonicalWorkspace = GetCanonicalRealPath(workspacePath);
        var searchExt = Path.GetExtension(normalized);
        var searchPattern = string.IsNullOrEmpty(searchExt) ? "*.*" : "*" + searchExt;
        var existingFullFiles = SafeFindFiles(canonicalWorkspace, searchPattern);

        var existingRelativeFiles = existingFullFiles
            .Select(f => Path.GetRelativePath(canonicalWorkspace, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("..", StringComparison.Ordinal) && !f.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (existingRelativeFiles.Count == 0)
        {
            failureReason = $"Impacted file path '{relativeFilePath}' with action 'Modify' does not exist in the repository and cannot be deterministically resolved.";
            return false;
        }

        var targetFileName = Path.GetFileName(normalized);
        var targetFileNameWithoutExt = Path.GetFileNameWithoutExtension(normalized);

        // Determine target project root if path starts with one
        var targetProjectRoot = projectRoots?.FirstOrDefault(r =>
            !string.IsNullOrEmpty(r) && (normalized.StartsWith(r.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) || normalized.Equals(r, StringComparison.OrdinalIgnoreCase)));

        var candidatePool = (targetProjectRoot != null
            ? existingRelativeFiles.Where(f => f.StartsWith(targetProjectRoot.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)).ToList()
            : existingRelativeFiles);

        if (candidatePool.Count == 0)
        {
            candidatePool = existingRelativeFiles;
        }

        // --- Match Strategy 1: Exact Filename Match ---
        var exactNameMatches = candidatePool
            .Where(f => Path.GetFileName(f).Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (exactNameMatches.Count == 1)
        {
            resolvedRelativePath = exactNameMatches[0];
            return true;
        }

        if (exactNameMatches.Count > 1)
        {
            failureReason = $"Impacted file path '{relativeFilePath}' with action 'Modify' does not exist in the repository and matches multiple candidate files ({string.Join(", ", exactNameMatches)}). Ambiguous mapping cannot be resolved safely.";
            return false;
        }

        // If target project pool had 0 exact matches, check whole workspace for exact filename match
        if (targetProjectRoot != null && candidatePool != existingRelativeFiles)
        {
            var workspaceExactMatches = existingRelativeFiles
                .Where(f => Path.GetFileName(f).Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (workspaceExactMatches.Count == 1)
            {
                resolvedRelativePath = workspaceExactMatches[0];
                return true;
            }
            if (workspaceExactMatches.Count > 1)
            {
                failureReason = $"Impacted file path '{relativeFilePath}' with action 'Modify' does not exist in the repository and matches multiple candidate files ({string.Join(", ", workspaceExactMatches)}). Ambiguous mapping cannot be resolved safely.";
                return false;
            }
        }

        // --- Match Strategy 2: Canonical Role Suffix + Domain Stem Alias ---
        var knownSuffixes = new[]
        {
            "Repository", "Controller", "Service", "Handler", "Command", "Query",
            "Manager", "Client", "Validator", "Factory", "Store", "DbContext",
            "Hub", "Worker", "Processor", "Provider"
        };

        var matchedSuffix = knownSuffixes.FirstOrDefault(s => targetFileNameWithoutExt.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (matchedSuffix != null)
        {
            var baseStem = targetFileNameWithoutExt[..^matchedSuffix.Length];
            var strippedDomain = StripKnownPrefixes(baseStem);

            var stemMatches = new List<string>();
            foreach (var candidate in candidatePool)
            {
                var candFileNameWithoutExt = Path.GetFileNameWithoutExtension(candidate);
                if (!candFileNameWithoutExt.EndsWith(matchedSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var candBaseStem = candFileNameWithoutExt[..^matchedSuffix.Length];
                var candStrippedDomain = StripKnownPrefixes(candBaseStem);

                if (string.IsNullOrWhiteSpace(strippedDomain) || AreDomainNamesMatching(strippedDomain, candStrippedDomain))
                {
                    stemMatches.Add(candidate);
                }
            }

            var distinctStemMatches = stemMatches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctStemMatches.Count == 1)
            {
                resolvedRelativePath = distinctStemMatches[0];
                return true;
            }
            if (distinctStemMatches.Count > 1)
            {
                failureReason = $"Impacted file path '{relativeFilePath}' with action 'Modify' does not exist in the repository and matches multiple candidate files ({string.Join(", ", distinctStemMatches)}). Ambiguous mapping cannot be resolved safely.";
                return false;
            }
        }

        failureReason = $"Impacted file path '{relativeFilePath}' with action 'Modify' does not exist in the repository and cannot be deterministically resolved.";
        return false;
    }

    private static string StripKnownPrefixes(string stem)
    {
        if (stem.StartsWith("Ef", StringComparison.OrdinalIgnoreCase)) return stem[2..];
        if (stem.StartsWith("I", StringComparison.Ordinal) && stem.Length > 1 && char.IsUpper(stem[1])) return stem[1..];
        if (stem.StartsWith("Mock", StringComparison.OrdinalIgnoreCase)) return stem[4..];
        if (stem.StartsWith("Fake", StringComparison.OrdinalIgnoreCase)) return stem[4..];
        if (stem.StartsWith("Stub", StringComparison.OrdinalIgnoreCase)) return stem[4..];
        if (stem.StartsWith("Async", StringComparison.OrdinalIgnoreCase)) return stem[5..];
        return stem;
    }

    private static bool AreDomainNamesMatching(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

        // Plural / singular: e.g. "Task" vs "Tasks", "Workspace" vs "Workspaces"
        if (a.TrimEnd('s').Equals(b.TrimEnd('s'), StringComparison.OrdinalIgnoreCase)) return true;
        if (a.TrimEnd('e', 's').Equals(b.TrimEnd('e', 's'), StringComparison.OrdinalIgnoreCase)) return true;

        // Compound name substring matching (e.g. "DevelopmentTask" contains "Task", "RepositoryWorkspace" contains "Workspace", "TaskExecution" contains "Execution")
        if (a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase))
        {
            var shorter = a.Length < b.Length ? a : b;
            if (shorter.Length >= 3) return true;
        }

        return false;
    }

    public static List<string> DiscoverProjectRoots(string workspacePath)
    {
        var canonicalWorkspace = GetCanonicalRealPath(workspacePath);
        var csprojFiles = SafeFindFiles(canonicalWorkspace, "*.csproj");
        var projectRoots = new List<string>();

        foreach (var file in csprojFiles)
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir)) continue;

            var relativeDir = Path.GetRelativePath(canonicalWorkspace, dir).Replace('\\', '/');
            if (relativeDir == ".") relativeDir = string.Empty;

            if (!projectRoots.Contains(relativeDir, StringComparer.OrdinalIgnoreCase))
            {
                projectRoots.Add(relativeDir);
            }
        }

        return projectRoots;
    }

    public static List<DiscoveredProjectNode> DiscoverProjectGraph(string workspacePath)
    {
        var canonicalWorkspace = GetCanonicalRealPath(workspacePath);
        var csprojFiles = SafeFindFiles(canonicalWorkspace, "*.csproj");
        var nodes = new List<DiscoveredProjectNode>();

        foreach (var fullPath in csprojFiles)
        {
            var relativeProjPath = Path.GetRelativePath(canonicalWorkspace, fullPath).Replace('\\', '/');
            var projDir = Path.GetDirectoryName(fullPath) ?? canonicalWorkspace;
            var relativeProjDir = Path.GetRelativePath(canonicalWorkspace, projDir).Replace('\\', '/');
            if (string.IsNullOrEmpty(relativeProjDir) || relativeProjDir == ".")
            {
                relativeProjDir = ".";
            }

            var projectName = Path.GetFileNameWithoutExtension(fullPath);
            bool isTest = false;
            var references = new List<string>();
            var packages = new List<string>();

            if (projectName.Contains("Test", StringComparison.OrdinalIgnoreCase))
            {
                isTest = true;
            }

            try
            {
                var content = File.ReadAllText(fullPath);
                var doc = XDocument.Parse(content);

                var isTestElem = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName.Equals("IsTestProject", StringComparison.OrdinalIgnoreCase));
                if (isTestElem != null && bool.TryParse(isTestElem.Value.Trim(), out var parsedIsTest))
                {
                    isTest = parsedIsTest;
                }

                var pkgRefs = doc.Descendants()
                    .Where(e => e.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase));
                foreach (var pkg in pkgRefs)
                {
                    var pkgInclude = (string?)pkg.Attribute("Include") ?? (string?)pkg.Attribute("include");
                    if (!string.IsNullOrEmpty(pkgInclude))
                    {
                        var trimmedPkg = pkgInclude.Trim();
                        if (!packages.Contains(trimmedPkg, StringComparer.OrdinalIgnoreCase))
                        {
                            packages.Add(trimmedPkg);
                        }

                        if (trimmedPkg.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                            trimmedPkg.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
                            trimmedPkg.Contains("mstest", StringComparison.OrdinalIgnoreCase) ||
                            trimmedPkg.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                        {
                            isTest = true;
                        }
                    }
                }

                var projRefs = doc.Descendants()
                    .Where(e => e.Name.LocalName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase));
                foreach (var pref in projRefs)
                {
                    var include = (string?)pref.Attribute("Include") ?? (string?)pref.Attribute("include");
                    if (!string.IsNullOrWhiteSpace(include))
                    {
                        var normalizedInclude = include.Trim().Replace('\\', '/');
                        var hostInclude = normalizedInclude.Replace('/', Path.DirectorySeparatorChar);
                        var resolvedFull = Path.GetFullPath(Path.Combine(projDir, hostInclude));
                        var canonicalRef = GetCanonicalRealPath(resolvedFull);
                        if (IsSubPath(canonicalWorkspace, canonicalRef))
                        {
                            var relRef = Path.GetRelativePath(canonicalWorkspace, canonicalRef).Replace('\\', '/');
                            if (!references.Contains(relRef, StringComparer.OrdinalIgnoreCase))
                            {
                                references.Add(relRef);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Graceful fallback if XML parsing fails
            }

            nodes.Add(new DiscoveredProjectNode
            {
                ProjectPath = relativeProjPath,
                ProjectName = projectName,
                ProjectDirectory = relativeProjDir,
                IsTestProject = isTest,
                ProjectReferences = references,
                PackageReferences = packages
            });
        }

        return nodes.OrderBy(n => n.ProjectPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> SafeFindFiles(string rootPath, string searchPattern)
    {
        var result = new List<string>();
        if (!Directory.Exists(rootPath)) return result;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            var files = Directory.EnumerateFiles(rootPath, searchPattern, options);
            foreach (var file in files)
            {
                if (IsUnderExcludedDirectory(rootPath, file))
                {
                    continue;
                }
                result.Add(file);
            }
        }
        catch
        {
            // Fallback to manual recursive scan
            SafeFindFilesManual(rootPath, searchPattern, result);
        }

        return result;
    }

    private static void SafeFindFilesManual(string currentDir, string searchPattern, List<string> accumulator)
    {
        try
        {
            foreach (var file in Directory.GetFiles(currentDir, searchPattern))
            {
                accumulator.Add(file);
            }

            foreach (var dir in Directory.GetDirectories(currentDir))
            {
                var dirName = Path.GetFileName(dir);
                if (ExcludedDirectoryNames.Contains(dirName))
                {
                    continue;
                }

                SafeFindFilesManual(dir, searchPattern, accumulator);
            }
        }
        catch
        {
            // Suppress errors during directory traversal
        }
    }

    private static bool IsUnderExcludedDirectory(string rootPath, string filePath)
    {
        var rel = Path.GetRelativePath(rootPath, filePath);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (ExcludedDirectoryNames.Contains(parts[i]))
            {
                return true;
            }
        }
        return false;
    }

    private static string GetCanonicalRealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                var finalPath = Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true);
                if (finalPath != null) return finalPath.FullName;
            }
            else if (File.Exists(fullPath))
            {
                var finalPath = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
                if (finalPath != null) return finalPath.FullName;
            }
            return fullPath;
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    private static bool IsSubPath(string basePath, string targetPath)
    {
        var rel = Path.GetRelativePath(basePath, targetPath);
        return !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
    }
}
