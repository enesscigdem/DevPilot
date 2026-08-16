using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
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

    private readonly ILogger<WorktreeEditApplier> _logger;

    public WorktreeEditApplier(ILogger<WorktreeEditApplier> logger)
    {
        _logger = logger;
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

        foreach (var relativePath in filePaths)
        {
            var resolvedPath = ValidateAndResolvePath(workspacePath, relativePath);

            if (!File.Exists(resolvedPath))
            {
                _logger.LogWarning("Context file does not exist and will be skipped: '{RelativePath}'.", relativePath);
                continue;
            }

            var fileInfo = new FileInfo(resolvedPath);
            if (fileInfo.Length > limits.MaxFileSizeBytes)
            {
                throw new InvalidOperationException(
                    $"File '{relativePath}' size ({fileInfo.Length} bytes) exceeds maximum context file size limit of {limits.MaxFileSizeBytes} bytes.");
            }

            var bytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
            if (IsBinaryContent(bytes))
            {
                throw new InvalidOperationException($"File '{relativePath}' contains binary content and cannot be loaded into context.");
            }

            totalContentBytes += bytes.Length;
            if (totalContentBytes > limits.MaxTotalContentSizeBytes)
            {
                throw new InvalidOperationException(
                    $"Total context file size ({totalContentBytes} bytes) exceeds maximum limit of {limits.MaxTotalContentSizeBytes} bytes.");
            }

            var content = System.Text.Encoding.UTF8.GetString(bytes);
            result[relativePath] = content;
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("No valid context files could be loaded from the requested paths.");
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
        var preparedModifies = new List<(string ResolvedPath, string RelativePath, string NewContent, string OriginalContent)>();
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

                    var evolvingContent = System.Text.Encoding.UTF8.GetString(originalBytes);
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

                    preparedModifies.Add((resolvedPath, spec.FilePath, evolvingContent, originalContent));
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

                await File.WriteAllTextAsync(create.ResolvedPath, create.Content, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                createdDiskPaths.Add(create.ResolvedPath);
            }

            // Write Modifies
            foreach (var mod in preparedModifies)
            {
                await File.WriteAllTextAsync(mod.ResolvedPath, mod.NewContent, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
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

        // Reject absolute paths
        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new InvalidOperationException($"Absolute paths are rejected: '{relativePath}'.");
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

        var canonicalWorkspace = GetCanonicalRealPath(workspacePath);
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
            FileSystemInfo info = File.Exists(fullPath) ? new FileInfo(fullPath) : new DirectoryInfo(fullPath);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target != null)
            {
                fullPath = target.FullName;
            }
        }

        // Check parent directory components for symlinks
        var current = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
        while (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            var dirInfo = new DirectoryInfo(current);
            var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (target != null)
            {
                var relative = Path.GetRelativePath(current, fullPath);
                fullPath = Path.GetFullPath(Path.Combine(target.FullName, relative));
                current = target.FullName;
            }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
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
