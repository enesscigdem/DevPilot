using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Infrastructure.DeveloperAgent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DevPilot.Infrastructure.Executions;

public sealed record DiagnosticSourceLocation(string FilePath, int? Line = null, int? Column = null);

public sealed record CompilerFailureEvidence(
    string FailureFingerprint,
    IReadOnlyList<string> DiagnosticLines,
    IReadOnlyList<DiagnosticSourceLocation> Locations);

public sealed record FileScopedCompilerDiagnostics(
    IReadOnlyList<string> DiagnosticLines,
    IReadOnlyList<DiagnosticSourceLocation> Locations);

public sealed record CompilerRepairSelection(
    string? FilePath,
    IReadOnlyList<string> ImplicatedFiles,
    string Reason);

public sealed record TestRepairSelection(
    IReadOnlyList<string> FilePaths,
    string Reason,
    string? FailingTestName = null,
    string? ErrorSummary = null,
    IReadOnlyList<string>? EvidenceLines = null);

public sealed record MissingContractReference(string TypeName, string? MemberName = null);

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
    public const int MaxScopedDiagnosticsPerFile = 8;
    public const int MaxSanitizedActivityDiagnosticLines = 5;
    public const int MaxSanitizedActivityDiagnosticChars = 240;

    private const string SourceExtensionPattern = @"(?:cs|fs|vb|ts|tsx|js|jsx|py|java|kt|go|rs)";

    private static readonly Regex ParenthesizedDiagnosticRegex = new(
        $@"(?<path>(?:[A-Za-z]:)?[^\r\n]*?\.{SourceExtensionPattern})\((?<line>\d+),(?<column>\d+)\):\s*error\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ColonDiagnosticRegex = new(
        $@"(?<path>(?:[A-Za-z]:)?[^\r\n]*?\.{SourceExtensionPattern}):(?<line>\d+)(?::(?<column>\d+))?:\s*(?:error|fatal)\s*(?<code>[A-Za-z]+\d+)?\s*:?\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FailedTestRegex = new(
        @"^\s*(?:x\s+)?(?:Failed|Başarısız|Fehlgeschlagen|Échec|Fallido)\s+(?<name>[A-Za-z_][A-Za-z0-9_.+`]+)(?:\s+\[[^\]]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex XUnitTestRegex = new(
        @"\[xUnit\.net\s+[^\]]+\]\s+(?<name>[A-Za-z_][A-Za-z0-9_.+`]+)\s+\[FAIL\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StackLocationRegex = new(
        $@"(?:(?:\bat|\bkonum:)\s+[^\r\n]*?\b(?:in|içinde)\s+|\bin\s+|\biçinde\s+|\bat\s+[^\r\n]*?\(|\bkonum:\s+[^\r\n]*?\()?\s*(?<path>(?:[A-Za-z]:)?[^\r\n()]*?\.{SourceExtensionPattern})(?::line\s+|:satır\s+|:\s*line\s+|:\s*satır\s+|:)(?<line>\d+)(?::(?<column>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PythonStackLocationRegex = new(
        @"\bFile\s+[""'](?<path>(?:[A-Za-z]:)?[^""'\r\n]+?\.py)[""'],\s*line\s+(?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SectionHeaderRegex = new(
        @"^\s*(?:Error\s*Message|Hata\s*İletisi|Fehlermeldung|Message\s*d'erreur|Mensaje\s*de\s*error|Message|Error)\s*:\s*(?<inline>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StackHeaderRegex = new(
        @"^\s*(?:Stack\s*Trace|Yığın\s*İzleme|Stapelüberwachung|Trace\s*de\s*la\s*pile|Seguimiento\s*de\s*la\s*pila|Trace|StackTrace)\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StackFrameRegex = new(
        @"(?:^\s*(?:at|konum:)\s+|\.cs:(?:line|satır)\s+\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SummaryLineRegex = new(
        @"^\s*(?:\[xUnit\.net|Failed!|Passed!|Başarısız!|Başarılı!|Toplam|Total|Passed|Failed|Başarısız|Başarılı|Standard\s+Output|Standart\s+Çıktı)",
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
            if (trimmed.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Başarısız!", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var xunitMatch = XUnitTestRegex.Match(trimmed);
            if (xunitMatch.Success)
            {
                failureStart = i;
                testName = xunitMatch.Groups["name"].Value.Trim();
                break;
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

                if (i > failureStart)
                {
                    if (compact.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase) ||
                        compact.StartsWith("Başarısız!", StringComparison.OrdinalIgnoreCase) ||
                        compact.StartsWith("Passed!", StringComparison.OrdinalIgnoreCase) ||
                        compact.StartsWith("Başarılı!", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    var isNewTestMarker = (XUnitTestRegex.IsMatch(compact) || FailedTestRegex.IsMatch(compact)) &&
                                          (testName == null || !compact.Contains(testName, StringComparison.OrdinalIgnoreCase));
                    if (isNewTestMarker)
                    {
                        break;
                    }
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

    public static FileScopedCompilerDiagnostics ScopeToFile(
        CompilerFailureEvidence evidence,
        string filePath,
        int maxLines = MaxScopedDiagnosticsPerFile)
    {
        if (evidence == null || string.IsNullOrWhiteSpace(filePath))
        {
            return new FileScopedCompilerDiagnostics(Array.Empty<string>(), Array.Empty<DiagnosticSourceLocation>());
        }

        var target = new[] { filePath };
        var locations = evidence.Locations
            .Where(location => MatchModifiedFile(location.FilePath, target) != null)
            .Take(Math.Max(1, maxLines))
            .ToList();

        var lines = new List<string>();
        foreach (var line in evidence.DiagnosticLines)
        {
            if (!DiagnosticLineBelongsToFile(line, filePath))
            {
                continue;
            }

            lines.Add(line);
            if (lines.Count >= maxLines)
            {
                break;
            }
        }

        return new FileScopedCompilerDiagnostics(lines, locations);
    }

    public static bool IsCompilerDiagnosticLine(string diagnosticLine)
    {
        if (string.IsNullOrWhiteSpace(diagnosticLine))
        {
            return false;
        }

        return ParenthesizedDiagnosticRegex.IsMatch(diagnosticLine) ||
               ColonDiagnosticRegex.IsMatch(diagnosticLine);
    }

    public static bool DiagnosticLineBelongsToFile(string diagnosticLine, string filePath)
    {
        if (string.IsNullOrWhiteSpace(diagnosticLine) || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var match = ParenthesizedDiagnosticRegex.Match(diagnosticLine);
        if (!match.Success)
        {
            match = ColonDiagnosticRegex.Match(diagnosticLine);
        }

        if (match.Success)
        {
            return MatchModifiedFile(match.Groups["path"].Value, new[] { filePath }) != null;
        }

        var normalizedTarget = NormalizePath(filePath);
        var fileName = Path.GetFileName(normalizedTarget);
        var normalizedLine = diagnosticLine.Replace('\\', '/');
        return normalizedLine.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(fileName) &&
                normalizedLine.Contains(fileName, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> ListImplicatedCompilerFiles(
        CompilerFailureEvidence evidence,
        IEnumerable<string>? modifiedFiles = null)
    {
        var modified = (modifiedFiles ?? Array.Empty<string>()).ToList();
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in evidence.Locations)
        {
            if (string.IsNullOrWhiteSpace(location.FilePath))
            {
                continue;
            }

            var matched = modified.Count > 0
                ? MatchModifiedFile(location.FilePath, modified)
                : null;
            var candidate = matched ?? NormalizePath(location.FilePath);
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
            {
                continue;
            }

            ordered.Add(candidate);
        }

        return ordered;
    }

    public static CompilerRepairSelection SelectNextCompilerRepairTarget(
        CompilerFailureEvidence evidence,
        IEnumerable<string> modifiedFiles,
        IEnumerable<string>? attemptedForCurrentFailureSet = null,
        IReadOnlyDictionary<string, string>? touchedFileContents = null)
    {
        var modified = modifiedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var attempted = new HashSet<string>(
            (attemptedForCurrentFailureSet ?? Array.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var implicated = ListImplicatedCompilerFiles(evidence, modified);

        var matchedImplicated = implicated
            .Select(path => MatchModifiedFile(path, modified) ?? (modified.Contains(path, StringComparer.OrdinalIgnoreCase) ? path : null))
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contractOwner = TrySelectUniqueSharedContractOwner(evidence, modified, touchedFileContents);
        if (!string.IsNullOrWhiteSpace(contractOwner) && !attempted.Contains(contractOwner))
        {
            return new CompilerRepairSelection(
                contractOwner,
                matchedImplicated.Count > 0 ? matchedImplicated : new[] { contractOwner },
                "UniqueContractOwner");
        }

        var unattemptedMatched = matchedImplicated
            .Where(path => !attempted.Contains(path))
            .ToList();
        if (unattemptedMatched.Count > 0)
        {
            return new CompilerRepairSelection(
                unattemptedMatched[0],
                matchedImplicated,
                "NextUnattemptedFile");
        }

        return new CompilerRepairSelection(
            null,
            matchedImplicated,
            matchedImplicated.Count == 0 ? "Uncorrelated" : "AllImplicatedFilesExhausted");
    }

    public static string? TrySelectUniqueSharedContractOwner(
        CompilerFailureEvidence evidence,
        IEnumerable<string> modifiedFiles,
        IReadOnlyDictionary<string, string>? touchedFileContents)
    {
        if (evidence == null || touchedFileContents == null || touchedFileContents.Count == 0)
        {
            return null;
        }

        var shared = TryGetSharedMissingContract(evidence.DiagnosticLines);
        if (shared == null)
        {
            return null;
        }

        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var modified in modifiedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (path, content) in touchedFileContents)
            {
                if (MatchModifiedFile(path, new[] { modified }) != null ||
                    string.Equals(path, modified, StringComparison.OrdinalIgnoreCase))
                {
                    sources[modified] = content;
                    break;
                }
            }
        }

        return PlannedFileDependencyResolver.FindUniqueDeclarationOwner(shared.TypeName, sources);
    }

    public static MissingContractReference? TryGetSharedMissingContract(IEnumerable<string>? diagnosticLines)
    {
        if (diagnosticLines == null)
        {
            return null;
        }

        var parsed = diagnosticLines
            .Select(TryParseMissingContract)
            .Where(item => item != null)
            .Cast<MissingContractReference>()
            .ToList();
        if (parsed.Count < 2)
        {
            return null;
        }

        var typeName = parsed[0].TypeName;
        if (parsed.Any(item => !string.Equals(item.TypeName, typeName, StringComparison.Ordinal)))
        {
            return null;
        }

        var members = parsed
            .Select(item => item.MemberName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (members.Count > 1)
        {
            return null;
        }

        return new MissingContractReference(typeName, members.Count == 1 ? members[0] : parsed[0].MemberName);
    }

    public static MissingContractReference? TryParseMissingContract(string? diagnosticLine)
    {
        if (string.IsNullOrWhiteSpace(diagnosticLine))
        {
            return null;
        }

        var propertyMatch = Regex.Match(
            diagnosticLine,
            @"Property\s+'([A-Za-z_][\w]*)'\s+does not exist on type\s+'([A-Za-z_][\w]*)'",
            RegexOptions.IgnoreCase);
        if (propertyMatch.Success)
        {
            return new MissingContractReference(propertyMatch.Groups[2].Value, propertyMatch.Groups[1].Value);
        }

        var objectLiteralMatch = Regex.Match(
            diagnosticLine,
            @"'([A-Za-z_][\w]*)'\s+does not exist in type\s+'([A-Za-z_][\w]*)'",
            RegexOptions.IgnoreCase);
        if (objectLiteralMatch.Success)
        {
            return new MissingContractReference(objectLiteralMatch.Groups[2].Value, objectLiteralMatch.Groups[1].Value);
        }

        var definitionMatch = Regex.Match(
            diagnosticLine,
            @"'([A-Za-z_][\w]*)'\s+does not contain a definition for\s+'([A-Za-z_][\w]*)'",
            RegexOptions.IgnoreCase);
        if (definitionMatch.Success)
        {
            return new MissingContractReference(definitionMatch.Groups[1].Value, definitionMatch.Groups[2].Value);
        }

        return null;
    }

    public static IReadOnlyDictionary<string, string> LoadTouchedSourceSnapshots(
        string? workspacePath,
        IEnumerable<string> modifiedFiles,
        int maxFiles = 20,
        int maxCharsPerFile = 16_384)
    {
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(workspacePath) || modifiedFiles == null)
        {
            return snapshots;
        }

        foreach (var filePath in modifiedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (snapshots.Count >= maxFiles || string.IsNullOrWhiteSpace(filePath))
            {
                break;
            }

            var content = TryReadBoundedEvidenceText(filePath, workspacePath, fileContents: null);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            snapshots[filePath] = content.Length > maxCharsPerFile ? content[..maxCharsPerFile] : content;
        }

        return snapshots;
    }

    public static IReadOnlyList<string> SanitizeDiagnosticLinesForActivity(
        IEnumerable<string>? lines,
        string? workspaceRoot = null,
        int maxLines = MaxSanitizedActivityDiagnosticLines,
        int maxCharsPerLine = MaxSanitizedActivityDiagnosticChars)
    {
        var sanitized = new List<string>();
        if (lines == null)
        {
            return sanitized;
        }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var line = raw.Trim();
            if (!IsCompilerDiagnosticLine(line) &&
                !Regex.IsMatch(line, @"\b(?:error|fatal)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                line = MakeRepositoryRelative(line, workspaceRoot);
                var normalizedRoot = NormalizePath(workspaceRoot).TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(normalizedRoot))
                {
                    line = line.Replace(normalizedRoot + "/", string.Empty, StringComparison.OrdinalIgnoreCase);
                    line = line.Replace(normalizedRoot, string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (line.Length > maxCharsPerLine)
            {
                line = line[..maxCharsPerLine];
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            sanitized.Add(line);
            if (sanitized.Count >= maxLines)
            {
                break;
            }
        }

        return sanitized;
    }

    public static IReadOnlyList<string> SelectTestRepairFiles(
        TestFailureEvidence evidence,
        IEnumerable<string> modifiedFiles,
        string? workspacePath = null,
        IReadOnlyDictionary<string, string>? fileContents = null) =>
        SelectTestRepairTarget(evidence, modifiedFiles, workspacePath, fileContents).FilePaths;

    public static TestRepairSelection SelectTestRepairTarget(
        TestFailureEvidence evidence,
        IEnumerable<string> modifiedFiles,
        string? workspacePath = null,
        IReadOnlyDictionary<string, string>? fileContents = null)
    {
        var modified = modifiedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var evidenceLines = SanitizeTestEvidenceForActivity(evidence);
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

            return new TestRepairSelection(
                selected,
                "TouchedFileFromStack",
                evidence.TestName,
                evidence.ErrorSummary,
                evidenceLines);
        }

        var testClassName = GetTestClassName(evidence.TestName);
        if (!string.IsNullOrWhiteSpace(testClassName))
        {
            var testFile = modified.FirstOrDefault(path =>
                ProjectGraphHelper.IsTestFileCandidate(path) &&
                string.Equals(Path.GetFileNameWithoutExtension(path), testClassName, StringComparison.OrdinalIgnoreCase));
            if (testFile != null)
            {
                return new TestRepairSelection(
                    new[] { testFile },
                    "TouchedTestClassName",
                    evidence.TestName,
                    evidence.ErrorSummary,
                    evidenceLines);
            }
        }

        var productionFromUntouchedTest = TrySelectProductionFromUntouchedTest(
            evidence,
            modified,
            workspacePath,
            fileContents);
        if (productionFromUntouchedTest.FilePaths.Count > 0)
        {
            return productionFromUntouchedTest;
        }

        var mentionedFile = modified.FirstOrDefault(path =>
            evidence.RelevantLines.Any(line =>
                line.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)));

        return mentionedFile != null
            ? new TestRepairSelection(
                new[] { mentionedFile },
                "MentionedModifiedFile",
                evidence.TestName,
                evidence.ErrorSummary,
                evidenceLines)
            : new TestRepairSelection(
                Array.Empty<string>(),
                "Uncorrelated",
                evidence.TestName,
                evidence.ErrorSummary,
                evidenceLines);
    }

    public static IReadOnlyList<string> SanitizeTestEvidenceForActivity(
        TestFailureEvidence? evidence,
        int maxLines = MaxSanitizedActivityDiagnosticLines,
        int maxCharsPerLine = MaxSanitizedActivityDiagnosticChars)
    {
        var lines = new List<string>();
        if (evidence == null)
        {
            return lines;
        }

        if (!string.IsNullOrWhiteSpace(evidence.TestName))
        {
            lines.Add(BoundActivityLine($"Failed test: {evidence.TestName}", maxCharsPerLine));
        }

        if (!string.IsNullOrWhiteSpace(evidence.ErrorSummary))
        {
            lines.Add(BoundActivityLine($"Error: {evidence.ErrorSummary}", maxCharsPerLine));
        }

        foreach (var raw in evidence.RelevantLines ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                (!StackFrameRegex.IsMatch(raw) &&
                 !PythonStackLocationRegex.IsMatch(raw) &&
                 !raw.Contains(" at ", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            lines.Add(BoundActivityLine(raw.Trim(), maxCharsPerLine));
            if (lines.Count >= maxLines)
            {
                break;
            }
        }

        return lines.Take(maxLines).ToList();
    }

    private static TestRepairSelection TrySelectProductionFromUntouchedTest(
        TestFailureEvidence evidence,
        IReadOnlyList<string> modifiedFiles,
        string? workspacePath,
        IReadOnlyDictionary<string, string>? fileContents)
    {
        var empty = new TestRepairSelection(
            Array.Empty<string>(),
            "Uncorrelated",
            evidence.TestName,
            evidence.ErrorSummary,
            SanitizeTestEvidenceForActivity(evidence));

        var failingTestPath = evidence.Locations
            .Select(location => location.FilePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && ProjectGraphHelper.IsTestFileCandidate(path));

        if (string.IsNullOrWhiteSpace(failingTestPath))
        {
            return empty;
        }

        if (MatchModifiedFile(failingTestPath, modifiedFiles) != null)
        {
            return empty;
        }

        var productionModified = modifiedFiles
            .Where(path => !ProjectGraphHelper.IsTestFileCandidate(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (productionModified.Count == 0)
        {
            return empty;
        }

        var testSource = TryReadBoundedEvidenceText(failingTestPath, workspacePath, fileContents);
        var combined = string.Join('\n', new[]
        {
            testSource,
            evidence.ErrorSummary,
            string.Join('\n', evidence.RelevantLines ?? Array.Empty<string>())
        });

        var scored = productionModified
            .Select(path => (Path: path, Score: ScoreProductionImplication(path, combined, testSource)))
            .Where(item => item.Score >= 2)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (scored.Count == 0 || scored.Count > 2)
        {
            return empty;
        }

        return new TestRepairSelection(
            scored.Select(item => item.Path).ToList(),
            "TouchedProductionFromUntouchedTest",
            evidence.TestName,
            evidence.ErrorSummary,
            SanitizeTestEvidenceForActivity(evidence));
    }

    private static int ScoreProductionImplication(string productionPath, string combinedEvidence, string? testSource)
    {
        var score = 0;
        var fileName = Path.GetFileName(productionPath);
        var stem = Path.GetFileNameWithoutExtension(productionPath);
        var normalizedPath = NormalizePath(productionPath);

        if (!string.IsNullOrWhiteSpace(fileName) &&
            combinedEvidence.Contains(fileName, StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(normalizedPath) &&
            combinedEvidence.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        if (IsSignificantIdentifier(stem) &&
            !string.IsNullOrWhiteSpace(testSource) &&
            ContainsIdentifier(testSource, stem))
        {
            score += 2;
        }

        return score;
    }

    private static bool IsSignificantIdentifier(string stem) =>
        !string.IsNullOrWhiteSpace(stem) && stem.Length > 3;

    private static bool ContainsIdentifier(string text, string identifier)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        return Regex.IsMatch(
            text,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? TryReadBoundedEvidenceText(
        string diagnosticPath,
        string? workspacePath,
        IReadOnlyDictionary<string, string>? fileContents)
    {
        var relative = string.IsNullOrWhiteSpace(workspacePath)
            ? NormalizePath(diagnosticPath)
            : MakeRepositoryRelative(diagnosticPath, workspacePath);

        if (!string.IsNullOrWhiteSpace(relative) &&
            fileContents != null &&
            fileContents.TryGetValue(relative, out var provided) &&
            !string.IsNullOrWhiteSpace(provided))
        {
            return provided.Length > 16_384 ? provided[..16_384] : provided;
        }

        if (string.IsNullOrWhiteSpace(workspacePath) || string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        try
        {
            var resolved = WorktreeEditApplier.ValidateAndResolvePath(workspacePath, relative);
            if (!File.Exists(resolved))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(resolved);
            if (bytes.Length == 0 || bytes.Length > 16_384 || WorktreeEditApplier.IsBinaryContent(bytes))
            {
                return null;
            }

            var content = WorktreeEditApplier.DecodeUtf8Text(bytes, out _);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    private static string BoundActivityLine(string line, int maxChars)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
    }

    private static List<string> ExtractErrorLines(IReadOnlyList<string> relevant)
    {
        var result = new List<string>();
        var inError = false;

        foreach (var line in relevant)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var sectionMatch = SectionHeaderRegex.Match(trimmed);
            if (sectionMatch.Success)
            {
                inError = true;
                var inline = sectionMatch.Groups["inline"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    result.Add(inline);
                }
                continue;
            }

            if (StackHeaderRegex.IsMatch(trimmed))
            {
                break;
            }

            if (StackFrameRegex.IsMatch(trimmed))
            {
                if (inError) break;
                continue;
            }

            if (inError)
            {
                result.Add(trimmed);
            }
        }

        if (result.Count == 0)
        {
            foreach (var line in relevant)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (SummaryLineRegex.IsMatch(trimmed)) continue;
                if (StackHeaderRegex.IsMatch(trimmed)) continue;
                if (StackFrameRegex.IsMatch(trimmed)) continue;
                if (Regex.IsMatch(trimmed, @"^[\w\s]+:$")) continue;

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

    private static string? MatchModifiedFile(
        string diagnosticPath,
        IEnumerable<string> modifiedFiles)
    {
        var normalizedDiagnostic = NormalizePath(diagnosticPath);
        var candidates = modifiedFiles
            .Select(path => (Original: path, Normalized: NormalizePath(path)))
            .Where(item =>
                string.Equals(item.Normalized, normalizedDiagnostic, StringComparison.OrdinalIgnoreCase) ||
                normalizedDiagnostic.EndsWith('/' + item.Normalized.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
                item.Normalized.EndsWith('/' + normalizedDiagnostic.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
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

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim().Replace('\\', '/');
        if (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        return trimmed;
    }

    private static string NormalizeText(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim().ToLowerInvariant();

    private static string ComputeFingerprint(IEnumerable<string> parts)
    {
        var normalized = string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static string MakeRepositoryRelative(string path, string? workspaceRoot = null)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return normalizedPath;
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var normalizedRoot = NormalizePath(workspaceRoot).TrimEnd('/');
            if (normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPath[(normalizedRoot.Length + 1)..];
            }
            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        return normalizedPath;
    }

    public static IReadOnlyList<NormalizedFailureItem> ParseAllCompilerFailures(
        string? stdOut,
        string? stdErr,
        string? errorMessage,
        string? workspaceRoot = null)
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

            var rawPath = match.Groups["path"].Value;
            var relativePath = MakeRepositoryRelative(rawPath, workspaceRoot);
            var lineNo = match.Groups["line"].Value;
            var colNo = match.Groups["column"].Value;
            var code = match.Groups["code"].Value.ToUpperInvariant();
            var msg = match.Groups["message"].Value;

            var normDiag = $"{relativePath.ToLowerInvariant()}:{lineNo}:{colNo}:{code}:{NormalizeText(msg)}";
            var failureKey = ComputeFingerprint(new[] { normDiag });

            failures.Add(new NormalizedFailureItem(
                FailureKey: failureKey,
                TestName: null,
                ErrorSummary: line,
                NormalizedDiagnostic: normDiag,
                Location: $"{relativePath}:{lineNo}"));
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

    public static IReadOnlyList<NormalizedFailureItem> ParseAllTestFailures(
        string? stdOut,
        string? stdErr,
        string? errorMessage,
        string? workspaceRoot = null)
    {
        var allLines = JoinOutput(stdOut, stdErr, errorMessage)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

        var failures = new List<NormalizedFailureItem>();
        var failureIndices = new List<(int Index, string Name)>();

        for (var i = 0; i < allLines.Length; i++)
        {
            var trimmed = allLines[i].Trim();
            if (trimmed.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Başarısız!", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? name = null;
            var xunitMatch = XUnitTestRegex.Match(trimmed);
            if (xunitMatch.Success)
            {
                name = xunitMatch.Groups["name"].Value.Trim();
            }
            else
            {
                var match = FailedTestRegex.Match(trimmed);
                if (match.Success)
                {
                    name = match.Groups["name"].Value.Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                if (failureIndices.Count > 0 &&
                    string.Equals(failureIndices[^1].Name, name, StringComparison.OrdinalIgnoreCase) &&
                    i - failureIndices[^1].Index < 5)
                {
                    continue;
                }

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
                                     compact.StartsWith("Başarısız!", StringComparison.OrdinalIgnoreCase) ||
                                     compact.StartsWith("Passed!", StringComparison.OrdinalIgnoreCase) ||
                                     compact.StartsWith("Başarılı!", StringComparison.OrdinalIgnoreCase)))
                    {
                        break;
                    }
                    testLines.Add(allLines[j]);
                }

                var errorLines = ExtractErrorLines(testLines);
                var errorSummary = errorLines.Count > 0
                    ? string.Join(" | ", errorLines.Take(5))
                    : testLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? errorMessage ?? "test failed";

                var locations = testLines
                    .SelectMany(line =>
                        StackLocationRegex.Matches(line).Cast<Match>()
                            .Concat(PythonStackLocationRegex.Matches(line).Cast<Match>()))
                    .Select(m => new DiagnosticSourceLocation(
                        MakeRepositoryRelative(m.Groups["path"].Value, workspaceRoot),
                        int.TryParse(m.Groups["line"].Value, out var parsedLine) ? parsedLine : (int?)null))
                    .DistinctBy(l => $"{l.FilePath}:{l.Line}", StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var normDiag = string.Join("\n", new[]
                {
                    NormalizeText(testName),
                    NormalizeText(errorSummary)
                });

                failures.Add(new NormalizedFailureItem(
                    FailureKey: ComputeFingerprint(new[] { normDiag }),
                    TestName: testName,
                    ErrorSummary: errorSummary,
                    NormalizedDiagnostic: normDiag,
                    Location: locations.FirstOrDefault()?.FilePath));
            }
        }
        else
        {
            var fallbackEvidence = ParseTestFailure(stdOut, stdErr, errorMessage);
            var normDiag = string.Join("\n", new[]
            {
                NormalizeText(fallbackEvidence.TestName ?? "unknown test"),
                NormalizeText(fallbackEvidence.ErrorSummary)
            });

            failures.Add(new NormalizedFailureItem(
                FailureKey: ComputeFingerprint(new[] { normDiag }),
                TestName: fallbackEvidence.TestName,
                ErrorSummary: fallbackEvidence.ErrorSummary,
                NormalizedDiagnostic: normDiag,
                Location: fallbackEvidence.Locations.FirstOrDefault()?.FilePath));
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
                NewRegressionCount: 0,
                ChangedCount: 0,
                PreExistingFailures: Array.Empty<NormalizedFailureItem>(),
                NewRegressions: Array.Empty<NormalizedFailureItem>(),
                ChangedFailures: Array.Empty<NormalizedFailureItem>(),
                Summary: "Baseline check was inconclusive; preserving task failure evidence.");
        }

        if (baselineCheckSucceeded)
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

        if (baselineFailures.Count == 0)
        {
            return new BaselineFailureComparison(
                BaselineFailureClassification.Unknown,
                PreExistingCount: 0,
                NewRegressionCount: 0,
                ChangedCount: 0,
                PreExistingFailures: Array.Empty<NormalizedFailureItem>(),
                NewRegressions: Array.Empty<NormalizedFailureItem>(),
                ChangedFailures: Array.Empty<NormalizedFailureItem>(),
                Summary: "Baseline check produced no diagnostic items; unable to classify regression.");
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
                var (fileOnly, _) = ParseDiagnosticLocation(taskFailure.Location);
                var matchingComp = baselineFailures.FirstOrDefault(b =>
                    b.TestName == null &&
                    b.Location != null &&
                    string.Equals(ParseDiagnosticLocation(b.Location).FilePath, fileOnly, StringComparison.OrdinalIgnoreCase) &&
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
            classification = BaselineFailureClassification.ChangedRegression;
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

    public static CompilerFailureEvidence CreateActionableCompilerEvidence(
        IReadOnlyList<NormalizedFailureItem> actionableFailures,
        string? fallbackStdOut,
        string? fallbackStdErr,
        string? fallbackErrorMessage)
    {
        if (actionableFailures == null || actionableFailures.Count == 0)
        {
            return ParseCompilerFailure(fallbackStdOut, fallbackStdErr, fallbackErrorMessage);
        }

        var lines = actionableFailures
            .Select(f => f.ErrorSummary)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompilerDiagnostics)
            .ToList();

        var locations = new List<DiagnosticSourceLocation>();
        var normalized = new List<string>();

        foreach (var failure in actionableFailures)
        {
            if (!string.IsNullOrWhiteSpace(failure.Location))
            {
                var (path, line) = ParseDiagnosticLocation(failure.Location);
                locations.Add(new DiagnosticSourceLocation(path, line));
            }
            normalized.Add(failure.NormalizedDiagnostic);
        }

        if (normalized.Count == 0)
        {
            normalized.Add(NormalizeText(fallbackErrorMessage ?? "build failed"));
        }

        return new CompilerFailureEvidence(
            ComputeFingerprint(normalized),
            lines,
            locations);
    }

    public static TestFailureEvidence CreateActionableTestEvidence(
        IReadOnlyList<NormalizedFailureItem> actionableFailures,
        string? fallbackStdOut,
        string? fallbackStdErr,
        string? fallbackErrorMessage)
    {
        if (actionableFailures == null || actionableFailures.Count == 0)
        {
            return ParseTestFailure(fallbackStdOut, fallbackStdErr, fallbackErrorMessage);
        }

        var first = actionableFailures[0];
        var testName = first.TestName;
        var errorSummary = string.Join(" | ", actionableFailures.Select(f => f.ErrorSummary).Take(5));
        var relevant = actionableFailures.Select(f => f.ErrorSummary).ToList();
        var locations = new List<DiagnosticSourceLocation>();

        foreach (var failure in actionableFailures)
        {
            if (!string.IsNullOrWhiteSpace(failure.Location))
            {
                var (path, line) = ParseDiagnosticLocation(failure.Location);
                locations.Add(new DiagnosticSourceLocation(path, line));
            }
        }

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

    public static (string FilePath, int? Line) ParseDiagnosticLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return (string.Empty, null);
        }

        var trimmed = location.Trim();
        var lastColon = trimmed.LastIndexOf(':');

        if (lastColon > 0 && int.TryParse(trimmed.AsSpan(lastColon + 1), out var line))
        {
            var rawPath = trimmed[..lastColon];
            return (NormalizePath(rawPath), line);
        }

        return (NormalizePath(trimmed), null);
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
}
