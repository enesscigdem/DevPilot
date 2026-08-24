using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class ExecutionWorkspaceRawLogTests
{
    [Fact]
    public void RawTechnicalLog_PreservesMultilineDiagnosticMessages()
    {
        var source = ReadExecutionWorkspaceSource();
        var rawLogSpan = ExtractSpanClass(source, "{act.message}");

        rawLogSpan.Should().Contain("whitespace-pre-wrap");
        rawLogSpan.Should().Contain("break-words");
        rawLogSpan.Should().NotContain("truncate");
    }

    [Fact]
    public void RawTechnicalLog_RendersDiagnosticLinesFromMetadata()
    {
        var source = ReadExecutionWorkspaceSource();
        source.Should().Contain("act.metadata?.diagnosticLines");
        source.Should().Contain("act.metadata.diagnosticLines.join");
        var rawSectionStart = source.IndexOf("Raw technical log", StringComparison.Ordinal);
        rawSectionStart.Should().BeGreaterThan(0);
        var rawSection = source[rawSectionStart..];
        rawSection.Should().Contain("diagnosticLines");
    }

    [Fact]
    public void StructuredExecutionViewer_StillTruncatesItemMessages()
    {
        var source = ReadExecutionWorkspaceSource();
        var structuredSpan = ExtractSpanClass(source, "{item.message}");

        structuredSpan.Should().Contain("truncate");
        structuredSpan.Should().NotContain("whitespace-pre-wrap");
    }

    private static string ExtractSpanClass(string source, string expression)
    {
        var expressionIndex = source.IndexOf(expression, StringComparison.Ordinal);
        expressionIndex.Should().BeGreaterThan(0, $"source should contain {expression}");

        var spanStart = source.LastIndexOf("<span", expressionIndex, StringComparison.Ordinal);
        spanStart.Should().BeGreaterThan(0);
        var span = source[spanStart..expressionIndex];
        var classStart = span.IndexOf("className=\"", StringComparison.Ordinal);
        classStart.Should().BeGreaterThan(0);
        var valueStart = classStart + "className=\"".Length;
        var valueEnd = span.IndexOf('"', valueStart);
        return span[valueStart..valueEnd];
    }

    private static string ReadExecutionWorkspaceSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DevPilot.Web", "src", "pages", "ExecutionWorkspace.tsx");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate ExecutionWorkspace.tsx from the test assembly.");
    }
}
