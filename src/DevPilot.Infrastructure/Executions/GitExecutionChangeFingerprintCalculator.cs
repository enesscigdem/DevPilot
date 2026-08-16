using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class GitExecutionChangeFingerprintCalculator : IExecutionChangeFingerprintCalculator
{
    private static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger<GitExecutionChangeFingerprintCalculator> _logger;

    public GitExecutionChangeFingerprintCalculator(ILogger<GitExecutionChangeFingerprintCalculator> logger)
    {
        _logger = logger;
    }

    public async Task<ExecutionFingerprintResult> ComputeFingerprintAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return new ExecutionFingerprintResult(false, ErrorMessage: $"Workspace path does not exist: '{workspacePath}'.");
        }

        var fullWorkspacePath = Path.GetFullPath(workspacePath);

        // 1. Obtain current HEAD SHA
        var headCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        if (!headCmd.IsSuccess || string.IsNullOrWhiteSpace(headCmd.StdOut))
        {
            return new ExecutionFingerprintResult(false, ErrorMessage: $"Failed to get HEAD commit SHA: {headCmd.StdErr}");
        }
        var baseHeadSha = headCmd.StdOut.Trim();

        // 2. Discover changes relative to HEAD via git status
        var statusCmd = await RunGitCommandAsync(
            fullWorkspacePath,
            cancellationToken,
            "-c", "diff.renames=true",
            "-c", "status.renames=true",
            "status",
            "--porcelain=v1",
            "-z",
            "--untracked-files=all").ConfigureAwait(false);

        if (!statusCmd.IsSuccess)
        {
            return new ExecutionFingerprintResult(false, ErrorMessage: $"Failed to run git status: {statusCmd.StdErr}");
        }

        var statusEntries = ParseStatusPorcelainZ(statusCmd.RawBytes);
        if (statusEntries.Count == 0)
        {
            var emptyFingerprint = CanonicalFingerprintBuilder.BuildFingerprint(baseHeadSha, Array.Empty<CanonicalEntry>());
            return new ExecutionFingerprintResult(
                Success: true,
                Fingerprint: emptyFingerprint,
                BaseHeadSha: baseHeadSha,
                HasSensitiveFiles: false,
                ChangedFileCount: 0);
        }

        var canonicalEntries = new List<CanonicalEntry>();
        var hasSensitiveFiles = false;

        foreach (var entry in statusEntries)
        {
            if (ExecutionSensitivePathClassifier.IsSensitivePath(entry.Path) ||
                (!string.IsNullOrEmpty(entry.OldPath) && ExecutionSensitivePathClassifier.IsSensitivePath(entry.OldPath)))
            {
                hasSensitiveFiles = true;
                break;
            }

            var absoluteFilePath = Path.Combine(fullWorkspacePath, entry.Path);
            string fileMode = "100644";
            string contentHash = "";

            if (entry.ChangeType == "D")
            {
                contentHash = "";
            }
            else if (File.Exists(absoluteFilePath) || Directory.Exists(absoluteFilePath))
            {
                try
                {
                    var fileInfo = new FileInfo(absoluteFilePath);

                    // Check for symlink
                    if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        fileMode = "120000";
                        var rawLinkTarget = fileInfo.LinkTarget ?? GetSymlinkTargetFallback(absoluteFilePath);
                        if (ExecutionSensitivePathClassifier.IsSensitivePath(rawLinkTarget))
                        {
                            hasSensitiveFiles = true;
                            break;
                        }
                        contentHash = ComputeSha256String(Encoding.UTF8.GetBytes(rawLinkTarget));
                    }
                    else if (fileInfo.Exists)
                    {
                        // Executable check on Unix / Git
                        if (IsExecutable(absoluteFilePath))
                        {
                            fileMode = "100755";
                        }

                        contentHash = await ComputeGitBlobShaAsync(fullWorkspacePath, entry.Path, absoluteFilePath, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read file details for path '{Path}'", entry.Path);
                }
            }

            canonicalEntries.Add(new CanonicalEntry(
                ChangeType: entry.ChangeType,
                FileMode: fileMode,
                OldPath: entry.OldPath,
                Path: entry.Path,
                ContentHash: contentHash));
        }

        if (hasSensitiveFiles)
        {
            return new ExecutionFingerprintResult(
                Success: true,
                BaseHeadSha: baseHeadSha,
                HasSensitiveFiles: true,
                ChangedFileCount: statusEntries.Count,
                ErrorMessage: "Workspace contains sensitive files.");
        }

        var fingerprint = CanonicalFingerprintBuilder.BuildFingerprint(baseHeadSha, canonicalEntries);
        return new ExecutionFingerprintResult(
            Success: true,
            Fingerprint: fingerprint,
            BaseHeadSha: baseHeadSha,
            HasSensitiveFiles: false,
            ChangedFileCount: canonicalEntries.Count);
    }

    public async Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(
        string workspacePath,
        string treeSha,
        string baseHeadSha,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return new ExecutionFingerprintResult(false, ErrorMessage: $"Workspace path does not exist: '{workspacePath}'.");
        }

        var fullWorkspacePath = Path.GetFullPath(workspacePath);

        // Run git diff-tree relative to baseHeadSha and candidate treeSha
        var diffCmd = await RunGitCommandAsync(
            fullWorkspacePath,
            cancellationToken,
            "-c", "diff.renames=true",
            "diff-tree",
            "--no-commit-id",
            "-r",
            "-z",
            "-M50%",
            baseHeadSha,
            treeSha).ConfigureAwait(false);

        if (!diffCmd.IsSuccess)
        {
            return new ExecutionFingerprintResult(false, ErrorMessage: $"Failed to run git diff-tree: {diffCmd.StdErr}");
        }

        var diffEntries = ParseDiffTreeZ(diffCmd.RawBytes);
        var canonicalEntries = new List<CanonicalEntry>();
        var hasSensitiveFiles = false;

        foreach (var entry in diffEntries)
        {
            if (ExecutionSensitivePathClassifier.IsSensitivePath(entry.Path) ||
                (!string.IsNullOrEmpty(entry.OldPath) && ExecutionSensitivePathClassifier.IsSensitivePath(entry.OldPath)))
            {
                hasSensitiveFiles = true;
                break;
            }

            string contentHash = "";
            if (entry.ChangeType != "D" && !string.IsNullOrEmpty(entry.BlobSha))
            {
                // Retrieve blob hash or content hash from git cat-file / blob sha
                contentHash = entry.BlobSha;
            }

            canonicalEntries.Add(new CanonicalEntry(
                ChangeType: entry.ChangeType,
                FileMode: entry.FileMode,
                OldPath: entry.OldPath,
                Path: entry.Path,
                ContentHash: contentHash));
        }

        if (hasSensitiveFiles)
        {
            return new ExecutionFingerprintResult(
                Success: true,
                BaseHeadSha: baseHeadSha,
                HasSensitiveFiles: true,
                ChangedFileCount: diffEntries.Count,
                ErrorMessage: "Staged tree contains sensitive files.");
        }

        var fingerprint = CanonicalFingerprintBuilder.BuildFingerprint(baseHeadSha, canonicalEntries);
        return new ExecutionFingerprintResult(
            Success: true,
            Fingerprint: fingerprint,
            BaseHeadSha: baseHeadSha,
            HasSensitiveFiles: false,
            ChangedFileCount: canonicalEntries.Count);
    }

    private static bool IsExecutable(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var mode = File.GetUnixFileMode(filePath);
            return mode.HasFlag(UnixFileMode.UserExecute) ||
                   mode.HasFlag(UnixFileMode.GroupExecute) ||
                   mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch
        {
            return false;
        }
    }

    private static string GetSymlinkTargetFallback(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.LinkTarget ?? path;
        }
        catch
        {
            return path;
        }
    }

    private static async Task<string> ComputeGitBlobShaAsync(
        string workingDirectory,
        string relativePath,
        string absoluteFilePath,
        CancellationToken cancellationToken)
    {
        var cmd = await RunGitCommandAsync(workingDirectory, cancellationToken, "hash-object", relativePath).ConfigureAwait(false);
        if (cmd.IsSuccess && !string.IsNullOrWhiteSpace(cmd.StdOut))
        {
            return cmd.StdOut.Trim();
        }
        throw new InvalidOperationException($"git hash-object failed for path '{relativePath}': {cmd.StdErr}");
    }

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hashBytes);
    }

    private static string ComputeSha256String(byte[] data)
    {
        var hashBytes = SHA256.HashData(data);
        return Convert.ToHexStringLower(hashBytes);
    }

    private static List<ParsedStatusEntry> ParseStatusPorcelainZ(byte[] rawBytes)
    {
        var entries = new List<ParsedStatusEntry>();
        if (rawBytes == null || rawBytes.Length == 0)
        {
            return entries;
        }

        var tokens = SplitByNul(rawBytes);
        var index = 0;

        while (index < tokens.Count)
        {
            var token = tokens[index];
            index++;

            if (token.Length < 3)
            {
                continue;
            }

            var x = token[0];
            var y = token[1];
            var path = token.Substring(3);

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string? oldPath = null;
            if (x == 'R' || y == 'R' || x == 'C' || y == 'C')
            {
                if (index < tokens.Count)
                {
                    oldPath = tokens[index];
                    index++;
                }
            }

            var changeType = NormalizeStatusType(x, y);
            entries.Add(new ParsedStatusEntry(path, oldPath, changeType));
        }

        return entries;
    }

    private static string NormalizeStatusType(char x, char y)
    {
        if (x == 'R' || y == 'R') return "R";
        if (x == 'C' || y == 'C') return "C";
        if (x == '?' && y == '?') return "A";
        if (x == 'A' || y == 'A') return "A";
        if (x == 'D' || y == 'D') return "D";
        if (x == 'M' || y == 'M') return "M";

        return "M";
    }

    private static List<ParsedDiffTreeEntry> ParseDiffTreeZ(byte[] rawBytes)
    {
        var entries = new List<ParsedDiffTreeEntry>();
        if (rawBytes == null || rawBytes.Length == 0)
        {
            return entries;
        }

        var tokens = SplitByNul(rawBytes);
        var i = 0;

        while (i < tokens.Count)
        {
            var header = tokens[i];
            i++;

            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            // diff-tree output line format:
            // :100644 100644 <oldSha> <newSha> M\t<path>
            // or for renames: :100644 100644 <oldSha> <newSha> R100\t<oldPath>\0<newPath>
            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            var fileMode = parts[1].TrimStart(':');
            var newSha = parts[3];
            var statusRaw = parts[4];
            var changeType = statusRaw.Length > 0 ? statusRaw[..1] : "M";

            string? oldPath = null;
            string path = "";

            if (changeType == "R" || changeType == "C")
            {
                if (i < tokens.Count)
                {
                    oldPath = tokens[i];
                    i++;
                }
                if (i < tokens.Count)
                {
                    path = tokens[i];
                    i++;
                }
            }
            else
            {
                if (i < tokens.Count)
                {
                    path = tokens[i];
                    i++;
                }
            }

            if (!string.IsNullOrEmpty(path))
            {
                entries.Add(new ParsedDiffTreeEntry(fileMode, changeType, oldPath, path, newSha));
            }
        }

        return entries;
    }

    private static List<string> SplitByNul(byte[] bytes)
    {
        var result = new List<string>();
        var start = 0;

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0)
            {
                if (i > start)
                {
                    result.Add(Encoding.UTF8.GetString(bytes, start, i - start));
                }
                else
                {
                    result.Add("");
                }
                start = i + 1;
            }
        }

        if (start < bytes.Length)
        {
            result.Add(Encoding.UTF8.GetString(bytes, start, bytes.Length - start));
        }

        return result;
    }

    private static async Task<GitCommandResult> RunGitCommandAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var timeoutCts = new CancellationTokenSource(DefaultGitTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.directory=*");

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
            return new GitCommandResult(false, Array.Empty<byte>(), "", $"Git executable not found: {ex.Message}");
        }

        using var msOut = new MemoryStream();
        var msOutTask = process.StandardOutput.BaseStream.CopyToAsync(msOut, linkedCts.Token);
        var errTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            await Task.WhenAll(msOutTask, errTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort
            }
            return new GitCommandResult(false, Array.Empty<byte>(), "", "Git operation cancelled or timed out.");
        }

        var rawBytes = msOut.ToArray();
        var stdOutText = Encoding.UTF8.GetString(rawBytes);
        var stdErrText = await errTask.ConfigureAwait(false);

        return new GitCommandResult(process.ExitCode == 0, rawBytes, stdOutText, stdErrText ?? "");
    }

    private sealed record ParsedStatusEntry(string Path, string? OldPath, string ChangeType);

    private sealed record ParsedDiffTreeEntry(string FileMode, string ChangeType, string? OldPath, string Path, string BlobSha);

    private sealed record GitCommandResult(bool IsSuccess, byte[] RawBytes, string StdOut, string StdErr);
}

public sealed record CanonicalEntry(
    string ChangeType,
    string FileMode,
    string? OldPath,
    string Path,
    string ContentHash);

public static class CanonicalFingerprintBuilder
{
    public static string BuildFingerprint(string baseHeadSha, IEnumerable<CanonicalEntry> entries)
    {
        var sortedEntries = entries
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // 1. Base HEAD SHA
        writer.Write(baseHeadSha ?? "");

        // 2. Entry count
        writer.Write(sortedEntries.Count);

        // 3. Length-prefixed binary records
        foreach (var entry in sortedEntries)
        {
            writer.Write(entry.ChangeType ?? "");
            writer.Write(entry.FileMode ?? "100644");
            writer.Write(entry.OldPath ?? "");
            writer.Write(entry.Path ?? "");
            writer.Write(entry.ContentHash ?? "");
        }

        writer.Flush();
        ms.Position = 0;

        var hashBytes = SHA256.HashData(ms);
        return $"sha256:{Convert.ToHexStringLower(hashBytes)}";
    }
}
