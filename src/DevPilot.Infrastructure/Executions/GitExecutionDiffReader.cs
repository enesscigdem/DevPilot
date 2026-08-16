using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

public sealed class GitExecutionDiffReader : IExecutionGitDiffReader
{
    private const int MaxDiffSizeBytes = 512 * 1024; // 512 KiB UTF-8 byte limit
    private static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] SensitiveExactFileNames =
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

    private readonly ILogger<GitExecutionDiffReader> _logger;

    public GitExecutionDiffReader(ILogger<GitExecutionDiffReader> logger)
    {
        _logger = logger;
    }

    public async Task<ExecutionGitDiffResult> ReadWorkspaceDiffAsync(
        string workspacePath,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return new ExecutionGitDiffResult(false, $"Execution workspace directory does not exist: '{workspacePath}'.");
        }

        var fullWorkspacePath = Path.GetFullPath(workspacePath);

        // 1. Run git status --porcelain=v1 -z --untracked-files=all to discover changed paths
        var statusCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all")
            .ConfigureAwait(false);

        if (!statusCmd.IsSuccess)
        {
            return new ExecutionGitDiffResult(false, $"Failed to get Git status: {statusCmd.StdErr}");
        }

        var statusEntries = ParseStatusPorcelainZ(statusCmd.RawBytes);
        if (statusEntries.Count == 0)
        {
            return new ExecutionGitDiffResult(true, ChangedFiles: Array.Empty<ExecutionReviewFileDto>(), DiffText: "", DiffTruncated: false);
        }

        // 2. Fetch git diff --numstat -z HEAD for tracked files to get additions/deletions & binary info
        var numstatCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, "diff", "--numstat", "-z", "HEAD")
            .ConfigureAwait(false);

        var numstatMap = numstatCmd.IsSuccess ? ParseNumstatZ(numstatCmd.RawBytes) : new Dictionary<string, NumstatEntry>();

        var changedFileDtos = new List<ExecutionReviewFileDto>();
        var diffBuilder = new StringBuilder();
        var currentUtf8Bytes = 0;
        var diffTruncated = false;

        foreach (var entry in statusEntries)
        {
            var relativePath = entry.Path;

            // Step A: Sensitive-path classification FIRST (MUST NOT read/diff sensitive files)
            var isSensitive = IsSensitivePath(relativePath);

            // Step B: Metadata & Binary classification
            int? additions = null;
            int? deletions = null;
            var isBinary = false;

            if (numstatMap.TryGetValue(relativePath, out var numstat))
            {
                if (numstat.IsBinary)
                {
                    isBinary = true;
                }
                else
                {
                    additions = numstat.Additions;
                    deletions = numstat.Deletions;
                }
            }
            else if (entry.ChangeType == "Added")
            {
                // Untracked new file on disk
                var absoluteFilePath = Path.Combine(fullWorkspacePath, relativePath);
                if (!isSensitive && File.Exists(absoluteFilePath))
                {
                    try
                    {
                        var fileInfo = new FileInfo(absoluteFilePath);
                        if (fileInfo.Length > 0)
                        {
                            isBinary = IsFileBinary(absoluteFilePath);
                            if (!isBinary)
                            {
                                var lineCount = CountLines(absoluteFilePath);
                                additions = lineCount;
                                deletions = 0;
                            }
                        }
                        else
                        {
                            additions = 0;
                            deletions = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read untracked file metadata for '{Path}'", relativePath);
                    }
                }
            }

            changedFileDtos.Add(new ExecutionReviewFileDto(
                Path: relativePath,
                ChangeType: entry.ChangeType,
                Additions: additions,
                Deletions: deletions));

            // Step C: Diff content generation (ONLY IF safe text)
            string fileDiffText;

            if (isSensitive)
            {
                fileDiffText = $"[Redacted sensitive file content: {relativePath}]\n";
            }
            else if (isBinary)
            {
                fileDiffText = $"[Binary file diff not shown: {relativePath}]\n";
            }
            else if (entry.ChangeType == "Added" && !numstatMap.ContainsKey(relativePath))
            {
                // Untracked safe text file -> generate unified diff in managed C# code
                var absoluteFilePath = Path.Combine(fullWorkspacePath, relativePath);
                fileDiffText = BuildUntrackedFileDiff(relativePath, absoluteFilePath);
            }
            else
            {
                // Tracked safe text file -> git diff HEAD -- <path>
                var diffCmd = await RunGitCommandAsync(fullWorkspacePath, cancellationToken, "diff", "HEAD", "--", relativePath)
                    .ConfigureAwait(false);

                if (diffCmd.IsSuccess && !string.IsNullOrEmpty(diffCmd.StdOut))
                {
                    fileDiffText = diffCmd.StdOut;
                    if (!fileDiffText.EndsWith('\n'))
                    {
                        fileDiffText += "\n";
                    }
                }
                else
                {
                    fileDiffText = "";
                }
            }

            if (!string.IsNullOrEmpty(fileDiffText))
            {
                if (!diffTruncated)
                {
                    var textBytes = Encoding.UTF8.GetByteCount(fileDiffText);
                    if (currentUtf8Bytes + textBytes <= MaxDiffSizeBytes)
                    {
                        diffBuilder.Append(fileDiffText);
                        currentUtf8Bytes += textBytes;
                    }
                    else
                    {
                        var remainingBytes = MaxDiffSizeBytes - currentUtf8Bytes;
                        if (remainingBytes > 0)
                        {
                            var truncatedStr = TruncateUtf8String(fileDiffText, remainingBytes);
                            diffBuilder.Append(truncatedStr);
                            currentUtf8Bytes += Encoding.UTF8.GetByteCount(truncatedStr);
                        }
                        diffTruncated = true;
                    }
                }
            }
        }

        return new ExecutionGitDiffResult(
            Success: true,
            ChangedFiles: changedFileDtos,
            DiffText: diffBuilder.ToString(),
            DiffTruncated: diffTruncated);
    }

    public static bool IsSensitivePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        // Check for .git directory or files
        if (normalized.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(normalized);

        // Exact file name match
        if (SensitiveExactFileNames.Any(s => fileName.Equals(s, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Starts with .env.
        if (fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Sensitive extensions
        if (SensitiveExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static List<StatusEntry> ParseStatusPorcelainZ(byte[] rawBytes)
    {
        var entries = new List<StatusEntry>();
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

            var changeType = DetermineChangeType(x, y);
            entries.Add(new StatusEntry(path, oldPath, changeType));
        }

        return entries;
    }

    private static string DetermineChangeType(char x, char y)
    {
        if (x == 'R' || y == 'R') return "Renamed";
        if (x == 'C' || y == 'C') return "Copied";
        if (x == '?' && y == '?') return "Added";
        if (x == 'A' || y == 'A') return "Added";
        if (x == 'D' || y == 'D') return "Deleted";
        if (x == 'M' || y == 'M') return "Modified";

        return $"{x}{y}".Trim();
    }

    private static Dictionary<string, NumstatEntry> ParseNumstatZ(byte[] rawBytes)
    {
        var result = new Dictionary<string, NumstatEntry>(StringComparer.Ordinal);
        if (rawBytes == null || rawBytes.Length == 0)
        {
            return result;
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

            var parts = header.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var addStr = parts[0].Trim();
            var delStr = parts[1].Trim();

            string filePath;
            if (parts.Length >= 3)
            {
                filePath = parts[2].Trim();
            }
            else
            {
                if (i < tokens.Count)
                {
                    filePath = tokens[i];
                    i++;
                }
                else
                {
                    continue;
                }
            }

            if (addStr == "-" && delStr == "-")
            {
                result[filePath] = new NumstatEntry(IsBinary: true, Additions: null, Deletions: null);
            }
            else
            {
                int.TryParse(addStr, out var add);
                int.TryParse(delStr, out var del);
                result[filePath] = new NumstatEntry(IsBinary: false, Additions: add, Deletions: del);
            }
        }

        return result;
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
                    var token = Encoding.UTF8.GetString(bytes, start, i - start);
                    result.Add(token);
                }
                else if (i == start)
                {
                    // Empty token between NULs
                    result.Add("");
                }
                start = i + 1;
            }
        }

        if (start < bytes.Length)
        {
            var token = Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
            result.Add(token);
        }

        return result;
    }

    private static bool IsFileBinary(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var buffer = new byte[Math.Min(8192, fs.Length)];
            var read = fs.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return true; // Contains NUL byte -> binary
                }
            }
        }
        catch
        {
            // Fallback safety
        }
        return false;
    }

    private static int CountLines(string filePath)
    {
        var count = 0;
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        while (reader.ReadLine() != null)
        {
            count++;
        }
        return count;
    }

    private static string BuildUntrackedFileDiff(string relativePath, string absoluteFilePath)
    {
        if (!File.Exists(absoluteFilePath))
        {
            return "";
        }

        try
        {
            var lines = File.ReadAllLines(absoluteFilePath, Encoding.UTF8);
            var sb = new StringBuilder();
            sb.AppendLine($"--- /dev/null");
            sb.AppendLine($"+++ b/{relativePath}");
            sb.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
            foreach (var line in lines)
            {
                sb.AppendLine($"+{line}");
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string TruncateUtf8String(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || maxBytes <= 0)
        {
            return "";
        }

        var encoding = Encoding.UTF8;
        var bytes = encoding.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return text;
        }

        var len = maxBytes;
        // Move backward if mid-way through a UTF-8 multi-byte char
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
        {
            len--;
        }

        return encoding.GetString(bytes, 0, len);
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
                // Best-effort process kill
            }

            var message = timeoutCts.IsCancellationRequested
                ? "Git process timed out."
                : "Git operation was cancelled.";

            return new GitCommandResult(false, Array.Empty<byte>(), "", message);
        }

        var rawBytes = msOut.ToArray();
        var stdOutText = Encoding.UTF8.GetString(rawBytes);
        var stdErrText = await errTask.ConfigureAwait(false);

        if (stdErrText != null && stdErrText.Length > 500)
        {
            stdErrText = stdErrText[..500].TrimEnd();
        }

        return new GitCommandResult(process.ExitCode == 0, rawBytes, stdOutText, stdErrText ?? "");
    }

    private sealed record StatusEntry(string Path, string? OldPath, string ChangeType);

    private sealed record NumstatEntry(bool IsBinary, int? Additions, int? Deletions);

    private sealed record GitCommandResult(bool IsSuccess, byte[] RawBytes, string StdOut, string StdErr);
}
