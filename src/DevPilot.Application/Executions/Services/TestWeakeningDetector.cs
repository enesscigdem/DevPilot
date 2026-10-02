using System.Text.RegularExpressions;

namespace DevPilot.Application.Executions.Services;

public sealed record TestWeakeningResult(
    bool IsSuspected,
    int RemovedAssertions,
    int AddedSkips,
    int RemovedTests)
{
    public string Describe(string filePath)
    {
        var parts = new List<string>();
        if (RemovedAssertions > 0) parts.Add($"{RemovedAssertions} assertion(s) removed");
        if (AddedSkips > 0) parts.Add($"{AddedSkips} skip/ignore marker(s) added");
        if (RemovedTests > 0) parts.Add($"{RemovedTests} test(s) removed");
        return $"{Path.GetFileName(filePath)}: {string.Join(", ", parts)}";
    }
}

/// <summary>
/// Heuristic guard for the "repair must not weaken tests" rule: compares assertion, skip and test markers
/// of a test file before and after an automated repair. Language-agnostic and intentionally conservative.
/// </summary>
public static class TestWeakeningDetector
{
    private static readonly Regex[] AssertionPatterns =
    {
        new(@"\bAssert\w*\s*\.\s*\w+\s*\(", RegexOptions.Compiled),
        new(@"\.Should\s*\(", RegexOptions.Compiled),
        new(@"\bexpect\s*\(", RegexOptions.Compiled),
        new(@"(?<![\w.])assert(?:Equal|True|False|That|Throws|Raises)?\s*[\(\s]", RegexOptions.Compiled),
        new(@"\bt\.(?:Errorf?|Fatalf?)\s*\(", RegexOptions.Compiled),
    };

    private static readonly Regex[] SkipPatterns =
    {
        new(@"\bSkip\s*=", RegexOptions.Compiled),
        new(@"\[\s*Ignore\b", RegexOptions.Compiled),
        new(@"\.skip\s*\(", RegexOptions.Compiled),
        new(@"\b(?:xit|xdescribe|xtest)\s*\(", RegexOptions.Compiled),
        new(@"@pytest\.mark\.skip", RegexOptions.Compiled),
        new(@"@(?:Ignore|Disabled)\b", RegexOptions.Compiled),
        new(@"\bt\.Skip\w*\s*\(", RegexOptions.Compiled),
    };

    private static readonly Regex[] TestPatterns =
    {
        new(@"\[\s*(?:Fact|Theory|Test|TestMethod)\b", RegexOptions.Compiled),
        new(@"(?<![\w.])(?:it|test)\s*\(", RegexOptions.Compiled),
        new(@"\bdef\s+test_\w+", RegexOptions.Compiled),
        new(@"\bfunc\s+Test\w+\s*\(", RegexOptions.Compiled),
    };

    public static TestWeakeningResult Analyze(string? before, string? after)
    {
        before ??= string.Empty;
        after ??= string.Empty;

        var removedAssertions = Math.Max(0, Count(AssertionPatterns, before) - Count(AssertionPatterns, after));
        var addedSkips = Math.Max(0, Count(SkipPatterns, after) - Count(SkipPatterns, before));
        var removedTests = Math.Max(0, Count(TestPatterns, before) - Count(TestPatterns, after));

        return new TestWeakeningResult(
            removedAssertions > 0 || addedSkips > 0 || removedTests > 0,
            removedAssertions,
            addedSkips,
            removedTests);
    }

    private static int Count(IEnumerable<Regex> patterns, string text) =>
        patterns.Sum(pattern => pattern.Matches(text).Count);
}
