using System.Text.RegularExpressions;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.Executions;

namespace DevPilot.Infrastructure.DeveloperAgent;

/// <summary>
/// Deterministic, bounded recovery for repository-local missing modules discovered after verification.
/// Never guesses external packages or ambiguous references.
/// </summary>
public static class ManifestGapRecovery
{
    private const int MaxAddedCreateFiles = 2;

    private static readonly Regex TsCannotFindModule = new(
        @"(?:Cannot find module|Could not resolve)\s+'([^']+)'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CsTypeNotFound = new(
        @"error CS0246:\s*The type or namespace name '([^']+)' could not be found",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ExternalPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "express", "react", "react-dom", "next", "vue", "angular", "rxjs", "lodash",
        "axios", "fs", "path", "http", "https", "crypto", "os", "util", "stream",
        "buffer", "events", "child_process", "url", "zlib", "assert", "module",
        "Microsoft.", "System.", "Newtonsoft.", "Npgsql.", "Dapper.", "Serilog.",
        "FluentValidation.", "MediatR.", "AutoMapper.", "Swashbuckle.",
        "org.", "com.", "javax.", "jakarta.", "kotlin.", "android.", "androidx."
    };

    public static IReadOnlyList<ManifestFileEntry> DetectMissingLocalCreates(
        string workspacePath,
        IReadOnlyList<ManifestFileEntry> plannedFiles,
        IReadOnlyList<string> generatedFilePaths,
        IReadOnlyDictionary<string, string> generatedContents,
        IEnumerable<string> diagnosticLines,
        IReadOnlyDictionary<string, string>? existingFileContents = null)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || generatedFilePaths == null || generatedFilePaths.Count == 0)
        {
            return Array.Empty<ManifestFileEntry>();
        }

        var plannedSet = new HashSet<string>(
            plannedFiles.Select(file => DeveloperAgent.NormalizeFocusedRepairPath(file.FilePath)),
            StringComparer.OrdinalIgnoreCase);
        var discovered = new List<ManifestFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var generatedPath in generatedFilePaths)
        {
            if (!generatedContents.TryGetValue(generatedPath, out var content) ||
                string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            foreach (var import in GenerationDependencyAnalyzer.ExtractLocalImportSpecs(content, generatedPath))
            {
                if (!TryResolveDeterministicLocalSource(workspacePath, generatedPath, import, out var resolvedPath) ||
                    plannedSet.Contains(resolvedPath) ||
                    seen.Contains(resolvedPath) ||
                    FileExistsInWorkspace(workspacePath, resolvedPath) ||
                    discovered.Count >= MaxAddedCreateFiles)
                {
                    continue;
                }

                if (!CompilerEvidenceSupportsMissingModule(diagnosticLines, generatedPath, import, resolvedPath))
                {
                    continue;
                }

                seen.Add(resolvedPath);
                discovered.Add(new ManifestFileEntry(
                    resolvedPath,
                    FileEditAction.Create,
                    Purpose: $"Deterministic recovery for missing local import '{import}' referenced by {Path.GetFileName(generatedPath)}."));
            }
        }

        return discovered;
    }

    private static bool CompilerEvidenceSupportsMissingModule(
        IEnumerable<string> diagnosticLines,
        string referencingFile,
        string importSpec,
        string resolvedPath)
    {
        var refFile = Path.GetFileName(referencingFile);
        var resolvedName = Path.GetFileNameWithoutExtension(resolvedPath);
        var importTail = Path.GetFileName(importSpec.TrimStart('.', '/'));

        foreach (var line in diagnosticLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!line.Contains(refFile, StringComparison.OrdinalIgnoreCase) &&
                !line.Contains(resolvedName, StringComparison.OrdinalIgnoreCase) &&
                !line.Contains(importTail, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TsCannotFindModule.IsMatch(line) ||
                CsTypeNotFound.IsMatch(line) ||
                line.Contains("Cannot find module", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Module not found", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error TS2307", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error CS0246", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveDeterministicLocalSource(
        string workspacePath,
        string consumerPath,
        string importSpec,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(importSpec) || IsExternalImport(importSpec))
        {
            return false;
        }

        if (!importSpec.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var consumerDir = GenerationDependencyAnalyzer.ParentDirectoryPublic(consumerPath);
        var combined = GenerationDependencyAnalyzer.ResolveRelativeImportPublic(consumerDir, importSpec);
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        foreach (var candidate in ExpandSourceCandidates(combined))
        {
            var normalized = DeveloperAgent.NormalizeFocusedRepairPath(candidate);
            if (IsExternalImport(normalized) || normalized.Contains('*') || normalized.Contains(':'))
            {
                continue;
            }

            if (HasSourceExtension(normalized) && !FileExistsInWorkspace(workspacePath, normalized))
            {
                resolvedPath = normalized;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ExpandSourceCandidates(string basePath)
    {
        yield return basePath;
        yield return basePath + ".ts";
        yield return basePath + ".tsx";
        yield return basePath + ".js";
        yield return basePath + ".jsx";
        yield return basePath + ".cs";
        yield return basePath + ".py";
        yield return basePath + ".go";
        yield return basePath + ".java";
        yield return basePath + ".kt";
        yield return Path.Combine(basePath, "index.ts").Replace('\\', '/');
        yield return Path.Combine(basePath, "index.js").Replace('\\', '/');
    }

    private static bool IsExternalImport(string spec)
    {
        var trimmed = spec.Trim().TrimStart('.', '/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return true;
        }

        if (trimmed.StartsWith("@", StringComparison.Ordinal) && !trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            return true;
        }

        var head = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? trimmed;
        return ExternalPrefixes.Any(prefix => head.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSourceExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(ext) &&
               GenerationDependencyAnalyzer.SourceExtensionsPublic.Contains(ext);
    }

    private static bool FileExistsInWorkspace(string workspacePath, string relativePath)
    {
        try
        {
            var resolved = WorktreeEditApplier.ValidateAndResolvePath(workspacePath, relativePath);
            return File.Exists(resolved);
        }
        catch
        {
            return false;
        }
    }
}
