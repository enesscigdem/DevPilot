using System.Collections.Frozen;
using System.Text;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ProjectBrain.Entities;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.ProjectBrain;

public sealed class RepositoryChunker : IRepositoryChunker
{
    private const int MaxFileSizeBytes = 1024 * 1024;

    private static readonly FrozenSet<string> ExcludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".vscode", ".idea", "dist", "build", "out",
        "Migrations", "generated", "packages", "TestResults", "coverage", "artifacts",
    }.ToFrozenSet();

    private static readonly FrozenSet<string> ExcludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".a", ".lib",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".ico",
        ".pdf", ".zip", ".tar", ".gz", ".rar", ".7z",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".mp3", ".mp4", ".avi", ".mov", ".wav",
        ".g.cs", ".g.i.cs", ".Designer.cs", ".generated.cs",
        ".min.js", ".min.css", ".map",
        ".lock", ".cache", ".DS_Store", ".gitignore",
    }.ToFrozenSet();

    private static readonly FrozenSet<string> SensitiveFileNameTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "secret", "secrets", "credential", "credentials", "token", "tokens", "apikey", "api-key", "api_key",
        "password", "passwd", "private", ".env", "appsettings.Development", "appsettings.Local",
        "appsettings.Staging", "appsettings.Production", "connectionstring", "connection-string", "connection_string",
        "key", "keys", "cert", "certificate", ".pem", ".pfx", ".p12", ".cer", ".crt",
    }.ToFrozenSet();

    private static readonly FrozenSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".json", ".tsx", ".ts", ".js", ".md",
    }.ToFrozenSet();

    private static readonly FrozenSet<string> SafeJsonFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "tsconfig.json", "tsconfig.app.json", "tsconfig.node.json", "tsconfig.build.json",
        "jsconfig.json", "global.json", ".eslintrc.json", ".eslintrc", ".prettierrc.json", ".prettierrc",
        ".babelrc.json", ".babelrc", ".stylelintrc.json", ".stylelintrc", "manifest.json", "project.json",
    }.ToFrozenSet();

    private readonly ILogger<RepositoryChunker> _logger;

    public RepositoryChunker(ILogger<RepositoryChunker> logger)
    {
        _logger = logger;
    }


    public Task<IReadOnlyList<CodeChunk>> ChunkRepositoryAsync(
        ChunkMetadata metadata,
        RepositoryAnalysisResult? analysisResult,
        CancellationToken cancellationToken = default)
    {
        if (metadata is null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        var workspacePath = metadata.WorkspacePath;
        if (!Directory.Exists(workspacePath))
        {
            throw new DirectoryNotFoundException($"Workspace path does not exist: {workspacePath}");
        }

        var filePaths = EnumerateSourceFiles(workspacePath, cancellationToken).ToList();
        var projectDirectoryMap = BuildProjectDirectoryMap(analysisResult);
        var symbolMap = BuildSymbolMap(analysisResult);

        var chunks = new List<CodeChunk>();

        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(workspacePath, filePath);
            var normalizedRelative = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            var extension = Path.GetExtension(filePath);
            var language = DetectLanguage(extension);
            var projectName = ResolveProjectName(filePath, projectDirectoryMap, extension);
            var symbolInfo = symbolMap.GetValueOrDefault(NormalizePath(filePath));

            try
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > MaxFileSizeBytes)
                {
                    _logger.LogInformation(
                        "Skipping large file {RelativePath} ({Size} bytes)",
                        normalizedRelative,
                        fileInfo.Length);
                    continue;
                }

                var content = File.ReadAllText(filePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var fileChunks = ChunkContent(content);
                for (int i = 0; i < fileChunks.Count; i++)
                {
                    var (startLine, endLine, chunkContent) = fileChunks[i];
                    var chunk = new CodeChunk
                    {
                        Id = Guid.NewGuid(),
                        WorkspacePath = workspacePath,
                        WorkspaceName = metadata.WorkspaceName,
                        ProjectName = projectName,
                        FilePath = filePath,
                        RelativePath = normalizedRelative,
                        Language = language,
                        SymbolName = symbolInfo?.SymbolName,
                        TypeName = symbolInfo?.TypeName,
                        MethodName = symbolInfo?.MethodName,
                        DeclaredSymbols = symbolInfo?.DeclaredSymbols ?? string.Empty,
                        ChunkOrder = i,
                        StartLine = startLine,
                        EndLine = endLine,
                        Content = chunkContent,
                        ContentHash = ContentHash.Compute(chunkContent),
                        TokenCount = EstimateTokenCount(chunkContent),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };

                    chunks.Add(chunk);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to chunk file {RelativePath}", normalizedRelative);
            }
        }

        _logger.LogInformation(
            "Chunked workspace {WorkspacePath} into {ChunkCount} chunks from {FileCount} files",
            workspacePath,
            chunks.Count,
            filePaths.Count);

        return Task.FromResult<IReadOnlyList<CodeChunk>>(chunks);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string workspacePath, CancellationToken cancellationToken)
    {
        var directories = new Queue<string>();
        directories.Enqueue(workspacePath);

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = directories.Dequeue();

            foreach (var file in TryGetFiles(current))
            {
                if (IsSupportedFile(file))
                {
                    yield return file;
                }
            }

            foreach (var subDirectory in TryGetDirectories(current))
            {
                if (IsExcludedDirectory(subDirectory))
                {
                    continue;
                }

                directories.Enqueue(subDirectory);
            }
        }
    }

    private static string[] TryGetFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string[] TryGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsExcludedDirectory(string directoryPath)
    {
        var name = Path.GetFileName(directoryPath);
        return string.IsNullOrWhiteSpace(name) || ExcludedDirectories.Contains(name);
    }

    private static bool IsSupportedFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (SensitiveFileNameTokens.Any(token => fileName.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);

        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".cache", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(extension) && ExcludedExtensions.Contains(extension))
        {
            return false;
        }

        if (!SupportedExtensions.Contains(extension))
        {
            return false;
        }

        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) && !IsSafeJsonFile(fileName))
        {
            return false;
        }

        return true;
    }

    private static bool IsSafeJsonFile(string fileName)
    {
        return SafeJsonFileNames.Contains(fileName) ||
            fileName.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("jsconfig", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".eslintrc", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".prettierrc", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".babelrc", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".stylelintrc", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectLanguage(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".csproj" => "xml",
            ".sln" or ".slnx" => "solution",
            ".json" => "json",
            ".tsx" => "tsx",
            ".ts" => "typescript",
            ".js" => "javascript",
            ".md" => "markdown",
            _ => "text",
        };
    }

    private static string ResolveProjectName(
        string filePath,
        IReadOnlyDictionary<string, string> projectDirectoryMap,
        string extension)
    {
        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(filePath);
        }

        var normalizedFile = NormalizePath(filePath);
        foreach (var (projectDir, projectName) in projectDirectoryMap)
        {
            if (normalizedFile.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
            {
                return projectName;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyDictionary<string, string> BuildProjectDirectoryMap(RepositoryAnalysisResult? analysisResult)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (analysisResult is null)
        {
            return map;
        }

        foreach (var solution in analysisResult.Solutions)
        {
            foreach (var project in solution.Projects)
            {
                var directory = Path.GetDirectoryName(project.Path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    map[NormalizePath(directory)] = project.Name;
                }
            }
        }

        foreach (var project in analysisResult.StandaloneProjects)
        {
            var directory = Path.GetDirectoryName(project.Path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                map[NormalizePath(directory)] = project.Name;
            }
        }

        return map;
    }


    private static IReadOnlyDictionary<string, SymbolInfo> BuildSymbolMap(RepositoryAnalysisResult? analysisResult)
    {
        var map = new Dictionary<string, SymbolInfo>(StringComparer.OrdinalIgnoreCase);
        if (analysisResult is null)
        {
            return map;
        }

        var allTypes = analysisResult.Solutions
            .SelectMany(s => s.Projects)
            .SelectMany(p => p.Classes.Concat(p.Interfaces).Concat(p.Records).Concat(p.Controllers))
            .Concat(analysisResult.StandaloneProjects
                .SelectMany(p => p.Classes.Concat(p.Interfaces).Concat(p.Records).Concat(p.Controllers)))
            .ToList();

        foreach (var type in allTypes)
        {
            var normalizedPath = NormalizePath(type.SourcePath);
            if (!map.TryGetValue(normalizedPath, out var info))
            {
                info = new SymbolInfo();
                map[normalizedPath] = info;
            }

            if (string.IsNullOrWhiteSpace(info.TypeName))
            {
                info.TypeName = type.Name;
            }

            if (string.IsNullOrWhiteSpace(info.SymbolName))
            {
                info.SymbolName = type.Name;
            }

            var declaredNames = new List<string> { type.Name };
            declaredNames.AddRange(type.Methods.Select(m => m.Name));
            declaredNames.AddRange(type.Constructors.Select(m => m.Name));
            declaredNames.AddRange(type.Properties.Select(p => p.Name));
            info.DeclaredSymbols = string.Join(", ", declaredNames.Distinct(StringComparer.OrdinalIgnoreCase));

            var methodNames = type.Methods
                .Select(m => m.Name)
                .Concat(type.Constructors.Select(m => m.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (methodNames.Count > 0)
            {
                info.MethodName = string.Join(", ", methodNames);
            }
        }

        return map;
    }

    private static List<(int StartLine, int EndLine, string Content)> ChunkContent(string content)
    {
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var chunks = new List<(int, int, string)>();
        var currentLines = new List<string>();
        int startLine = 1;

        for (int i = 0; i < lines.Length; i++)
        {
            currentLines.Add(lines[i]);
            var chunkContent = string.Join(Environment.NewLine, currentLines);
            var isLastLine = i == lines.Length - 1;

            bool shouldFlush = currentLines.Count >= ProjectBrainConstants.MaxChunkLines ||
                chunkContent.Length >= ProjectBrainConstants.MaxChunkCharacters ||
                isLastLine;

            if (shouldFlush)
            {
                chunks.Add((startLine, i + 1, chunkContent));
                currentLines.Clear();
                startLine = i + 2;
            }
        }

        if (currentLines.Count > 0)
        {
            chunks.Add((startLine, lines.Length, string.Join(Environment.NewLine, currentLines)));
        }

        return chunks;
    }

    private static int EstimateTokenCount(string content)
    {
        return Math.Max(1, content.Length / 4);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
    }

    private sealed class SymbolInfo
    {
        public string? SymbolName { get; set; }
        public string? TypeName { get; set; }
        public string? MethodName { get; set; }
        public string DeclaredSymbols { get; set; } = string.Empty;
    }
}

