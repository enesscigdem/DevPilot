using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DevPilot.Application.DeveloperAgent.Models;

namespace DevPilot.Infrastructure.DeveloperAgent;

/// <summary>One model-issued action. Unknown properties are ignored.</summary>
public sealed class AgenticAction
{
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("startLine")] public int? StartLine { get; set; }
    [JsonPropertyName("endLine")] public int? EndLine { get; set; }
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("searchReplaceEdits")] public List<SearchReplaceEdit>? SearchReplaceEdits { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Extracts the first balanced JSON object from a reply (tolerates prose and code fences).</summary>
    public static AgenticAction? TryParse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        var start = reply.IndexOf('{');
        while (start >= 0)
        {
            var end = FindObjectEnd(reply, start);
            if (end > start)
            {
                try
                {
                    var action = JsonSerializer.Deserialize<AgenticAction>(reply.AsSpan(start, end - start + 1), Options);
                    if (!string.IsNullOrWhiteSpace(action?.Tool))
                    {
                        return action;
                    }
                }
                catch (JsonException)
                {
                    // Try the next '{'.
                }
            }

            start = reply.IndexOf('{', start + 1);
        }

        return null;
    }

    private static int FindObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        return -1;
    }
}

/// <summary>
/// The workspace-bound operations the repairing model may use. Every path is resolved against the worktree
/// and rejected when it leaves it or targets generated/vendored/protected files.
/// </summary>
public sealed class AgenticRepairTools
{
    public const int MaxReadChars = 16_000;
    public const int MaxReadLines = 400;
    public const int MaxSearchMatches = 40;
    public const int MaxDirEntries = 120;
    private const long MaxSearchFileBytes = 512 * 1024;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", ".next", "coverage", ".vs", ".idea", "__pycache__", ".venv", "venv"
    };

    private static readonly HashSet<string> ProtectedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "poetry.lock", "Cargo.lock", "packages.lock.json"
    };

    private static readonly HashSet<string> SearchableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".json", ".css", ".scss", ".html", ".md", ".cs", ".csproj", ".sln",
        ".py", ".go", ".rs", ".java", ".kt", ".rb", ".php", ".yml", ".yaml", ".toml", ".xml", ".vue", ".svelte", ".sh", ".txt"
    };

    private readonly string _root;
    private readonly Dictionary<string, string> _originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

    public AgenticRepairTools(string workspacePath)
    {
        _root = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public IReadOnlyList<string> ChangedFiles => _changed.ToList();

    public IReadOnlyDictionary<string, string> OriginalContents => _originals;

    public string Execute(AgenticAction action)
    {
        try
        {
            return action.Tool?.Trim().ToLowerInvariant() switch
            {
                "read_file" => ReadFile(action),
                "list_dir" => ListDir(action),
                "search" => Search(action),
                "edit_file" => EditFile(action),
                "write_file" => WriteFile(action),
                var other => $"ERROR: unknown tool '{other}'. Tools: read_file, list_dir, search, edit_file, write_file, run_checks, done."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private string ReadFile(AgenticAction action)
    {
        if (!TryResolve(action.Path, allowDirectory: false, out var full, out var relative, out var error))
        {
            return error!;
        }

        if (!File.Exists(full))
        {
            return $"ERROR: file '{relative}' does not exist.";
        }

        var lines = File.ReadAllLines(full);
        var start = Math.Max(1, action.StartLine ?? 1);
        var end = Math.Min(lines.Length, action.EndLine ?? (start + MaxReadLines - 1));
        end = Math.Min(end, start + MaxReadLines - 1);
        if (start > lines.Length)
        {
            return $"ERROR: '{relative}' has only {lines.Length} line(s).";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[{relative} · lines {start}-{end} of {lines.Length}]");
        for (var i = start; i <= end; i++)
        {
            if (sb.Length >= MaxReadChars)
            {
                sb.AppendLine($"[truncated at line {i - 1}; call read_file with startLine={i} to continue]");
                break;
            }

            sb.AppendLine(lines[i - 1]);
        }

        return sb.ToString();
    }

    private string ListDir(AgenticAction action)
    {
        var requested = string.IsNullOrWhiteSpace(action.Path) ? "." : action.Path;
        if (!TryResolve(requested, allowDirectory: true, out var full, out var relative, out var error))
        {
            return error!;
        }

        if (!Directory.Exists(full))
        {
            return $"ERROR: directory '{relative}' does not exist.";
        }

        var entries = Directory.EnumerateFileSystemEntries(full)
            .Select(path => (Name: Path.GetFileName(path), IsDir: Directory.Exists(path)))
            .Where(e => !(e.IsDir && SkippedDirectories.Contains(e.Name)))
            .OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDirEntries)
            .Select(e => e.IsDir ? e.Name + "/" : e.Name);
        return $"[{relative}]\n" + string.Join("\n", entries);
    }

    private string Search(AgenticAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Pattern))
        {
            return "ERROR: 'pattern' is required.";
        }

        var scope = string.IsNullOrWhiteSpace(action.Path) ? "." : action.Path;
        if (!TryResolve(scope, allowDirectory: true, out var start, out _, out var error))
        {
            return error!;
        }

        Regex regex;
        try
        {
            regex = new Regex(action.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            regex = new Regex(Regex.Escape(action.Pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        var matches = new List<string>();
        var files = File.Exists(start) ? new[] { start } : EnumerateSearchFiles(start);
        foreach (var file in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                bool hit;
                try { hit = regex.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException) { hit = false; }

                if (!hit)
                {
                    continue;
                }

                var text = lines[i].Trim();
                matches.Add($"{Relative(file)}:{i + 1}: {(text.Length > 200 ? text[..200] + "…" : text)}");
                if (matches.Count >= MaxSearchMatches)
                {
                    matches.Add($"[stopped after {MaxSearchMatches} matches; narrow the pattern or path]");
                    return string.Join("\n", matches);
                }
            }
        }

        return matches.Count == 0 ? "No matches." : string.Join("\n", matches);
    }

    private IEnumerable<string> EnumerateSearchFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> subdirectories;
            IEnumerable<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(current).ToList();
                files = Directory.EnumerateFiles(current).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subdirectories.Where(d => !SkippedDirectories.Contains(Path.GetFileName(d))))
            {
                pending.Push(sub);
            }

            foreach (var file in files)
            {
                if (SearchableExtensions.Contains(Path.GetExtension(file)) && new FileInfo(file).Length <= MaxSearchFileBytes)
                {
                    yield return file;
                }
            }
        }
    }

    private string EditFile(AgenticAction action)
    {
        if (!TryResolve(action.Path, allowDirectory: false, out var full, out var relative, out var error) ||
            !CheckWritable(full, relative, out error))
        {
            return error!;
        }

        if (!File.Exists(full))
        {
            return $"ERROR: '{relative}' does not exist; use write_file to create a new file.";
        }

        var original = File.ReadAllText(full);
        var applied = DevPilot.Infrastructure.DeveloperAgent.WorktreeEditApplier.ValidateAndApplySearchReplaceEdits(
            original,
            action.SearchReplaceEdits,
            relative);
        if (!applied.Success || applied.ModifiedContent == null)
        {
            return "ERROR: edit not applied. " + (applied.ErrorMessage ?? "unknown reason") +
                   " Each 'search' must match the file exactly once; read the file again and use a longer unique excerpt.";
        }

        _originals.TryAdd(relative, original);
        File.WriteAllText(full, applied.ModifiedContent);
        _changed.Add(relative);
        return $"OK: edited '{relative}' ({applied.TotalEdits} edit(s)).";
    }

    private string WriteFile(AgenticAction action)
    {
        if (!TryResolve(action.Path, allowDirectory: false, out var full, out var relative, out var error) ||
            !CheckWritable(full, relative, out error))
        {
            return error!;
        }

        if (action.Content == null)
        {
            return "ERROR: 'content' is required.";
        }

        if (File.Exists(full))
        {
            return $"ERROR: '{relative}' already exists; use edit_file to change it.";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, action.Content);
        _originals.TryAdd(relative, string.Empty);
        _changed.Add(relative);
        return $"OK: created '{relative}'.";
    }

    private bool CheckWritable(string full, string relative, out string? error)
    {
        var name = Path.GetFileName(full);
        if (ProtectedFileNames.Contains(name))
        {
            error = $"ERROR: '{relative}' is a lockfile and must not be edited.";
            return false;
        }

        if (relative.StartsWith(".github/", StringComparison.OrdinalIgnoreCase))
        {
            error = $"ERROR: '{relative}' is CI configuration and must not be edited.";
            return false;
        }

        error = null;
        return true;
    }

    private bool TryResolve(string? path, bool allowDirectory, out string full, out string relative, out string? error)
    {
        full = string.Empty;
        relative = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "ERROR: 'path' is required.";
            return false;
        }

        try
        {
            full = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException)
        {
            error = "ERROR: invalid path.";
            return false;
        }

        var inside = full.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
                     full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside)
        {
            error = "ERROR: path is outside the workspace.";
            return false;
        }

        relative = Relative(full);
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                              s.Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
        {
            error = $"ERROR: '{relative}' is not accessible.";
            return false;
        }

        if (relative.Length == 0)
        {
            relative = ".";
        }

        return true;
    }

    private string Relative(string full)
    {
        var relative = Path.GetRelativePath(_root, full).Replace('\\', '/');
        return relative == "." ? string.Empty : relative;
    }
}
