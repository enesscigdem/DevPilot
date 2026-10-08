using System.Text.RegularExpressions;

namespace DevPilot.Infrastructure.DeveloperAgent;

/// <summary>
/// Last resort for an edit whose SEARCH anchors never matched: the model returns the whole file instead of a patch.
/// A whole-file answer can silently drop code, so it is only trusted when it passes these checks.
/// </summary>
public static class FullFileFallbackGuard
{
    /// <summary>Files larger than this are not rewritten whole; the output would not fit a normal budget and truncation is likely.</summary>
    public const int MaxOriginalChars = 24_000;

    private const double MinShrinkRatio = 0.6;
    private const int ShrinkCheckMinChars = 1_500;

    private static readonly Regex Elision = new(
        @"(?:\.\.\.|…)\s*(?:rest of|remaining|existing|unchanged|previous|same as)|(?://|#|/\*|\{/\*)\s*(?:\.\.\.|…|rest of (?:the )?(?:file|code|component)|existing code|unchanged|remaining code)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsEligible(string? original) =>
        !string.IsNullOrWhiteSpace(original) && original.Length <= MaxOriginalChars;

    /// <summary>Returns null when the rewrite is acceptable, otherwise the reason it is not.</summary>
    public static string? Reject(string original, string? rewritten)
    {
        if (string.IsNullOrWhiteSpace(rewritten))
        {
            return "The rewritten file is empty.";
        }

        if (string.Equals(Normalize(original), Normalize(rewritten), StringComparison.Ordinal))
        {
            return "The rewritten file is identical to the current file.";
        }

        // Placeholders such as "// ... rest of the file" mean the model skipped code it was told to keep.
        var added = Elision.Matches(rewritten).Count - Elision.Matches(original).Count;
        if (added > 0)
        {
            return "The rewritten file contains an elision placeholder instead of the full code.";
        }

        if (original.Length >= ShrinkCheckMinChars && rewritten.Length < original.Length * MinShrinkRatio)
        {
            return $"The rewritten file is {rewritten.Length} chars against {original.Length} originally; code was probably dropped.";
        }

        return null;
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
}
