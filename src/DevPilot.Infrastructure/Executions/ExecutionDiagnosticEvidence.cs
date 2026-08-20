using DevPilot.Application.DeveloperAgent.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevPilot.Infrastructure.DeveloperAgent;

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

    private static readonly Regex CompilerDiagnosticRegex = new(
        @"(?<path>(?:[A-Za-z]:)?[^\r\n]*?\.cs)\((?<line>\d+),(?<column>\d+)\):\s*error\s+(?<code>[A-Za-z]+\d+):\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FailedTestRegex = new(
        @"^\s*(?:x\s+)?Failed\s+(?<name>.+?)(?:\s+\[[^\]]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StackLocationRegex = new(
        @"\bin\s+(?<path>(?:[A-Za-z]:)?[^\r\n]+?\.cs):line\s+(?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static CompilerFailureEvidence ParseCompilerFailure(
        string? stdOut,
        string? stdErr,
        string? errorMessage)
    {
        var lines = JoinOutput(stdOut, stdErr, errorMessage)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Contains("error ", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompilerDiagnostics)
            .ToList();

        var locations = new List<DiagnosticSourceLocation>();
        var normalized = new List<string>();

        foreach (var line in lines)
        {
            var match = CompilerDiagnosticRegex.Match(line);
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
            .SelectMany(line => StackLocationRegex.Matches(line).Cast<Match>())
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
}
