using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.DeveloperAgent;

public sealed class WorktreeEditApplier : IWorktreeEditApplier
{
    private static readonly string[] SensitiveFileNameExact =
    {
        ".env",
        "id_rsa",
        "id_rsa.pub",
        "id_ed25519",
        "id_ecdsa",
        "secrets.json",
        "credentials.json"
    };

    private static readonly string[] SensitiveExtensions =
    {
        ".pem",
        ".key",
        ".pfx",
        ".p12",
        ".crt",
        ".cer",
        ".der",
        ".kdbx"
    };

    private static readonly UTF8Encoding Utf8WithoutBom =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithBom =
        new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);

    private readonly ILogger<WorktreeEditApplier> _logger;

    public WorktreeEditApplier(ILogger<WorktreeEditApplier> logger)
    {
        _logger = logger;
    }

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    private static string DecodeUtf8Text(byte[] bytes, out bool hasBom)
    {
        hasBom = HasUtf8Bom(bytes);
        int offset = hasBom ? 3 : 0;
        var text = Utf8WithoutBom.GetString(bytes, offset, bytes.Length - offset);
        if (text.StartsWith('\uFEFF'))
        {
            text = text.Substring(1);
        }
        return text;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadContextFilesAsync(
        string workspacePath,
        string branchName,
        IReadOnlyList<string> filePaths,
        ContextLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= ContextLimits.Default;

        await VerifyWorkspaceAndGitBranchAsync(workspacePath, branchName, cancellationToken).ConfigureAwait(false);

        if (filePaths.Count > limits.MaxFileCount)
        {
            throw new InvalidOperationException(
                $"Requested file count ({filePaths.Count}) exceeds maximum context limit of {limits.MaxFileCount}.");
        }

        var result = new Dictionary<string, string>();
        long totalContentBytes = 0;

        foreach (var rawRelativePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(rawRelativePath))
            {
                continue;
            }

            var resolvedPath = ValidateAndResolvePath(workspacePath, rawRelativePath);

            if (!File.Exists(resolvedPath))
            {
                _logger.LogWarning("Context file does not exist and will be skipped: '{RawPath}'.", rawRelativePath);
                continue;
            }

            var fileInfo = new FileInfo(resolvedPath);
            if (fileInfo.Length > limits.MaxFileSizeBytes)
            {
                _logger.LogWarning("Context file '{RawPath}' size ({Size} bytes) exceeds limit ({Limit} bytes) and will be skipped.", rawRelativePath, fileInfo.Length, limits.MaxFileSizeBytes);
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read context file '{RawPath}'. Skipping.", rawRelativePath);
                continue;
            }

            if (IsBinaryContent(bytes))
            {
                _logger.LogWarning("Context file '{RawPath}' contains binary content and will be skipped.", rawRelativePath);
                continue;
            }

            if (totalContentBytes + bytes.Length > limits.MaxTotalContentSizeBytes)
            {
                _logger.LogWarning("Context file '{RawPath}' exceeds total context size limit ({Limit} bytes). Stopping context loading.", rawRelativePath, limits.MaxTotalContentSizeBytes);
                break;
            }

            totalContentBytes += bytes.Length;
            var content = DecodeUtf8Text(bytes, out _);
            result[rawRelativePath] = content;
        }

        if (result.Count == 0 && filePaths.Count > 0)
        {
            _logger.LogWarning("No valid context files could be loaded from the {Count} requested paths. Continuing with empty context.", filePaths.Count);
        }

        return result;
    }

    public async Task<DeveloperAgentResult> ApplyEditsAsync(
        string workspacePath,
        string branchName,
        StructuredEditPlan editPlan,
        CancellationToken cancellationToken = default)
    {
        if (editPlan == null || editPlan.Files == null || editPlan.Files.Count == 0)
        {
            return DeveloperAgentResult.Fail("Structured edit plan is empty.");
        }

        // 1. Verify initial Git workspace state & branch
        try
        {
            await VerifyWorkspaceAndGitBranchAsync(workspacePath, branchName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return DeveloperAgentResult.Fail($"Workspace verification failed: {ex.Message}");
        }

        // Capture initial Git commit hash to ensure no commits occur
        var initialCommitHash = await GetCurrentCommitHashAsync(workspacePath, cancellationToken).ConfigureAwait(false);

        // 2. Validate all paths & in-memory edits before writing
        var preparedCreates = new List<(string ResolvedPath, string RelativePath, string Content)>();
        var preparedModifies = new List<(string ResolvedPath, string RelativePath, string NewContent, string OriginalContent, bool HasBom)>();
        var modifiedRelativePaths = new List<string>();

        foreach (var spec in editPlan.Files)
        {
            if (string.IsNullOrWhiteSpace(spec.FilePath))
            {
                return DeveloperAgentResult.Fail("File edit spec contains an empty filePath.");
            }

            string resolvedPath;
            try
            {
                resolvedPath = ValidateAndResolvePath(workspacePath, spec.FilePath);
            }
            catch (Exception ex)
            {
                return DeveloperAgentResult.Fail($"Path safety violation for '{spec.FilePath}': {ex.Message}");
            }

            switch (spec.Action)
            {
                case FileEditAction.Create:
                    if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
                    {
                        return DeveloperAgentResult.Fail(
                            $"Strict Create action failed: file already exists at '{spec.FilePath}'.");
                    }

                    if (spec.NewContent == null)
                    {
                        return DeveloperAgentResult.Fail(
                            $"Strict Create action failed: newContent was null for '{spec.FilePath}'.");
                    }

                    if (spec.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        var projectRoots = DiscoverProjectRoots(workspacePath);
                        if (!IsCsFileInProjectRoot(spec.FilePath, projectRoots))
                        {
                            var rootsFormatted = projectRoots.Count > 0
                                ? string.Join(", ", projectRoots.Select(r => string.IsNullOrEmpty(r) ? "." : r))
                                : "none";

                            return DeveloperAgentResult.Fail(
                                $"Target path safety violation: Created C# file '{spec.FilePath}' is outside all discovered .NET project roots ({rootsFormatted}). C# files must be created within an existing .NET project directory.");
                        }
                    }

                    preparedCreates.Add((resolvedPath, spec.FilePath, spec.NewContent));
                    modifiedRelativePaths.Add(spec.FilePath);
                    break;

                case FileEditAction.Modify:
                    if (!File.Exists(resolvedPath))
                    {
                        return DeveloperAgentResult.Fail(
                            $"Strict Modify action failed: target file does not exist at '{spec.FilePath}'.");
                    }

                    if (spec.SearchReplaceEdits == null || spec.SearchReplaceEdits.Count == 0)
                    {
                        return DeveloperAgentResult.Fail(
                            $"Strict Modify action failed: searchReplaceEdits list was empty for '{spec.FilePath}'.");
                    }

                    var originalBytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
                    if (IsBinaryContent(originalBytes))
                    {
                        return DeveloperAgentResult.Fail(
                            $"Target file '{spec.FilePath}' is a binary file and cannot be modified.");
                    }

                    var evolvingContent = DecodeUtf8Text(originalBytes, out var hasBom);
                    var originalContent = evolvingContent;

                    // Apply search/replace edits sequentially in memory
                    foreach (var edit in spec.SearchReplaceEdits)
                    {
                        if (edit.Search == null)
                        {
                            return DeveloperAgentResult.Fail(
                                $"Search/replace edit for '{spec.FilePath}' has null search string.");
                        }

                        var matchCount = CountOccurrences(evolvingContent, edit.Search);
                        if (matchCount == 0)
                        {
                            return DeveloperAgentResult.Fail(
                                $"Missing search match in '{spec.FilePath}'. Search text was not found.");
                        }

                        if (matchCount > 1)
                        {
                            return DeveloperAgentResult.Fail(
                                $"Ambiguous multiple search matches ({matchCount}) in '{spec.FilePath}'. Search text must match exactly once.");
                        }

                        // Exact single match found - perform replacement
                        evolvingContent = ReplaceFirstOccurrence(evolvingContent, edit.Search, edit.Replace ?? string.Empty);
                    }

                    preparedModifies.Add((resolvedPath, spec.FilePath, evolvingContent, originalContent, hasBom));
                    modifiedRelativePaths.Add(spec.FilePath);
                    break;

                default:
                    return DeveloperAgentResult.Fail($"Unsupported edit action '{spec.Action}' for '{spec.FilePath}'.");
            }
        }

        // 3. Disk application phase with Rollback Safety
        var backupDir = Path.Combine(Path.GetTempPath(), "DevPilot_Backup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDir);

        var createdDiskPaths = new List<string>();
        var modifiedDiskPaths = new List<(string ResolvedPath, string BackupPath)>();

        try
        {
            // Create backups for modified files
            foreach (var mod in preparedModifies)
            {
                var backupPath = Path.Combine(backupDir, Guid.NewGuid().ToString("N"));
                File.Copy(mod.ResolvedPath, backupPath, overwrite: true);
                modifiedDiskPaths.Add((mod.ResolvedPath, backupPath));
            }

            // Write Creates
            foreach (var create in preparedCreates)
            {
                var parentDir = Path.GetDirectoryName(create.ResolvedPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                await File.WriteAllTextAsync(create.ResolvedPath, create.Content, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
                createdDiskPaths.Add(create.ResolvedPath);
            }

            // Write Modifies
            foreach (var mod in preparedModifies)
            {
                await File.WriteAllTextAsync(mod.ResolvedPath, mod.NewContent, mod.HasBom ? Utf8WithBom : Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing files during edit application. Executing rollback.");

            // Rollback newly created files
            foreach (var createdPath in createdDiskPaths)
            {
                try
                {
                    if (File.Exists(createdPath))
                    {
                        File.Delete(createdPath);
                    }
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogWarning(rollbackEx, "Failed to delete created file '{Path}' during rollback.", createdPath);
                }
            }

            // Rollback modified files from backup
            foreach (var (modPath, backupPath) in modifiedDiskPaths)
            {
                try
                {
                    if (File.Exists(backupPath))
                    {
                        File.Copy(backupPath, modPath, overwrite: true);
                    }
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogWarning(rollbackEx, "Failed to restore modified file '{Path}' from backup during rollback.", modPath);
                }
            }

            CleanupDirectory(backupDir);

            return DeveloperAgentResult.Fail($"File write failed and all changes were rolled back. Error: {ex.Message}");
        }
        finally
        {
            CleanupDirectory(backupDir);
        }

        // 4. Post-application Git workspace verification
        try
        {
            await VerifyWorkspaceAndGitBranchAsync(workspacePath, branchName, cancellationToken).ConfigureAwait(false);
            var finalCommitHash = await GetCurrentCommitHashAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            if (initialCommitHash != finalCommitHash)
            {
                return DeveloperAgentResult.Fail("Git safety violation: Commit hash changed during edit application.");
            }
        }
        catch (Exception ex)
        {
            return DeveloperAgentResult.Fail($"Post-edit Git safety check failed: {ex.Message}");
        }

        return DeveloperAgentResult.Ok(modifiedRelativePaths);
    }

    public static string ValidateAndResolvePath(string workspacePath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new ArgumentException("Workspace path cannot be empty.", nameof(workspacePath));
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Target relative path cannot be empty.", nameof(relativePath));
        }

        relativePath = relativePath.Trim();

        // Trim leading root slashes for repository-root relative paths (e.g. "/src/File.cs" or "\src\File.cs")
        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            relativePath = relativePath.TrimStart('/', '\\');
        }

        var canonicalWorkspace = GetCanonicalRealPath(workspacePath);

        // If a drive-rooted absolute path is provided that resides within the execution workspace, convert it to a relative path
        if (Path.IsPathRooted(relativePath))
        {
            var fullPathCandidate = Path.GetFullPath(relativePath);
            var canonicalCandidate = GetCanonicalRealPath(fullPathCandidate);
            if (IsSubPath(canonicalWorkspace, canonicalCandidate))
            {
                relativePath = Path.GetRelativePath(canonicalWorkspace, canonicalCandidate);
            }
            else
            {
                throw new InvalidOperationException($"Absolute paths are rejected (outside workspace): '{relativePath}'.");
            }
        }

        // Reject .git segment anywhere in relative path
        var segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Modification of .git directory or files is rejected: '{relativePath}'.");
            }
        }

        // Reject sensitive files and credentials
        var fileName = Path.GetFileName(relativePath);
        if (SensitiveFileNameExact.Any(s => fileName.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
            fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            SensitiveExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Access to sensitive configuration/credential file is rejected: '{relativePath}'.");
        }

        var combinedPath = Path.Combine(canonicalWorkspace, relativePath);
        var canonicalTarget = GetCanonicalRealPath(combinedPath);

        // Symlink / Traversal check: Canonical target path must remain inside Canonical workspace path
        if (!IsSubPath(canonicalWorkspace, canonicalTarget))
        {
            throw new InvalidOperationException(
                $"Path safety violation: '{relativePath}' resolves outside the allowed execution workspace.");
        }

        return canonicalTarget;
    }

    public static string GetCanonicalRealPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            try
            {
                FileSystemInfo info = File.Exists(fullPath) ? new FileInfo(fullPath) : new DirectoryInfo(fullPath);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    fullPath = target.FullName;
                }
            }
            catch (Exception)
            {
                // Fall back to fullPath if link target resolution fails
            }
        }

        // Check parent directory components for symlinks
        var current = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
        while (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current || Path.GetPathRoot(current) == current)
            {
                break;
            }

            try
            {
                var dirInfo = new DirectoryInfo(current);
                var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    var relative = Path.GetRelativePath(current, fullPath);
                    fullPath = Path.GetFullPath(Path.Combine(target.FullName, relative));
                    current = target.FullName;
                    parent = Path.GetDirectoryName(current);
                }
            }
            catch (Exception)
            {
                // Ignore symlink resolution failures on individual parent directories
            }

            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return Path.GetFullPath(fullPath);
    }

    private static bool IsSubPath(string basePath, string candidatePath)
    {
        var normBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normCand = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normCand.Equals(normBase, StringComparison.OrdinalIgnoreCase) ||
               normCand.StartsWith(normBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBinaryContent(byte[] bytes)
    {
        // Check for null byte \0 in first 8KB of content
        var inspectLength = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < inspectLength; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static int CountOccurrences(string source, string search)
    {
        if (string.IsNullOrEmpty(search)) return 0;
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string ReplaceFirstOccurrence(string source, string search, string replace)
    {
        int index = source.IndexOf(search, StringComparison.Ordinal);
        if (index < 0) return source;
        return source.Substring(0, index) + replace + source.Substring(index + search.Length);
    }

    private static async Task VerifyWorkspaceAndGitBranchAsync(
        string workspacePath,
        string expectedBranchName,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workspacePath))
        {
            throw new InvalidOperationException($"Workspace directory does not exist at '{workspacePath}'.");
        }

        var (branchOk, currentBranch, branchError) = await RunGitCommandAsync(workspacePath, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (!branchOk || currentBranch?.Trim() != expectedBranchName)
        {
            throw new InvalidOperationException(
                $"Git workspace branch mismatch. Expected '{expectedBranchName}', actual '{currentBranch?.Trim()}'. Error: {branchError}");
        }
    }

    private static async Task<string> GetCurrentCommitHashAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var (ok, hash, _) = await RunGitCommandAsync(workspacePath, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        return ok ? hash?.Trim() ?? string.Empty : string.Empty;
    }

    private static async Task<(bool Success, string? StdOut, string? StdErr)> RunGitCommandAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return (false, null, $"Git executable not found: {ex.Message}");
        }

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (process.ExitCode == 0, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }

    private static readonly string[] ExcludedDirectoryNames =
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".dotnet_home", ".pnpm-store"
    };

    public static List<string> SafeFindFiles(string rootPath, string searchPattern)
    {
        var results = new List<string>();
        var canonicalRoot = GetCanonicalRealPath(rootPath);

        void Recurse(string currentDir)
        {
            var dirName = Path.GetFileName(currentDir);
            if (ExcludedDirectoryNames.Any(e => e.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var canonicalCurrentDir = GetCanonicalRealPath(currentDir);
            if (!IsSubPath(canonicalRoot, canonicalCurrentDir))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(currentDir, searchPattern))
            {
                var canonicalFile = GetCanonicalRealPath(file);
                if (IsSubPath(canonicalRoot, canonicalFile))
                {
                    results.Add(canonicalFile);
                }
            }

            foreach (var subDir in Directory.GetDirectories(currentDir))
            {
                Recurse(subDir);
            }
        }

        Recurse(canonicalRoot);
        return results;
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
                        if (pkgInclude.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                            pkgInclude.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
                            pkgInclude.Contains("mstest", StringComparison.OrdinalIgnoreCase) ||
                            pkgInclude.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
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
                        var resolvedFull = Path.GetFullPath(Path.Combine(projDir, include));
                        var relRef = Path.GetRelativePath(canonicalWorkspace, resolvedFull).Replace('\\', '/');
                        if (!references.Contains(relRef, StringComparer.OrdinalIgnoreCase))
                        {
                            references.Add(relRef);
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
                ProjectReferences = references
            });
        }

        return nodes.OrderBy(n => n.ProjectPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

public sealed class DiscoveredProjectNode
{
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectDirectory { get; set; } = string.Empty;
    public bool IsTestProject { get; set; }
    public List<string> ProjectReferences { get; set; } = new();
}
