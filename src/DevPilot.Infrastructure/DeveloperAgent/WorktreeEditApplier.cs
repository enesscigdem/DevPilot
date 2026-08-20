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
    public const int SmallFileMaxLines = 100;
    public const int SmallFileMaxChars = 4000;

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

    public static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    public static string DecodeUtf8Text(byte[] bytes, out bool hasBom)
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

    public static bool IsSmallTextFile(string content)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        return content.Length <= SmallFileMaxChars &&
               content.Count(character => character == '\n') + 1 <= SmallFileMaxLines;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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

                    var originalBytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
                    if (IsBinaryContent(originalBytes))
                    {
                        return DeveloperAgentResult.Fail(
                            $"Target file '{spec.FilePath}' is a binary file and cannot be modified.");
                    }

                    var originalContent = DecodeUtf8Text(originalBytes, out var hasBom);

                    var usesFullFileReplacement = spec.NewContent != null;
                    var hasSearchReplaceEdits = spec.SearchReplaceEdits is { Count: > 0 };
                    if (usesFullFileReplacement == hasSearchReplaceEdits)
                    {
                        return DeveloperAgentResult.Fail(
                            $"Strict Modify action failed: provide exactly one edit representation for '{spec.FilePath}'.");
                    }

                    if (usesFullFileReplacement && !IsSmallTextFile(originalContent))
                    {
                        return DeveloperAgentResult.Fail(
                            $"Strict Modify action failed: full-file replacement is limited to small text files for '{spec.FilePath}'.");
                    }

                    if (usesFullFileReplacement && string.IsNullOrEmpty(spec.TargetContentHash))
                    {
                        return DeveloperAgentResult.Fail(
                            $"Strict Modify action failed: small-file replacement requires a target content hash for '{spec.FilePath}'.");
                    }

                    if (!string.IsNullOrEmpty(spec.TargetContentHash))
                    {
                        var currentDiskHash = ComputeContentHash(originalContent);
                        if (!string.Equals(spec.TargetContentHash, currentDiskHash, StringComparison.Ordinal))
                        {
                            return DeveloperAgentResult.Fail(
                                $"Target file '{spec.FilePath}' has changed since edit generation (stale target snapshot hash mismatch).");
                        }
                    }

                    string modifiedContent;
                    if (usesFullFileReplacement)
                    {
                        var normalizedReplacement = NormalizeLineEndings(spec.NewContent!);
                        modifiedContent = originalContent.Contains("\r\n", StringComparison.Ordinal)
                            ? normalizedReplacement.Replace("\n", "\r\n", StringComparison.Ordinal)
                            : normalizedReplacement;
                    }
                    else
                    {
                        var appResult = ValidateAndApplySearchReplaceEdits(originalContent, spec.SearchReplaceEdits, spec.FilePath);
                        if (!appResult.Success)
                        {
                            return DeveloperAgentResult.Fail(appResult.ErrorMessage!);
                        }

                        modifiedContent = appResult.ModifiedContent!;
                    }

                    preparedModifies.Add((resolvedPath, spec.FilePath, modifiedContent, originalContent, hasBom));
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

    private static bool IsWindowsDriveRooted(string path)
    {
        return path.Length >= 2 &&
               char.IsAsciiLetter(path[0]) &&
               path[1] == ':' &&
               (path.Length == 2 || path[2] == '\\' || path[2] == '/');
    }

    private static bool IsUncPath(string path)
    {
        return path.Length >= 2 && ((path[0] == '\\' && path[1] == '\\') || (path[0] == '/' && path[1] == '/'));
    }

    public static string NormalizeAndValidateRelativePath(string relativePath)
    {
        // Normalize both \ and / to /
        var normalized = relativePath.Replace('\\', '/');

        // Trim leading single slash if present (from \src or /src repository-root relative paths)
        if (normalized.StartsWith('/'))
        {
            normalized = normalized.TrimStart('/');
        }

        var rawSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resolvedSegments = new List<string>();

        foreach (var segment in rawSegments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolvedSegments.Count == 0)
                {
                    throw new InvalidOperationException($"Path safety violation: '{relativePath}' resolves outside the allowed execution workspace.");
                }
                resolvedSegments.RemoveAt(resolvedSegments.Count - 1);
                continue;
            }

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Modification of .git directory or files is rejected: '{relativePath}'.");
            }

            resolvedSegments.Add(segment);
        }

        if (resolvedSegments.Count == 0)
        {
            throw new InvalidOperationException($"Path safety violation: '{relativePath}' resolves outside the allowed execution workspace.");
        }

        var fileName = resolvedSegments[^1];
        if (SensitiveFileNameExact.Any(s => fileName.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
            fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            SensitiveExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Access to sensitive configuration/credential file is rejected: '{relativePath}'.");
        }

        return string.Join('/', resolvedSegments);
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

        var canonicalWorkspace = GetCanonicalRealPath(workspacePath);

        // 1. Reject all absolute / UNC paths unconditionally
        if (IsWindowsDriveRooted(relativePath) || IsUncPath(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException($"Absolute paths are rejected: '{relativePath}'.");
        }

        // 2. Logical relative path normalization & traversal validation
        var logicalRelative = NormalizeAndValidateRelativePath(relativePath);

        // 3. Convert to host filesystem path
        var hostRelative = logicalRelative.Replace('/', Path.DirectorySeparatorChar);
        var combinedPath = Path.Combine(canonicalWorkspace, hostRelative);
        var canonicalTarget = GetCanonicalRealPath(combinedPath);

        // 4. Containment / symlink breakout check
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

    public static bool IsSubPath(string basePath, string candidatePath)
    {
        var normBase = Path.GetFullPath(basePath);
        var normCand = Path.GetFullPath(candidatePath);

        normBase = normBase.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        normCand = normCand.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (normCand.Equals(normBase, comparison))
        {
            return true;
        }

        var baseWithSep = normBase.EndsWith(Path.DirectorySeparatorChar)
            ? normBase
            : normBase + Path.DirectorySeparatorChar;

        return normCand.StartsWith(baseWithSep, comparison);
    }

    public static bool IsBinaryContent(byte[] bytes)
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

    public static string NormalizeLineEndings(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    public static string ComputeContentHash(string? content)
    {
        if (content == null) return string.Empty;
        var normalized = NormalizeLineEndings(content);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hashBytes = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static string FormatSearchPreview(string? search, int maxLength = 120)
    {
        if (string.IsNullOrEmpty(search)) return "(empty)";
        var singleLine = search.Replace("\r", " ").Replace("\n", " ");
        while (singleLine.Contains("  "))
        {
            singleLine = singleLine.Replace("  ", " ");
        }
        singleLine = singleLine.Trim();
        if (singleLine.Length > maxLength)
        {
            return $"\"{singleLine.Substring(0, maxLength)}...\"";
        }
        return $"\"{singleLine}\"";
    }

    public static string? ExtractSurroundingContext(string content, string search, int maxContextLines = 10)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var contentLines = content.Split('\n');
        if (contentLines.Length <= maxContextLines)
        {
            return content.Trim();
        }

        var searchLines = search.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int matchedLineIndex = -1;
        foreach (var sLine in searchLines)
        {
            if (sLine.Length < 4) continue;
            for (int i = 0; i < contentLines.Length; i++)
            {
                if (contentLines[i].Contains(sLine, StringComparison.Ordinal))
                {
                    matchedLineIndex = i;
                    break;
                }
            }
            if (matchedLineIndex != -1) break;
        }

        int startLine = 0;
        if (matchedLineIndex != -1)
        {
            startLine = Math.Max(0, matchedLineIndex - maxContextLines / 2);
        }

        int endLine = Math.Min(contentLines.Length, startLine + maxContextLines);
        var excerpt = string.Join('\n', contentLines.Skip(startLine).Take(endLine - startLine));
        return excerpt.Trim();
    }

    public static EditApplicabilityResult ValidateAndApplySearchReplaceEdits(
        string originalContent,
        IReadOnlyList<SearchReplaceEdit>? edits,
        string filePath)
    {
        if (edits == null || edits.Count == 0)
        {
            return EditApplicabilityResult.Fail(
                $"Strict Modify action failed: searchReplaceEdits list was empty for '{filePath}'.",
                failedEditIndex: 0,
                totalEdits: 0);
        }

        bool hadCrLf = originalContent.Contains("\r\n");
        var evolvingContent = NormalizeLineEndings(originalContent);
        int totalEdits = edits.Count;

        for (int i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            int blockIndex = i + 1;

            if (edit == null || edit.Search == null)
            {
                return EditApplicabilityResult.Fail(
                    $"Search/replace edit for '{filePath}' (block {blockIndex}/{totalEdits}) has null search string.",
                    failedEditIndex: blockIndex,
                    totalEdits: totalEdits);
            }

            if (string.IsNullOrEmpty(edit.Search))
            {
                return EditApplicabilityResult.Fail(
                    $"Modify action for '{filePath}' (block {blockIndex}/{totalEdits}) contains empty search string.",
                    failedEditIndex: blockIndex,
                    totalEdits: totalEdits);
            }

            if (edit.Replace == null)
            {
                return EditApplicabilityResult.Fail(
                    $"Modify action for '{filePath}' (block {blockIndex}/{totalEdits}) contains null replace string.",
                    failedEditIndex: blockIndex,
                    totalEdits: totalEdits,
                    failedSearch: edit.Search);
            }

            var normalizedSearch = NormalizeLineEndings(edit.Search);
            var normalizedReplace = NormalizeLineEndings(edit.Replace);

            var matchCount = CountOccurrences(evolvingContent, normalizedSearch);
            if (matchCount == 0)
            {
                var preview = FormatSearchPreview(normalizedSearch);
                var surrounding = ExtractSurroundingContext(evolvingContent, normalizedSearch);
                var errorMsg = $"Missing search match in '{filePath}':\n" +
                    $"- Edit block: {blockIndex}/{totalEdits}\n" +
                    $"- Reason: search matched 0 times (zero matches)\n" +
                    $"- Failed SEARCH preview: {preview}";

                return EditApplicabilityResult.Fail(
                    errorMsg,
                    failedEditIndex: blockIndex,
                    totalEdits: totalEdits,
                    failedSearch: edit.Search,
                    failedReplace: edit.Replace,
                    matchCount: 0,
                    surroundingContext: surrounding);
            }

            if (matchCount > 1)
            {
                var preview = FormatSearchPreview(normalizedSearch);
                var errorMsg = $"Ambiguous multiple search matches ({matchCount}) in '{filePath}':\n" +
                    $"- Edit block: {blockIndex}/{totalEdits}\n" +
                    $"- Reason: search matched {matchCount} times (multiple matches)\n" +
                    $"- Failed SEARCH preview: {preview}";

                return EditApplicabilityResult.Fail(
                    errorMsg,
                    failedEditIndex: blockIndex,
                    totalEdits: totalEdits,
                    failedSearch: edit.Search,
                    failedReplace: edit.Replace,
                    matchCount: matchCount);
            }

            evolvingContent = ReplaceFirstOccurrence(evolvingContent, normalizedSearch, normalizedReplace);
        }

        var finalContent = hadCrLf
            ? evolvingContent.Replace("\n", "\r\n")
            : evolvingContent;

        return EditApplicabilityResult.Ok(finalContent, totalEdits);
    }

    public static (bool Success, string? ErrorMessage, string? ModifiedContent) TryApplySearchReplaceEdits(
        string originalContent,
        IReadOnlyList<SearchReplaceEdit>? edits,
        string filePath)
    {
        var result = ValidateAndApplySearchReplaceEdits(originalContent, edits, filePath);
        return (result.Success, result.ErrorMessage, result.ModifiedContent);
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

    public static List<string> DiscoverProjectRoots(string workspacePath) =>
        ProjectGraphHelper.DiscoverProjectRoots(workspacePath);

    public static bool IsCsFileInProjectRoot(string relativeFilePath, IReadOnlyList<string> projectRoots) =>
        ProjectGraphHelper.IsCsFileInProjectRoot(relativeFilePath, projectRoots);

    public static bool IsTestFileCandidate(string relativeFilePath) =>
        ProjectGraphHelper.IsTestFileCandidate(relativeFilePath);

    public static bool TryRemapTestFileToSingleTestProject(
        string relativeFilePath,
        IReadOnlyList<DiscoveredProjectNode> projectGraph,
        out string remappedPath,
        out string? failureReason) =>
        ProjectGraphHelper.TryRemapTestFileToSingleTestProject(relativeFilePath, projectGraph, out remappedPath, out failureReason);

    public static bool TryResolveModifyTarget(
        string relativeFilePath,
        string workspacePath,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph,
        IReadOnlyList<string>? projectRoots,
        out string resolvedRelativePath,
        out string? failureReason) =>
        ProjectGraphHelper.TryResolveModifyTarget(relativeFilePath, workspacePath, projectGraph, projectRoots, out resolvedRelativePath, out failureReason);

    public static List<DiscoveredProjectNode> DiscoverProjectGraph(string workspacePath) =>
        ProjectGraphHelper.DiscoverProjectGraph(workspacePath);

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
