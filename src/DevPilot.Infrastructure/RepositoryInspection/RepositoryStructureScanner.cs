using System.Text.Json;
using System.Xml.Linq;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.RepositoryInspection;

public sealed class RepositoryStructureScanner : IRepositoryStructureScanner
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "build",
        ".idea",
        "coverage",
        "generated",
    };

    private readonly ILogger<RepositoryStructureScanner> _logger;

    public RepositoryStructureScanner(ILogger<RepositoryStructureScanner> logger)
    {
        _logger = logger;
    }

    public Task<List<WorkspaceFileNodeDto>> ScanStructureAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var result = new List<WorkspaceFileNodeDto>();

        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return Task.FromResult(result);
        }

        try
        {
            var rootDir = new DirectoryInfo(repositoryPath);
            result = ScanDirectory(rootDir, rootDir.FullName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan repository directory structure at {Path}", repositoryPath);
        }

        return Task.FromResult(result);
    }

    private List<WorkspaceFileNodeDto> ScanDirectory(
        DirectoryInfo directory,
        string rootFullPath,
        CancellationToken cancellationToken,
        int currentDepth = 0,
        int maxDepth = 8)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = new List<WorkspaceFileNodeDto>();

        if (currentDepth > maxDepth)
        {
            return nodes;
        }

        DirectoryInfo[] subDirs;
        FileInfo[] files;

        try
        {
            subDirs = directory.GetDirectories();
            files = directory.GetFiles();
        }
        catch
        {
            return nodes;
        }

        // Folders first
        foreach (var subDir in subDirs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ExcludedDirectoryNames.Contains(subDir.Name))
            {
                continue;
            }

            var relPath = GetRelativePath(rootFullPath, subDir.FullName);
            var children = ScanDirectory(subDir, rootFullPath, cancellationToken, currentDepth + 1, maxDepth);

            nodes.Add(new WorkspaceFileNodeDto
            {
                Name = subDir.Name,
                Path = relPath,
                Type = "folder",
                Children = children,
            });
        }

        // Files
        foreach (var file in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ext = file.Extension.ToLowerInvariant();
            var relPath = GetRelativePath(rootFullPath, file.FullName);

            nodes.Add(new WorkspaceFileNodeDto
            {
                Name = file.Name,
                Path = relPath,
                Type = "file",
                Lang = MapLanguage(ext),
            });
        }

        return nodes;
    }

    public Task<int> CountProjectFilesAsync(
        string projectFilePath,
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
        {
            return Task.FromResult(0);
        }

        try
        {
            var projectDir = Path.GetDirectoryName(projectFilePath);
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            {
                return Task.FromResult(0);
            }

            var count = CountSourceFilesInDirectory(new DirectoryInfo(projectDir), cancellationToken);
            return Task.FromResult(count);
        }
        catch
        {
            return Task.FromResult(0);
        }
    }

    private int CountSourceFilesInDirectory(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = 0;

        DirectoryInfo[] subDirs;
        FileInfo[] files;

        try
        {
            subDirs = directory.GetDirectories();
            files = directory.GetFiles();
        }
        catch
        {
            return 0;
        }

        count += files.Count(f =>
        {
            var ext = f.Extension.ToLowerInvariant();
            return ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".json" or ".sql" or ".html" or ".css";
        });

        foreach (var subDir in subDirs)
        {
            if (ExcludedDirectoryNames.Contains(subDir.Name))
            {
                continue;
            }

            count += CountSourceFilesInDirectory(subDir, cancellationToken);
        }

        return count;
    }

    public Task<List<WorkspaceTechnologyDto>> DetectTechnologiesAsync(
        string repositoryPath,
        RepositoryAnalysisResult? roslynResult,
        CancellationToken cancellationToken = default)
    {
        var technologies = new Dictionary<string, WorkspaceTechnologyDto>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return Task.FromResult(technologies.Values.ToList());
        }

        try
        {
            // 1. Scan .csproj files in repository
            var csprojFiles = Directory.GetFiles(repositoryPath, "*.csproj", SearchOption.AllDirectories);
            foreach (var csproj in csprojFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsUnderExcludedDir(csproj, repositoryPath))
                {
                    continue;
                }

                InspectCsproj(csproj, technologies);
            }

            // 2. Scan package.json files in repository
            var packageJsonFiles = Directory.GetFiles(repositoryPath, "package.json", SearchOption.AllDirectories);
            foreach (var packageJson in packageJsonFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsUnderExcludedDir(packageJson, repositoryPath))
                {
                    continue;
                }

                InspectPackageJson(packageJson, technologies);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect technologies in {Path}", repositoryPath);
        }

        // Ordered list with consistent categorization
        var ordered = technologies.Values
            .OrderBy(t => GetTechnologySortOrder(t.Kind))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(ordered);
    }

    private static void InspectCsproj(string csprojPath, Dictionary<string, WorkspaceTechnologyDto> techMap)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var root = doc.Root;
            var sdk = root?.Attribute("Sdk")?.Value ?? string.Empty;

            // Target framework
            var targetFramework = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value;

            if (string.IsNullOrWhiteSpace(targetFramework))
            {
                var tfms = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;
                targetFramework = tfms?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            }

            if (!string.IsNullOrWhiteSpace(targetFramework))
            {
                var version = ExtractDotNetVersion(targetFramework);
                AddOrUpdateTech(techMap, ".NET", version, "runtime");

                if (sdk.Contains(".Web", StringComparison.OrdinalIgnoreCase))
                {
                    AddOrUpdateTech(techMap, "ASP.NET Core", version, "framework");
                }
            }

            // Package References
            foreach (var pkg in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var name = pkg.Attribute("Include")?.Value ?? pkg.Attribute("Update")?.Value ?? string.Empty;
                var version = pkg.Attribute("Version")?.Value ?? pkg.Element(pkg.Name.Namespace + "Version")?.Value;
                version = CleanVersion(version);

                MapDotNetPackage(name, version, techMap);
            }
        }
        catch
        {
            // Ignore corrupted project files
        }
    }

    private static void MapDotNetPackage(string packageName, string? version, Dictionary<string, WorkspaceTechnologyDto> techMap)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return;

        if (packageName.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "ASP.NET Core", version, "framework");
        }
        else if (packageName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "Entity Framework Core", version, "orm");
        }
        else if (packageName.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "PostgreSQL", version, "database");
        }
        else if (packageName.StartsWith("MediatR", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "MediatR", version, "library");
        }
        else if (packageName.StartsWith("FluentValidation", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "FluentValidation", version, "library");
        }
        else if (packageName.StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "xUnit", version, "testing");
        }
        else if (packageName.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "NUnit", version, "testing");
        }
        else if (packageName.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "MSTest", version, "testing");
        }
        else if (packageName.StartsWith("Hangfire", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "Hangfire", version, "library");
        }
        else if (packageName.StartsWith("Serilog", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "Serilog", version, "library");
        }
        else if (packageName.StartsWith("Dapper", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "Dapper", version, "orm");
        }
        else if (packageName.StartsWith("AutoMapper", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "AutoMapper", version, "library");
        }
        else if (packageName.StartsWith("FluentAssertions", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "FluentAssertions", version, "testing");
        }
        else if (packageName.StartsWith("Moq", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "Moq", version, "testing");
        }
        else if (packageName.StartsWith("StackExchange.Redis", StringComparison.OrdinalIgnoreCase))
        {
            AddOrUpdateTech(techMap, "Redis", version, "database");
        }
    }

    private static void InspectPackageJson(string packageJsonPath, Dictionary<string, WorkspaceTechnologyDto> techMap)
    {
        try
        {
            var content = File.ReadAllText(packageJsonPath);
            using var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("dependencies", out var deps))
            {
                ProcessNpmDependencies(deps, techMap);
            }

            if (root.TryGetProperty("devDependencies", out var devDeps))
            {
                ProcessNpmDependencies(devDeps, techMap);
            }
        }
        catch
        {
            // Ignore malformed package.json
        }
    }

    private static void ProcessNpmDependencies(JsonElement depsElement, Dictionary<string, WorkspaceTechnologyDto> techMap)
    {
        foreach (var prop in depsElement.EnumerateObject())
        {
            var name = prop.Name;
            var rawVersion = prop.Value.GetString();
            var version = CleanVersion(rawVersion);

            if (name.Equals("react", StringComparison.OrdinalIgnoreCase))
            {
                AddOrUpdateTech(techMap, "React", version, "frontend");
            }
            else if (name.Equals("typescript", StringComparison.OrdinalIgnoreCase))
            {
                AddOrUpdateTech(techMap, "TypeScript", version, "frontend");
            }
            else if (name.Equals("vue", StringComparison.OrdinalIgnoreCase))
            {
                AddOrUpdateTech(techMap, "Vue", version, "frontend");
            }
            else if (name.Equals("next", StringComparison.OrdinalIgnoreCase))
            {
                AddOrUpdateTech(techMap, "Next.js", version, "framework");
            }
            else if (name.Equals("vite", StringComparison.OrdinalIgnoreCase))
            {
                AddOrUpdateTech(techMap, "Vite", version, "tooling");
            }
            else if (name.Equals("tailwindcss", StringComparison.OrdinalIgnoreCase))
            {
                AddOrUpdateTech(techMap, "Tailwind CSS", version, "styling");
            }
            else if (name.Equals("vitest", StringComparison.OrdinalIgnoreCase))
            {
                AddOrUpdateTech(techMap, "Vitest", version, "testing");
            }
            else if (name.Equals("jest", StringComparison.OrdinalIgnoreCase))
            {
                AddOrUpdateTech(techMap, "Jest", version, "testing");
            }
        }
    }

    private static void AddOrUpdateTech(
        Dictionary<string, WorkspaceTechnologyDto> techMap,
        string name,
        string? version,
        string kind)
    {
        if (techMap.TryGetValue(name, out var existing))
        {
            if (string.IsNullOrWhiteSpace(existing.Version) && !string.IsNullOrWhiteSpace(version))
            {
                existing.Version = version;
            }
        }
        else
        {
            techMap[name] = new WorkspaceTechnologyDto
            {
                Name = name,
                Version = string.IsNullOrWhiteSpace(version) ? null : version,
                Kind = kind,
            };
        }
    }

    private static string? ExtractDotNetVersion(string targetFramework)
    {
        var clean = targetFramework.Trim();
        if (clean.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            var ver = clean[3..];
            if (ver.Contains('-'))
            {
                ver = ver.Split('-')[0];
            }
            return ver;
        }
        return clean;
    }

    private static string? CleanVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        var trimmed = rawVersion.Trim().TrimStart('^', '~', 'v', '=', '>', '<');
        var parts = trimmed.Split('-');
        return parts[0].Trim();
    }

    private static string? MapLanguage(string extension)
    {
        return extension switch
        {
            ".cs" => "cs",
            ".ts" => "ts",
            ".tsx" => "tsx",
            ".js" => "js",
            ".jsx" => "jsx",
            ".json" => "json",
            ".sql" => "sql",
            ".md" => "md",
            ".html" => "html",
            ".css" => "css",
            ".xml" => "xml",
            ".csproj" => "xml",
            ".yml" => "yaml",
            ".yaml" => "yaml",
            _ => null,
        };
    }

    private static int GetTechnologySortOrder(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "runtime" => 1,
            "framework" => 2,
            "orm" => 3,
            "database" => 4,
            "frontend" => 5,
            "styling" => 6,
            "library" => 7,
            "tooling" => 8,
            "testing" => 9,
            _ => 10,
        };
    }

    private static bool IsUnderExcludedDir(string filePath, string repositoryRoot)
    {
        var rel = Path.GetRelativePath(repositoryRoot, filePath);
        var segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => ExcludedDirectoryNames.Contains(s));
    }

    private static string GetRelativePath(string rootPath, string fullPath)
    {
        var rel = Path.GetRelativePath(rootPath, fullPath);
        return rel.Replace('\\', '/');
    }
}
