using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Ports;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DevPilot.Infrastructure.Executions;

public sealed record DiagnosticSourceLocation(string FilePath, int? Line = null, int? Column = null);

public sealed record CompilerFailureEvidence(
    string FailureFingerprint,
    IReadOnlyList<string> DiagnosticLines,
    IReadOnlyList<DiagnosticSourceLocation> Locations);

public sealed record TestFailureEvidence(
    string FailureFingerprint,
    string? TestName,
    string ErrorSummary,
    IReadOnlyList<string> RelevantLines,
    IReadOnlyList<DiagnosticSourceLocation> Locations)
{
    public bool HasReliableTestName =>
        !string.IsNullOrWhiteSpace(TestName) &&
        Regex.IsMatch(TestName, @"^[A-Za-z_][A-Za-z0-9_.+`]*$");
}

/// <summary>
/// Extracts bounded, authoritative compiler/test evidence and maps it to the smallest
/// supported repair scope. This is intentionally deterministic and contains no retry state.
/// </summary>
public static class ExecutionDiagnosticEvidence
{
    private const int MaxCompilerDiagnostics = 20;
    private const int MaxTestEvidenceLines = 80;
    private const int MaxTestEvidenceChars = 12000;

    private const string SourceExtensionPattern = @"(?:cs|fs|vb|ts|tsx|js|jsx|py|java|kt|go|rs)";

    private static readonly Regex ParenthesizedDiagnosticRegex = new(
        $@"(?<path>(?:[A-Za-z]:)?[^\r\n]*?\.{SourceExtensionPattern})\((?<line>\d+),(?<column>\d+)\):\s*error\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ColonDiagnosticRegex = new(
        $@"(?<path>(?:[A-Za-z]:)?[^\r\n]*?\.{SourceExtensionPattern}):(?<line>\d+)(?::(?<column>\d+))?:\s*(?:error|fatal)\s*(?<code>[A-Za-z]+\d+)?\s*:?\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FailedTestRegex = new(
        @"^\s*(?:x\s+)?Failed\s+(?<name>.+?)(?:\s+\[[^\]]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StackLocationRegex = new(
        $@"(?:\bin\s+|\bat\s+[^\r\n]*?\()?\s*(?<path>(?:[A-Za-z]:)?[^\r\n()]*?\.{SourceExtensionPattern})(?::line\s+|:)(?<line>\d+)(?::(?<column>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PythonStackLocationRegex = new(
        @"\bFile\s+[""'](?<path>(?:[A-Za-z]:)?[^""'\r\n]+?\.py)[""'],\s*line\s+(?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static CompilerFailureEvidence ParseCompilerFailure(
        string? stdOut,
        string? stdErr,
        string? errorMessage) =>
        ParseVerificationFailure(stdOut, stdErr, errorMessage);

