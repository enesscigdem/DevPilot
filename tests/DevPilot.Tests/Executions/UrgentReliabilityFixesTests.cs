using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class UrgentReliabilityFixesTests
{
    [Theory]
    [InlineData("Cannot perform manual browser testing required by task.")]
    [InlineData("Real browser testing needed before implementing fixes.")]
    [InlineData("Unable to verify the formatting behaviour.")]
    public void NoChange_JustifiedByInabilityToTestManually_IsRejected(string reason)
    {
        var entry = new ManifestFileEntry("src/components/NoteEditor.tsx", FileEditAction.Modify);
        var spec = new FileEditSpec(entry.FilePath, FileEditAction.Modify, NoChange: true, NoChangeReason: reason);

        var act = () => DeveloperAgent.ValidateExplicitNoChange(spec, entry);

        act.Should().Throw<FormatException>().WithMessage("*not a reason to skip the edit*");
    }

    [Fact]
    public void NoChange_WithGenuineReason_IsAccepted()
    {
        var entry = new ManifestFileEntry("src/Foo.cs", FileEditAction.Modify);
        var spec = new FileEditSpec(entry.FilePath, FileEditAction.Modify, NoChange: true,
            NoChangeReason: "Target already trims whitespace as requested.");

        var act = () => DeveloperAgent.ValidateExplicitNoChange(spec, entry);

        act.Should().NotThrow();
    }

    [Fact]
    public void RepairHint_ForMultipleElementsFailure_ExplainsQueryNarrowingAndKeepsEscalation()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            "FAIL src/App.test.tsx > renders note\nTestingLibraryElementError: Found multiple elements with the text: Test",
            null,
            "npm test failed");

        var hint = ExecutionDiagnosticEvidence.BuildTestFailureRepairHint(evidence, null, "Do not repeat it.");

        hint.Should().Contain("Do not repeat it.").And.Contain("getByRole").And.Contain("unique");
    }

    [Fact]
    public void RepairHint_ForOtherFailures_PassesEscalationThrough()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            "FAIL src/a.test.ts\nAssertionError: expected 1 to be 2", null, "npm test failed");

        ExecutionDiagnosticEvidence.BuildTestFailureRepairHint(evidence, null, null).Should().BeNull();
    }

    [Fact]
    public void TestFile_IsGeneratedAfterUnrelatedSourceFileOfSameTask()
    {
        var files = new[]
        {
            new ManifestFileEntry("src/App.test.tsx", FileEditAction.Modify),
            new ManifestFileEntry("src/components/NoteEditor.tsx", FileEditAction.Modify)
        };

        var edges = DeveloperAgent.CollectGenerationPrerequisites(files);

        edges.Should().ContainSingle(e =>
            e.ProducerPath == "src/components/NoteEditor.tsx" &&
            e.ConsumerPath == "src/App.test.tsx" &&
            e.Reason == GenerationPrerequisiteReason.TestAfterSource);
    }

    [Fact]
    public void JsdomMissingApi_IsClassifiedAndGetsEnvironmentHint()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            "FAIL src/App.test.tsx > bold\nError: Uncaught [TypeError: document.execCommand is not a function]\n    at applyFormat (src/components/NoteEditor.tsx:42:12)",
            null,
            "npm test failed");

        ExecutionDiagnosticEvidence.IsDomEnvironmentLimitationFailure(evidence).Should().BeTrue();
        ExecutionDiagnosticEvidence.BuildTestFailureRepairHint(evidence, null, null)
            .Should().Contain("jsdom").And.Contain("stub");
    }

    [Fact]
    public void OrdinaryAssertionFailure_IsNotClassifiedAsEnvironmentLimitation()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            "FAIL src/a.test.ts\nAssertionError: expected '<p>x</p>' to contain '<b>'", null, "npm test failed");

        ExecutionDiagnosticEvidence.IsDomEnvironmentLimitationFailure(evidence).Should().BeFalse();
    }
}
