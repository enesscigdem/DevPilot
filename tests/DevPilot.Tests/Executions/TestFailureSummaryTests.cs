using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class TestFailureSummaryTests
{
    private const string VitestOutput = """
         RUN  v2.1.9 C:/work

         ❯ src/App.test.tsx (16 tests | 3 failed) 1103ms

        ⎯⎯⎯⎯⎯⎯⎯ Failed Tests 3 ⎯⎯⎯⎯⎯⎯⎯

         FAIL  src/App.test.tsx > App > yeni görevi ekler
        TestingLibraryElementError: Unable to find an element with the text: Henüz görev yok.. This could be because the text is broken up by multiple elements.

        Ignored nodes: comments, script, style
        <body />

         FAIL  src/App.test.tsx > App > görevi siler
        TestingLibraryElementError: Unable to find an element with the text: Henüz görev yok.. This could be because the text is broken up by multiple elements.

         FAIL  src/App.test.tsx > App > filtre
        TestingLibraryElementError: Unable to find an element with the text: Eşleşen görev yok.. This could be because the text is broken up by multiple elements.
        """;

    [Fact]
    public void Summarize_GroupsVitestFailuresBySharedCause()
    {
        var summary = TestFailureSummarizer.Summarize(VitestOutput, null, null);

        summary.Should().NotBeNull();
        summary!.FailureCount.Should().Be(3);
        summary.Groups.Should().HaveCount(2);
        summary.Groups.Single(group => group.TestNames.Count == 2).Reason
            .Should().Be("TestingLibraryElementError: Unable to find an element with the text: Henüz görev yok.");
        summary.HasSharedCause.Should().BeTrue();
    }

    [Fact]
    public void Summarize_StripsAnsiColorCodes()
    {
        var colored = VitestOutput.Replace(" FAIL  ", "\u001b[31m FAIL \u001b[39m ");

        TestFailureSummarizer.Summarize(colored, null, null)!.FailureCount.Should().Be(3);
    }

    [Fact]
    public void Fingerprint_ChangesWhenFailuresAreFixed_AndIsStableOtherwise()
    {
        var all = TestFailureSummarizer.Summarize(VitestOutput, null, null)!;
        var again = TestFailureSummarizer.Summarize(VitestOutput, null, null)!;
        var fewer = TestFailureSummarizer.Summarize(
            VitestOutput[..VitestOutput.IndexOf(" FAIL  src/App.test.tsx > App > filtre", StringComparison.Ordinal)],
            null,
            null)!;

        again.Fingerprint.Should().Be(all.Fingerprint);
        fewer.Fingerprint.Should().NotBe(all.Fingerprint);
    }

    [Fact]
    public void Summarize_ReturnsNullWhenThereIsNothingToParse()
    {
        TestFailureSummarizer.Summarize(null, null, null).Should().BeNull();
    }

    [Fact]
    public void FindSourceHints_PointsAtRenamedUiText()
    {
        var root = Path.Combine(Path.GetTempPath(), "devpilot-hints-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "components"));
        try
        {
            File.WriteAllText(
                Path.Combine(root, "src", "components", "BoardColumn.tsx"),
                "export const A = () => <p>{filtered ? \"Eşleşen görev yok\" : \"Henüz görev yok\"}</p>\n");
            File.WriteAllText(Path.Combine(root, "src", "App.test.tsx"), "getByText('Henüz görev yok.')\n");

            var summary = TestFailureSummarizer.Summarize(VitestOutput, null, null)!;
            var hints = TestFailureSummarizer.FindSourceHints(root, summary);

            hints.Should().HaveCount(2);
            hints.Should().Contain(hint => hint.Contains("src/components/BoardColumn.tsx:1") && hint.Contains("Henüz görev yok"));
            hints.Should().NotContain(hint => hint.Contains("App.test.tsx"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ToPromptText_ListsGroupsAndHints()
    {
        var summary = TestFailureSummarizer.Summarize(VitestOutput, null, null)!;

        var text = summary.ToPromptText(new[] { "tests look for \"X\" but a.tsx:1 renders \"Y\"" });

        text.Should().Contain("3 test(s) failed, in 2 distinct cause group(s)");
        text.Should().Contain("[2 test(s)]");
        text.Should().Contain("renders \"Y\"");
    }
}