    public static CompilerFailureEvidence ParseVerificationFailure(
        string? stdOut,
        string? stdErr,
        string? errorMessage)
    {
        var lines = JoinOutput(stdOut, stdErr, errorMessage)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => Regex.IsMatch(line, @"\b(?:error|fatal)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompilerDiagnostics)
            .ToList();

        var locations = new List<DiagnosticSourceLocation>();
        var normalized = new List<string>();

        foreach (var line in lines)
        {
            var match = ParenthesizedDiagnosticRegex.Match(line);
            if (!match.Success)
            {
                match = ColonDiagnosticRegex.Match(line);
            }

            if (!match.Success)
            {
                normalized.Add(NormalizeText(line));
                continue;
            }

            var path = NormalizePath(match.Groups["path"].Value);
            int? lineNumber = int.TryParse(match.Groups["line"].Value, out var parsedLine) ? parsedLine : null;
            int? column = int.TryParse(match.Groups["column"].Value, out var parsedColumn) ? parsedColumn : null;
            locations.Add(new DiagnosticSourceLocation(path, lineNumber, column));
            normalized.Add($"{path.ToLowerInvariant()}:{lineNumber}:{column}:{match.Groups["code"].Value.ToUpperInvariant()}:{NormalizeText(match.Groups["message"].Value)}");
        }

        if (normalized.Count == 0)
        {
            normalized.Add(NormalizeText(errorMessage ?? "build failed"));
        }

        return new CompilerFailureEvidence(
            ComputeFingerprint(normalized),
            lines,
            locations);
    }

    public static TestFailureEvidence ParseTestFailure(
        string? stdOut,
        string? stdErr,
        string? errorMessage)
    {
        var allLines = JoinOutput(stdOut, stdErr, errorMessage)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

        var failureStart = -1;
        string? testName = null;

        for (var i = 0; i < allLines.Length; i++)
        {
            var trimmed = allLines[i].Trim();
            if (trimmed.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = FailedTestRegex.Match(trimmed);
            if (match.Success)
            {
                failureStart = i;
                testName = match.Groups["name"].Value.Trim();
                break;
            }
        }

        var relevant = new List<string>();
        var relevantChars = 0;

        if (failureStart >= 0)
        {
            for (var i = failureStart; i < allLines.Length && relevant.Count < MaxTestEvidenceLines; i++)
            {
                var trimmed = allLines[i].TrimEnd();
                var compact = trimmed.Trim();

                if (i > failureStart &&
                    (compact.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase) ||
                     compact.StartsWith("Passed!", StringComparison.OrdinalIgnoreCase) ||
                     FailedTestRegex.IsMatch(compact)))
                {
                    break;
                }

                if (relevantChars + trimmed.Length > MaxTestEvidenceChars)
                {
                    break;
                }

                relevant.Add(trimmed);
                relevantChars += trimmed.Length;
            }
        }
        else
        {
            relevant = allLines
                .Select(line => line.TrimEnd())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Take(MaxTestEvidenceLines)
                .ToList();
        }

        var locations = relevant
            .SelectMany(line =>
                StackLocationRegex.Matches(line).Cast<Match>()
                    .Concat(PythonStackLocationRegex.Matches(line).Cast<Match>()))
            .Select(match => new DiagnosticSourceLocation(
                NormalizePath(match.Groups["path"].Value),
                int.TryParse(match.Groups["line"].Value, out var parsedLine) ? parsedLine : (int?)null))
            .DistinctBy(location => $"{location.FilePath}:{location.Line}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errorLines = ExtractErrorLines(relevant);
        var errorSummary = errorLines.Count > 0
            ? string.Join(" | ", errorLines.Take(5))
            : relevant.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? errorMessage ?? "test failed";

        var identityParts = new List<string>
        {
            NormalizeText(testName ?? "unknown test"),
            NormalizeText(errorSummary)
        };

        return new TestFailureEvidence(
            ComputeFingerprint(identityParts),
            testName,
            errorSummary,
            relevant,
            locations);
    }

    public static IReadOnlyList<string> SelectCompilerRepairFiles(
        CompilerFailureEvidence evidence,
        IEnumerable<string> modifiedFiles)
    {
        return evidence.Locations
            .Select(location => MatchModifiedFile(location.FilePath, modifiedFiles))
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
    }

    public static IReadOnlyList<string> SelectTestRepairFiles(
        TestFailureEvidence evidence,
        IEnumerable<string> modifiedFiles)
    {
        var modified = modifiedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var explicitMatches = evidence.Locations
            .Select(location => MatchModifiedFile(location.FilePath, modified))
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (explicitMatches.Count > 0)
        {
            var first = explicitMatches[0];
            var selected = new List<string> { first };
            var firstIsTest = ProjectGraphHelper.IsTestFileCandidate(first);
            var relatedAcrossBoundary = explicitMatches.FirstOrDefault(path =>
                ProjectGraphHelper.IsTestFileCandidate(path) != firstIsTest &&
                evidence.ErrorSummary.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

            if (relatedAcrossBoundary != null)
            {
                selected.Add(relatedAcrossBoundary);
            }

            return selected;
        }

        var testClassName = GetTestClassName(evidence.TestName);
        if (!string.IsNullOrWhiteSpace(testClassName))
        {
            var testFile = modified.FirstOrDefault(path =>
                ProjectGraphHelper.IsTestFileCandidate(path) &&
                string.Equals(Path.GetFileNameWithoutExtension(path), testClassName, StringComparison.OrdinalIgnoreCase));
            if (testFile != null)
            {
                return new[] { testFile };
            }
        }

        var mentionedFile = modified.FirstOrDefault(path =>
            evidence.RelevantLines.Any(line =>
                line.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)));

        return mentionedFile != null ? new[] { mentionedFile } : Array.Empty<string>();
    }

    private static List<string> ExtractErrorLines(IReadOnlyList<string> relevant)
    {
        var result = new List<string>();
        var inError = false;

        foreach (var line in relevant)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Error Message:", StringComparison.OrdinalIgnoreCase))
            {
                inError = true;
                var inline = trimmed["Error Message:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(inline)) result.Add(inline);
                continue;
            }

            if (trimmed.StartsWith("Stack Trace:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (inError && !string.IsNullOrWhiteSpace(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static string? GetTestClassName(string? testName)
    {
        if (string.IsNullOrWhiteSpace(testName)) return null;
        var parts = testName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? parts[^2] : null;
    }

    private static string? MatchModifiedFile(string diagnosticPath, IEnumerable<string> modifiedFiles)
    {
        var normalizedDiagnostic = NormalizePath(diagnosticPath);
        var candidates = modifiedFiles
            .Select(path => new { Original = path, Normalized = NormalizePath(path) })
            .Where(item =>
                string.Equals(item.Normalized, normalizedDiagnostic, StringComparison.OrdinalIgnoreCase) ||
                normalizedDiagnostic.EndsWith('/' + item.Normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 1) return candidates[0].Original;

        var fileName = Path.GetFileName(normalizedDiagnostic);
        var fileNameMatches = modifiedFiles
            .Where(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return fileNameMatches.Count == 1 ? fileNameMatches[0] : null;
    }

    private static string JoinOutput(string? stdOut, string? stdErr, string? errorMessage) =>
        $"{stdOut}\n{stdErr}\n{errorMessage}";

    private static string NormalizePath(string path) => path.Trim().Replace('\\', '/').TrimStart('.', '/');

    private static string NormalizeText(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim().ToLowerInvariant();

    private static string ComputeFingerprint(IEnumerable<string> parts)
    {
        var normalized = string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static IReadOnlyList<NormalizedFailureItem> ParseAllTestFailures(
        string? stdOut,
        string? stdErr,
        string? errorMessage)
    {
        var allLines = JoinOutput(stdOut, stdErr, errorMessage)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

        var failures = new List<NormalizedFailureItem>();
        var failureIndices = new List<(int Index, string Name)>();

        for (var i = 0; i < allLines.Length; i++)
        {
            var trimmed = allLines[i].Trim();
            if (trimmed.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = FailedTestRegex.Match(trimmed);
            if (match.Success)
            {
                var name = match.Groups["name"].Value.Trim();
                failureIndices.Add((i, name));
            }
        }

        if (failureIndices.Count > 0)
        {
            for (var idx = 0; idx < failureIndices.Count; idx++)
            {
                var start = failureIndices[idx].Index;
                var testName = failureIndices[idx].Name;
                var end = (idx + 1 < failureIndices.Count) ? failureIndices[idx + 1].Index : allLines.Length;

                var testLines = new List<string>();
                for (var j = start; j < end && testLines.Count < MaxTestEvidenceLines; j++)
                {
                    var compact = allLines[j].Trim();
                    if (j > start && (compact.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase) ||
                                     compact.StartsWith("Passed!", StringComparison.OrdinalIgnoreCase)))
                    {
                        break;
                    }
                    testLines.Add(allLines[j].TrimEnd());
                }

                var errorLines = ExtractErrorLines(testLines);
                var errorSummary = errorLines.Count > 0
                    ? string.Join(" | ", errorLines.Take(5))
                    : testLines.Skip(1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "test failed";

                var loc = testLines
                    .SelectMany(line => StackLocationRegex.Matches(line).Cast<Match>()
                        .Concat(PythonStackLocationRegex.Matches(line).Cast<Match>()))
                    .Select(m => NormalizePath(m.Groups["path"].Value))
                    .FirstOrDefault();

                var normDiag = NormalizeText($"{testName}: {errorSummary}");
                var failureKey = ComputeFingerprint(new[] { NormalizeText(testName), NormalizeText(errorSummary) });

                failures.Add(new NormalizedFailureItem(
                    FailureKey: failureKey,
                    TestName: testName,
                    ErrorSummary: errorSummary,
                    NormalizedDiagnostic: normDiag,
                    Location: loc));
            }
        }
        else
        {
            var fallbackSummary = errorMessage ?? allLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "test failed";
            var fallbackKey = ComputeFingerprint(new[] { NormalizeText(fallbackSummary) });
            failures.Add(new NormalizedFailureItem(
                FailureKey: fallbackKey,
                TestName: null,
                ErrorSummary: fallbackSummary,
                NormalizedDiagnostic: NormalizeText(fallbackSummary)));
        }

        return failures;
    }

    public static IReadOnlyList<NormalizedFailureItem> ParseAllCompilerFailures(
        string? stdOut,
        string? stdErr,
        string? errorMessage)
    {
        var lines = JoinOutput(stdOut, stdErr, errorMessage)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => Regex.IsMatch(line, @"\b(?:error|fatal)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompilerDiagnostics)
            .ToList();

        var failures = new List<NormalizedFailureItem>();

        foreach (var line in lines)
        {
            var match = ParenthesizedDiagnosticRegex.Match(line);
            if (!match.Success)
            {
                match = ColonDiagnosticRegex.Match(line);
            }

            if (!match.Success)
            {
                var norm = NormalizeText(line);
                failures.Add(new NormalizedFailureItem(
                    FailureKey: ComputeFingerprint(new[] { norm }),
                    TestName: null,
                    ErrorSummary: line,
                    NormalizedDiagnostic: norm));
                continue;
            }

            var path = NormalizePath(match.Groups["path"].Value);
            var lineNo = match.Groups["line"].Value;
            var colNo = match.Groups["column"].Value;
            var code = match.Groups["code"].Value.ToUpperInvariant();
            var msg = match.Groups["message"].Value;

            var normDiag = $"{path.ToLowerInvariant()}:{lineNo}:{colNo}:{code}:{NormalizeText(msg)}";
            var failureKey = ComputeFingerprint(new[] { normDiag });

            failures.Add(new NormalizedFailureItem(
                FailureKey: failureKey,
                TestName: null,
                ErrorSummary: line,
                NormalizedDiagnostic: normDiag,
                Location: $"{path}:{lineNo}"));
        }

        if (failures.Count == 0)
        {
            var fallback = errorMessage ?? "build failed";
            var norm = NormalizeText(fallback);
            failures.Add(new NormalizedFailureItem(
                FailureKey: ComputeFingerprint(new[] { norm }),
                TestName: null,
                ErrorSummary: fallback,
                NormalizedDiagnostic: norm));
        }

        return failures;
    }

    public static BaselineFailureComparison CompareFailureSets(
        IReadOnlyList<NormalizedFailureItem> taskFailures,
        IReadOnlyList<NormalizedFailureItem> baselineFailures,
        bool baselineCheckSucceeded,
        bool baselineIsInconclusive = false)
    {
        if (baselineIsInconclusive)
        {
            return new BaselineFailureComparison(
                BaselineFailureClassification.Unknown,
                PreExistingCount: 0,
                NewRegressionCount: taskFailures.Count,
                ChangedCount: 0,
                PreExistingFailures: Array.Empty<NormalizedFailureItem>(),
                NewRegressions: taskFailures,
                ChangedFailures: Array.Empty<NormalizedFailureItem>(),
                Summary: "Baseline check was inconclusive; preserving task failure evidence.");
        }

        if (baselineCheckSucceeded || baselineFailures.Count == 0)
        {
            return new BaselineFailureComparison(
                BaselineFailureClassification.NewRegression,
                PreExistingCount: 0,
                NewRegressionCount: taskFailures.Count,
                ChangedCount: 0,
                PreExistingFailures: Array.Empty<NormalizedFailureItem>(),
                NewRegressions: taskFailures,
                ChangedFailures: Array.Empty<NormalizedFailureItem>(),
                Summary: $"Failed: {taskFailures.Count} new regression(s) introduced.");
        }

        var preExisting = new List<NormalizedFailureItem>();
        var newRegressions = new List<NormalizedFailureItem>();
        var changed = new List<NormalizedFailureItem>();

        foreach (var taskFailure in taskFailures)
        {
            var exactMatch = baselineFailures.FirstOrDefault(b =>
                string.Equals(b.FailureKey, taskFailure.FailureKey, StringComparison.Ordinal));

            if (exactMatch != null)
            {
                preExisting.Add(taskFailure);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(taskFailure.TestName))
            {
                var matchingNameBaseline = baselineFailures.FirstOrDefault(b =>
                    !string.IsNullOrWhiteSpace(b.TestName) &&
                    string.Equals(b.TestName, taskFailure.TestName, StringComparison.OrdinalIgnoreCase));

                if (matchingNameBaseline != null)
                {
                    if (IsSubstantiallySimilarError(taskFailure.ErrorSummary, matchingNameBaseline.ErrorSummary))
                    {
                        preExisting.Add(taskFailure);
                    }
                    else
                    {
                        changed.Add(taskFailure);
                    }
                    continue;
                }
            }

            if (taskFailure.TestName == null && !string.IsNullOrWhiteSpace(taskFailure.Location))
            {
                var fileOnly = taskFailure.Location.Split(':')[0];
                var matchingComp = baselineFailures.FirstOrDefault(b =>
                    b.TestName == null &&
                    b.Location != null &&
                    string.Equals(b.Location.Split(':')[0], fileOnly, StringComparison.OrdinalIgnoreCase) &&
                    IsSubstantiallySimilarError(taskFailure.ErrorSummary, b.ErrorSummary));

                if (matchingComp != null)
                {
                    preExisting.Add(taskFailure);
                    continue;
                }
            }

            newRegressions.Add(taskFailure);
        }

        BaselineFailureClassification classification;
        string summary;

        if (newRegressions.Count > 0)
        {
            classification = BaselineFailureClassification.NewRegression;
            summary = preExisting.Count > 0
                ? $"Failed: {newRegressions.Count} new regression(s) introduced ({preExisting.Count} pre-existing repository failure(s) remain)."
                : $"Failed: {newRegressions.Count} new regression(s) introduced.";
        }
        else if (changed.Count > 0)
        {
            classification = BaselineFailureClassification.Changed;
            summary = $"Changed: {changed.Count} failure(s) changed ({preExisting.Count} pre-existing repository failure(s) remain).";
        }
        else if (preExisting.Count > 0)
        {
            classification = BaselineFailureClassification.PreExisting;
            summary = $"No new regressions: {preExisting.Count} pre-existing repository failure(s) remain.";
        }
        else
        {
            classification = BaselineFailureClassification.Unknown;
            summary = "Unable to classify failure against baseline.";
        }

        return new BaselineFailureComparison(
            classification,
            PreExistingCount: preExisting.Count,
            NewRegressionCount: newRegressions.Count,
            ChangedCount: changed.Count,
            PreExistingFailures: preExisting,
            NewRegressions: newRegressions,
            ChangedFailures: changed,
            Summary: summary);
    }

    private static bool IsSubstantiallySimilarError(string errA, string errB)
    {
        var normA = NormalizeText(errA);
        var normB = NormalizeText(errB);
        if (normA == normB) return true;
        if (normA.Contains(normB) || normB.Contains(normA)) return true;

        var tokensA = normA.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = normB.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var intersection = tokensA.Intersect(tokensB).Count();
        var maxLen = Math.Max(tokensA.Length, tokensB.Length);
        return maxLen > 0 && ((double)intersection / maxLen) >= 0.6;
    }

    public static bool AreCompilerDiagnosticsWorsened(CompilerFailureEvidence before, CompilerFailureEvidence after)
    {
        if (before == null || after == null) return false;
        return after.DiagnosticLines.Count > before.DiagnosticLines.Count * 2 && after.DiagnosticLines.Count >= 3;
    }

    public static bool IsCompilerProgressMade(CompilerFailureEvidence before, CompilerFailureEvidence after)
    {
        if (before == null || after == null) return false;
        if (string.Equals(before.FailureFingerprint, after.FailureFingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        return after.DiagnosticLines.Count < before.DiagnosticLines.Count;
    }
}
