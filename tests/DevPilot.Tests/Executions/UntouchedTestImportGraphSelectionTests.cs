using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class UntouchedTestImportGraphSelectionTests
{
    private const string VitestOutput = """
         ❯ src/App.test.tsx (14 tests | 1 failed) 52ms
           × App > opens the add task dialog 20ms
             → Unable to find an element with the role "button" and name /add task/i
         FAIL  src/App.test.tsx > App > opens the add task dialog
        TestingLibraryElementError: Unable to find an element with the role "button" and name /add task/i
         ❯ Object.getElementError node_modules/@testing-library/dom/dist/config.js:37:19
         ❯ src/App.test.tsx:45:30
        """;

    private static readonly string[] Planned =
    {
        "src/index.css", "src/App.tsx",
        "src/components/BoardColumn.tsx", "src/components/TaskCard.tsx", "src/components/AddTaskDialog.tsx"
    };

    private static readonly Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/App.test.tsx"] = "import { render } from '@testing-library/react';\nimport App from './App';\n",
        ["src/App.tsx"] = "import BoardColumn from './components/BoardColumn';\nimport AddTaskDialog from './components/AddTaskDialog';\nimport './index.css';\n"
    };

    [Fact]
    public void UntouchedTest_FailsAfterUiChange_SelectsOnlyDirectlyImportedTouchedFile()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(VitestOutput, null, "npm test failed");

        var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(
            evidence, Planned, null, Sources);

        selection.Reason.Should().NotBe("Uncorrelated");
        selection.FilePaths.Should().Contain("src/App.tsx");
        selection.FilePaths.Should().NotContain("src/index.css");
        selection.FilePaths.Count.Should().BeLessThanOrEqualTo(2);
    }
}
