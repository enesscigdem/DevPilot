using System.Text.RegularExpressions;
using DevPilot.Application.DeveloperAgent.Models;

namespace DevPilot.Infrastructure.DeveloperAgent;

/// <summary>
/// Deterministic planned-file prerequisite edges from explicit manifest dependencies
/// or exact local repository references. Does not infer from role names or neighbors.
/// </summary>
public static class PlannedFileDependencyResolver
{
    private static readonly Regex TsJsFromRequireRegex = new(
        @"(?:from|require\s*\()\s*['""](\.\.?/[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex TsJsSideEffectImportRegex = new(
        @"^\s*import\s+['""](\.\.?/[^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PythonRelativeImportRegex = new(
        @"^\s*(?:from\s+(\.+\w+(?:\.\w+)*)\s+import|import\s+(\.+\w+(?:\.\w+)*))",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PythonLocalModuleImportRegex = new(
        @"^\s*from\s+([A-Za-z_][\w]*(?:\.[\w]+)*)\s+import",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CsTypeDeclarationRegex = new(
        @"\b(?:public|internal|protected|private|file)\s+(?:sealed\s+|abstract\s+|partial\s+|static\s+|readonly\s+)*(?:class|record(?:\s+class|\s+struct)?|interface|struct|enum)\s+([A-Za-z_][\w]*)",
        RegexOptions.Compiled);

    private static readonly Regex ScriptTypeDeclarationRegex = new(
        @"^\s*(?:export\s+)?(?:declare\s+)?(?:abstract\s+)?(?:type|interface|class|enum)\s+([A-Za-z_][\w]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PythonClassDeclarationRegex = new(
        @"^\s*class\s+([A-Za-z_][\w]*)\s*[\(:]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly string[] ScriptExtensions = { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs" };
    private static readonly string[] PythonExtensions = { ".py" };

    public static IReadOnlyList<string> ResolveDirectLocalReferences(
        string targetPath,
        string? targetSource,
        IEnumerable<string> plannedPaths)
    {
        var planned = plannedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(targetPath) || planned.Count == 0)
        {
            return results;
        }

        var normalizedTarget = NormalizePath(targetPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in ExtractLocalReferenceSpecs(normalizedTarget, targetSource))
        {
            if (!TryResolveExactPlannedFile(spec, planned, out var resolved) ||
                string.Equals(resolved, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(resolved))
            {
                continue;
            }

            results.Add(ResolveOriginalPlannedPath(resolved, plannedPaths));
        }

        return results;
    }

    public static bool HasDirectLocalReference(
        string targetPath,
        string? targetSource,
        string candidatePath,
        IEnumerable<string> plannedPaths)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var normalizedCandidate = NormalizePath(candidatePath);
        return ResolveDirectLocalReferences(targetPath, targetSource, plannedPaths)
            .Any(path => string.Equals(NormalizePath(path), normalizedCandidate, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> ExtractDeclaredTypeNames(string filePath, string? source)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(source))
        {
            return names;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var extension = Path.GetExtension(filePath);
        IEnumerable<Match> matches = extension.ToLowerInvariant() switch
        {
            ".cs" => CsTypeDeclarationRegex.Matches(source).Cast<Match>(),
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" =>
                ScriptTypeDeclarationRegex.Matches(source).Cast<Match>(),
            ".py" => PythonClassDeclarationRegex.Matches(source).Cast<Match>(),
            _ => Array.Empty<Match>()
        };

        foreach (var match in matches)
        {
            var name = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    public static string? ExtractLockedContractExcerpt(string filePath, string? source, int maxChars = 2000)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var contract = RoslynContractExtractor.ExtractPublicContracts(filePath, source);
            return string.IsNullOrWhiteSpace(contract) ? null : Bound(contract, maxChars);
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
        {
            return ScriptContractExtractor.Extract(source, maxChars);
        }

        if (extension == ".py")
        {
            return ExtractPythonContractExcerpt(source, maxChars);
        }

        var declared = ExtractDeclaredTypeNames(filePath, source);
        if (declared.Count == 0)
        {
            return null;
        }

        var selected = new List<string>();
        var used = 0;
        foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
        {
            if (!declared.Any(name =>
                    Regex.IsMatch(line, $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])")))
            {
                continue;
            }

            var addition = selected.Count == 0 ? line : "\n" + line;
            if (used + addition.Length > maxChars)
            {
                break;
            }

            selected.Add(line);
            used += addition.Length;
        }

        return selected.Count == 0 ? null : string.Join('\n', selected);
    }

    public static IReadOnlyList<string> ResolveUniqueDeclaredTypeOwners(
        string targetPath,
        string? targetSource,
        IReadOnlyDictionary<string, string> plannedSources)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(targetSource) || plannedSources == null)
        {
            return results;
        }

        var ownersByType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, source) in plannedSources)
        {
            if (string.Equals(NormalizePath(path), NormalizePath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var typeName in ExtractDeclaredTypeNames(path, source))
            {
                if (!ownersByType.TryGetValue(typeName, out var owners))
                {
                    owners = new List<string>();
                    ownersByType[typeName] = owners;
                }

                if (!owners.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    owners.Add(path);
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (typeName, owners) in ownersByType)
        {
            if (owners.Count != 1)
            {
                continue;
            }

            if (Regex.IsMatch(
                    targetSource,
                    $@"(?<![A-Za-z0-9_]){Regex.Escape(typeName)}(?![A-Za-z0-9_])"))
            {
                if (seen.Add(owners[0]))
                {
                    results.Add(owners[0]);
                }
            }
        }

        return results;
    }

    public static string? FindUniqueDeclarationOwner(
        string typeName,
        IReadOnlyDictionary<string, string> fileSources)
    {
        if (string.IsNullOrWhiteSpace(typeName) || fileSources == null || fileSources.Count == 0)
        {
            return null;
        }

        var owners = new List<string>();
        foreach (var (path, source) in fileSources)
        {
            if (ExtractDeclaredTypeNames(path, source).Contains(typeName, StringComparer.Ordinal))
            {
                owners.Add(path);
            }
        }

        return owners.Count == 1 ? owners[0] : null;
    }

    private static IEnumerable<string> ExtractLocalReferenceSpecs(string targetPath, string? targetSource)
    {
        if (string.IsNullOrWhiteSpace(targetSource))
        {
            yield break;
        }

        var targetDir = NormalizePath(Path.GetDirectoryName(targetPath) ?? string.Empty);
        var extension = Path.GetExtension(targetPath).ToLowerInvariant();

        if (extension is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
        {
            foreach (Match match in TsJsFromRequireRegex.Matches(targetSource))
            {
                yield return CombineRelative(targetDir, match.Groups[1].Value);
            }

            foreach (Match match in TsJsSideEffectImportRegex.Matches(targetSource))
            {
                yield return CombineRelative(targetDir, match.Groups[1].Value);
            }

            yield break;
        }

        if (extension == ".py")
        {
            foreach (Match match in PythonRelativeImportRegex.Matches(targetSource))
            {
                var spec = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var resolved = ResolvePythonRelativeModule(targetDir, spec);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    yield return resolved;
                }
            }

            foreach (Match match in PythonLocalModuleImportRegex.Matches(targetSource))
            {
                var module = match.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(module) || module.StartsWith('.'))
                {
                    continue;
                }

                yield return module.Replace('.', '/');
            }
        }
    }

    public static IReadOnlyList<string> ResolveUniqueCsharpTypeOwners(
        string targetPath,
        string? targetSource,
        IReadOnlyDictionary<string, string> plannedSources)
    {
        if (string.IsNullOrWhiteSpace(targetPath) ||
            !targetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        return ResolveUniqueDeclaredTypeOwners(targetPath, targetSource, plannedSources);
    }

    private static bool TryResolveExactPlannedFile(
        string referenceSpec,
        IReadOnlyList<string> planned,
        out string resolved)
    {
        resolved = string.Empty;
        var normalized = NormalizePath(referenceSpec);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var matches = new List<string>();
        foreach (var plannedPath in planned)
        {
            if (IsExactLocalMatch(normalized, plannedPath))
            {
                matches.Add(plannedPath);
            }
        }

        if (matches.Count == 1)
        {
            resolved = matches[0];
            return true;
        }

        return false;
    }

    private static bool IsExactLocalMatch(string referenceSpec, string plannedPath)
    {
        if (string.Equals(referenceSpec, plannedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (plannedPath.StartsWith(referenceSpec, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = plannedPath[referenceSpec.Length..];
            if (remainder.StartsWith('.') && IsKnownSourceExtension(remainder))
            {
                return true;
            }

            if (remainder.Equals("/__init__.py", StringComparison.OrdinalIgnoreCase) ||
                remainder.Equals("/index.ts", StringComparison.OrdinalIgnoreCase) ||
                remainder.Equals("/index.js", StringComparison.OrdinalIgnoreCase) ||
                remainder.Equals("/index.tsx", StringComparison.OrdinalIgnoreCase) ||
                remainder.Equals("/index.jsx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKnownSourceExtension(string extension) =>
        ScriptExtensions.Concat(PythonExtensions).Concat(new[] { ".cs" })
            .Contains(extension, StringComparer.OrdinalIgnoreCase);

    private static string ResolveOriginalPlannedPath(string normalized, IEnumerable<string> plannedPaths) =>
        plannedPaths.FirstOrDefault(path =>
            string.Equals(NormalizePath(path), normalized, StringComparison.OrdinalIgnoreCase))
        ?? normalized;

    private static string ResolvePythonRelativeModule(string targetDir, string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || !spec.StartsWith('.'))
        {
            return string.Empty;
        }

        var dots = spec.TakeWhile(ch => ch == '.').Count();
        var remainder = spec[dots..];
        var parts = NormalizePath(targetDir)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        for (var i = 1; i < dots; i++)
        {
            if (parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }
        }

        if (!string.IsNullOrWhiteSpace(remainder))
        {
            parts.AddRange(remainder.Split('.', StringSplitOptions.RemoveEmptyEntries));
        }

        return string.Join('/', parts);
    }

    private static string CombineRelative(string directory, string relativeSpec)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(directory))
        {
            parts.AddRange(NormalizePath(directory).Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in NormalizePath(relativeSpec).Split('/', StringSplitOptions.RemoveEmptyEntries))
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

    private static string NormalizePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    private static string Bound(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    private static string? ExtractPythonContractExcerpt(string source, int maxChars)
    {
        var selected = new List<string>();
        var used = 0;
        foreach (var raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.TrimStart();
            if (!trimmed.StartsWith("class ", StringComparison.Ordinal) &&
                !trimmed.StartsWith("def ", StringComparison.Ordinal) &&
                !trimmed.StartsWith("async def ", StringComparison.Ordinal))
            {
                continue;
            }

            var signature = trimmed.TrimEnd().TrimEnd(':');
            var addition = selected.Count == 0 ? signature : "\n" + signature;
            if (used + addition.Length > maxChars)
            {
                break;
            }

            selected.Add(signature);
            used += addition.Length;
        }

        return selected.Count == 0 ? null : string.Join('\n', selected);
    }
}
