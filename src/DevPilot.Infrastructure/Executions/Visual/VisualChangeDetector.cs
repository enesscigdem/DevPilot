namespace DevPilot.Infrastructure.Executions.Visual;

/// <summary>Decides whether a set of changed files can alter what a user sees.</summary>
public static class VisualChangeDetector
{
    private static readonly HashSet<string> UiExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tsx", ".jsx", ".css", ".scss", ".sass", ".less", ".html", ".vue", ".svelte"
    };

    public static bool HasUiChanges(IEnumerable<string> changedFiles) => changedFiles.Any(IsUiFile);

    public static bool IsUiFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("node_modules/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("__tests__/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(".spec.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return UiExtensions.Contains(Path.GetExtension(normalized));
    }
}
