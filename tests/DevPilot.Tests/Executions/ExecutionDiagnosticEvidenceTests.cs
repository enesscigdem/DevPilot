using DevPilot.Application.Executions.Ports;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class ExecutionDiagnosticEvidenceTests
{
    [Fact]
    public void CompilerError_ExactLocation_SelectsOneTouchedFile()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            "src/Todos/TodoService.cs(42,17): error CS0103: The name 'filter' does not exist in the current context",
            null,
            "dotnet build failed");

        var selected = ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(
            evidence,
            new[] { "src/Todos/ITodoService.cs", "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" });

        selected.Should().Equal("src/Todos/TodoService.cs");
        evidence.Locations.Should().ContainSingle(location => location.Line == 42 && location.Column == 17);
    }

    [Fact]
    public void CompilerErrors_ExplicitlyImplicatingTwoTouchedFiles_SelectsHighestConfidenceFileOnly()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            """
            src/Todos/ITodoService.cs(9,12): error CS0246: The type 'TodoState' could not be found
            src/Todos/TodoService.cs(31,20): error CS0535: 'TodoService' does not implement interface member
            src/Todos/TodosController.cs(18,9): warning CS0168: Variable is never used
            """,
            null,
            "dotnet build failed");

        var selected = ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(
            evidence,
            new[] { "src/Todos/ITodoService.cs", "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" });

        selected.Should().Equal("src/Todos/ITodoService.cs");
    }

    [Fact]
    public void CompilerError_WithoutTouchedFileCorrelation_DoesNotFallbackBroadly()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            "src/Other/Startup.cs(10,5): error CS1002: ; expected",
            null,
            "dotnet build failed");

        var selected = ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(
            evidence,
            new[] { "src/Todos/ITodoService.cs", "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" });

        selected.Should().BeEmpty();
    }

    [Fact]
    public void SelectNext_SeventeenDiagnosticsAcrossMultipleFiles_RepairsFirstFileOnly()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(SeventeenDiagnostics(), null, "build failed");
        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            new[] { "src/App.cs", "src/Other.cs", "src/Valid.cs" },
            Array.Empty<string>(),
            lastRepairChangedFile: false,
            finalDiagnosticAttemptUsed: false);

        evidence.DiagnosticLines.Should().HaveCount(17);
        selection.ImplicatedFiles.Should().Equal("src/App.cs", "src/Other.cs");
        selection.FilePath.Should().Be("src/App.cs");
        selection.IsFinalDiagnosticAttempt.Should().BeFalse();
        selection.ScopeExpanded.Should().BeFalse();
        selection.Decision.Should().Be("NextUnattemptedFile");
    }

    [Fact]
    public void SelectNext_SameDiagnosticsAfterChangedFileA_SelectsExactFileBNotA()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(SeventeenDiagnostics(), null, "build failed");
        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            new[] { "src/App.cs", "src/Other.cs" },
            new[] { "src/App.cs" },
            lastRepairChangedFile: true,
            finalDiagnosticAttemptUsed: false);

        selection.FilePath.Should().Be("src/Other.cs");
        selection.IsFinalDiagnosticAttempt.Should().BeFalse();
        selection.Decision.Should().Be("NextUnattemptedFile");
    }

    [Fact]
    public void SelectNext_DoesNotUseFinalSameFileRepairWhileUnattemptedFileExists()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(SeventeenDiagnostics(), null, "build failed");
        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            new[] { "src/App.cs", "src/Other.cs" },
            new[] { "src/App.cs" },
            lastRepairChangedFile: true,
            finalDiagnosticAttemptUsed: false);

        selection.FilePath.Should().NotBe("src/App.cs");
        selection.IsFinalDiagnosticAttempt.Should().BeFalse();
    }

    [Fact]
    public void SelectNext_SingleRemainingDiagnosticFile_MayReceiveFinalDiagnosticRepair()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            "src/App.cs(8,3): error CS1002: ; expected",
            null,
            "build failed");
        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            new[] { "src/App.cs", "src/Valid.cs" },
            new[] { "src/App.cs" },
            lastRepairChangedFile: true,
            finalDiagnosticAttemptUsed: false);

        selection.FilePath.Should().Be("src/App.cs");
        selection.IsFinalDiagnosticAttempt.Should().BeTrue();
        selection.Decision.Should().Be("FinalSingleFile");
    }

    [Fact]
    public void SelectNext_ModelCannotIntroduceArbitraryRepairFiles()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            "src/App.cs(8,3): error CS1002: ; expected",
            null,
            "build failed");
        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            new[] { "src/App.cs", "src/GuessedByModel.cs", "src/AnotherGuess.cs" },
            Array.Empty<string>(),
            lastRepairChangedFile: false,
            finalDiagnosticAttemptUsed: false);

        selection.FilePath.Should().Be("src/App.cs");
        selection.ImplicatedFiles.Should().NotContain("src/GuessedByModel.cs");
        selection.ImplicatedFiles.Should().NotContain("src/AnotherGuess.cs");
    }

    private static string SeventeenDiagnostics()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 9; i++)
        {
            lines.Add($"src/App.cs({i},1): error CS100{i % 10}: diagnostic {i}");
        }

        for (var i = 1; i <= 8; i++)
        {
            lines.Add($"src/Other.cs({i},1): error CS200{i}: diagnostic {i}");
        }

        return string.Join('\n', lines);
    }

    [Fact]
    public void TypeScriptDiagnostic_UsesTheSameFocusedFileCorrelationContract()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseVerificationFailure(
            "src/todos/todo-service.ts(14,9): error TS2322: Type 'string' is not assignable to type 'boolean'.",
            null,
            "npm build failed");

        var selected = ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(
            evidence,
            new[] { "src/todos/todo-service.ts", "src/todos/todo-controller.ts" });

        selected.Should().Equal("src/todos/todo-service.ts");
        evidence.Locations.Should().ContainSingle(location => location.Line == 14 && location.Column == 9);
    }

    [Fact]
    public void FailingTest_WithTestStackLocation_SelectsOnlyLikelyTouchedTestFile()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos [12 ms]
              Error Message:
               Expected result to contain 1 item, but found 2.
              Stack Trace:
                 at DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos() in /repo/tests/TodoServiceTests.cs:line 88
            Failed! - Failed: 1, Passed: 42, Skipped: 0, Total: 43
            """,
            null,
            "dotnet test failed");

        var selected = ExecutionDiagnosticEvidence.SelectTestRepairFiles(
            evidence,
            new[] { "src/Todos/TodoService.cs", "tests/TodoServiceTests.cs", "tests/TodosControllerTests.cs" });

        evidence.TestName.Should().Be("DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos");
        selected.Should().Equal("tests/TodoServiceTests.cs");
    }

    [Fact]
    public void FailingTest_PreservesRelevantStackFramesAndFileLines()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos [12 ms]
              Error Message:
               TodoServiceTests.cs detected an invalid result from TodoService.cs.
              Stack Trace:
                 at DevPilot.Todos.TodoService.Filter(Boolean completed) in /repo/src/Todos/TodoService.cs:line 41
                 at DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos() in /repo/tests/TodoServiceTests.cs:line 88
            Failed! - Failed: 1, Passed: 42, Skipped: 0, Total: 43
            """,
            null,
            "dotnet test failed");

        evidence.RelevantLines.Should().Contain(line => line.Contains("TodoService.cs:line 41"));
        evidence.RelevantLines.Should().Contain(line => line.Contains("TodoServiceTests.cs:line 88"));
        evidence.Locations.Should().Contain(location => location.FilePath.EndsWith("src/Todos/TodoService.cs") && location.Line == 41);
        evidence.Locations.Should().Contain(location => location.FilePath.EndsWith("tests/TodoServiceTests.cs") && location.Line == 88);

        var selected = ExecutionDiagnosticEvidence.SelectTestRepairFiles(
            evidence,
            new[] { "src/Todos/TodoService.cs", "tests/TodoServiceTests.cs", "tests/TodosControllerTests.cs" });
        selected.Should().HaveCount(2);
        selected.Should().Contain("src/Todos/TodoService.cs");
        selected.Should().Contain("tests/TodoServiceTests.cs");
    }

    [Fact]
    public void JavaScriptTestStack_PreservesTouchedSourceLocation()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            FAIL src/todos/todo-service.test.ts
            Expected: true
            Received: false
                at filtersCompleted (src/todos/todo-service.test.ts:22:7)
                at Object.<anonymous> (src/todos/todo-service.ts:41:3)
            """,
            null,
            "npm test failed");

        var selected = ExecutionDiagnosticEvidence.SelectTestRepairFiles(
            evidence,
            new[] { "src/todos/todo-service.test.ts", "src/todos/todo-service.ts", "src/valid.ts" });

        evidence.Locations.Should().Contain(location =>
            location.FilePath.EndsWith("todo-service.test.ts") && location.Line == 22);
        selected.Should().Contain("src/todos/todo-service.test.ts");
        selected.Should().NotContain("src/valid.ts");
    }

    [Fact]
    public void PythonTraceback_PreservesFileAndLineForFocusedRepair()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            AssertionError: expected completed todo
              File "/repo/tests/test_todos.py", line 18, in test_filters_completed
              File "/repo/src/todos.py", line 42, in filter_todos
            """,
            null,
            "python pytest failed");

        var selected = ExecutionDiagnosticEvidence.SelectTestRepairFiles(
            evidence,
            new[] { "tests/test_todos.py", "src/todos.py", "src/valid.py" });

        evidence.Locations.Should().Contain(location =>
            location.FilePath.EndsWith("tests/test_todos.py") && location.Line == 18);
        selected.Should().Contain("tests/test_todos.py");
        selected.Should().NotContain("src/valid.py");
    }

    [Fact]
    public void EnglishTestOutput_ParsesTestNameCorrectly()
    {
        var stdout = """
            Failed Namespace.Tests.SomeTest [12ms]
              Error Message:
               Assert.True() Failure
              Stack Trace:
                 at Namespace.Tests.SomeTest() in /repo/tests/SomeTest.cs:line 42
            """;

        var failures = ExecutionDiagnosticEvidence.ParseAllTestFailures(stdout, null, null);

        failures.Should().ContainSingle();
        failures[0].TestName.Should().Be("Namespace.Tests.SomeTest");
        failures[0].ErrorSummary.Should().Be("Assert.True() Failure");
    }

    [Fact]
    public void LocalizedTurkishOutput_ParsesTestNameAndErrorCorrectly()
    {
        var stdout = """
            [xUnit.net 00:00:01.23]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Başarısız TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [260 ms]
              Hata İletisi:
               Intentional pre-existing fixture failure.
              Yığın İzleme:
                 at TodoApp.Tests.LegacyCalculatorTests.AlwaysFails() in C:\Users\enesc\OneDrive\Desktop\Desktop\projects\DevPilot.PreExistingFailureFixture\tests\TodoApp.Tests\LegacyCalculatorTests.cs:line 10
               at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
            Başarısız! - Başarısız:     1, Başarılı:     2, Atlanan:     0, Toplam:     3, Süre: 277 ms - TodoApp.Tests.dll (net10.0)
            """;

        var failures = ExecutionDiagnosticEvidence.ParseAllTestFailures(stdout, null, null, workspaceRoot: @"C:\Users\enesc\OneDrive\Desktop\Desktop\projects\DevPilot.PreExistingFailureFixture");

        failures.Should().ContainSingle();
        failures[0].TestName.Should().Be("TodoApp.Tests.LegacyCalculatorTests.AlwaysFails");
        failures[0].ErrorSummary.Should().Be("Intentional pre-existing fixture failure.");
        failures[0].Location.Should().Be("tests/TodoApp.Tests/LegacyCalculatorTests.cs");
    }

    [Fact]
    public void DifferentXUnitTimestamps_ProduceSameNormalizedFailureIdentity()
    {
        var output1 = """
            [xUnit.net 00:00:01.23]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Başarısız TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [260 ms]
              Hata İletisi:
               Intentional pre-existing fixture failure.
              Yığın İzleme:
                 at TodoApp.Tests.LegacyCalculatorTests.AlwaysFails() in C:\repo\tests\LegacyCalculatorTests.cs:line 10
            """;

        var output2 = """
            [xUnit.net 00:00:04.99]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Başarısız TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [15 ms]
              Hata İletisi:
               Intentional pre-existing fixture failure.
              Yığın İzleme:
                 at TodoApp.Tests.LegacyCalculatorTests.AlwaysFails() in C:\repo\tests\LegacyCalculatorTests.cs:line 10
            """;

        var f1 = ExecutionDiagnosticEvidence.ParseAllTestFailures(output1, null, null, workspaceRoot: @"C:\repo")[0];
        var f2 = ExecutionDiagnosticEvidence.ParseAllTestFailures(output2, null, null, workspaceRoot: @"C:\repo")[0];

        f1.FailureKey.Should().Be(f2.FailureKey);
        f1.NormalizedDiagnostic.Should().Be(f2.NormalizedDiagnostic);
    }

    [Fact]
    public void DifferentWorktreePaths_ProduceSameNormalizedFailureIdentity()
    {
        var taskOutput = """
            [xUnit.net 00:00:01.23]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Başarısız TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [260 ms]
              Hata İletisi:
               Intentional pre-existing fixture failure.
              Yığın İzleme:
                 at TodoApp.Tests.LegacyCalculatorTests.AlwaysFails() in C:\Users\enesc\.devpilot\workspaces\task123\tests\LegacyCalculatorTests.cs:line 10
            """;

        var baselineOutput = """
            [xUnit.net 00:00:00.88]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Başarısız TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [120 ms]
              Hata İletisi:
               Intentional pre-existing fixture failure.
              Yığın İzleme:
                 at TodoApp.Tests.LegacyCalculatorTests.AlwaysFails() in C:\Users\enesc\.devpilot\baselines\base456\tests\LegacyCalculatorTests.cs:line 10
            """;

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllTestFailures(taskOutput, null, null, workspaceRoot: @"C:\Users\enesc\.devpilot\workspaces\task123");
        var baselineFailures = ExecutionDiagnosticEvidence.ParseAllTestFailures(baselineOutput, null, null, workspaceRoot: @"C:\Users\enesc\.devpilot\baselines\base456");

        taskFailures[0].FailureKey.Should().Be(baselineFailures[0].FailureKey);
        taskFailures[0].Location.Should().Be("tests/LegacyCalculatorTests.cs");
        baselineFailures[0].Location.Should().Be("tests/LegacyCalculatorTests.cs");
    }

    [Fact]
    public void TurkishFixtureTaskFailure_Vs_BaselineFailure_ClassifiesPreExisting()
    {
        var taskOutput = """
            [xUnit.net 00:00:01.23]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Başarısız TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [260 ms]
              Hata İletisi:
               Intentional pre-existing fixture failure.
              Yığın İzleme:
                 at TodoApp.Tests.LegacyCalculatorTests.AlwaysFails() in C:\Users\enesc\.devpilot\workspaces\task123\tests\LegacyCalculatorTests.cs:line 10
            """;

        var baselineOutput = """
            [xUnit.net 00:00:00.88]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Başarısız TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [120 ms]
              Hata İletisi:
               Intentional pre-existing fixture failure.
              Yığın İzleme:
                 at TodoApp.Tests.LegacyCalculatorTests.AlwaysFails() in C:\Users\enesc\.devpilot\baselines\base456\tests\LegacyCalculatorTests.cs:line 10
            """;

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllTestFailures(taskOutput, null, null, workspaceRoot: @"C:\Users\enesc\.devpilot\workspaces\task123");
        var baselineFailures = ExecutionDiagnosticEvidence.ParseAllTestFailures(baselineOutput, null, null, workspaceRoot: @"C:\Users\enesc\.devpilot\baselines\base456");

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures,
            baselineFailures,
            baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.PreExisting);
        comparison.PreExistingCount.Should().Be(1);
        comparison.NewRegressionCount.Should().Be(0);
    }

    [Fact]
    public void GenuinelyDifferentNewFailingTest_ClassifiesAsNewRegression()
    {
        var taskOutput = """
            [xUnit.net 00:00:01.23]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Hata İletisi:
               Intentional pre-existing fixture failure.
            [xUnit.net 00:00:01.45]     TodoApp.Tests.TodoServiceTests.AddTodo_IncreasesCount [FAIL]
              Hata İletisi:
               Assert.Equal() Failure: Expected 1, Actual 0
            """;

        var baselineOutput = """
            [xUnit.net 00:00:00.88]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Hata İletisi:
               Intentional pre-existing fixture failure.
            """;

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllTestFailures(taskOutput, null, null);
        var baselineFailures = ExecutionDiagnosticEvidence.ParseAllTestFailures(baselineOutput, null, null);

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures,
            baselineFailures,
            baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.NewRegression);
        comparison.PreExistingCount.Should().Be(1);
        comparison.NewRegressionCount.Should().Be(1);
        comparison.NewRegressions.Should().ContainSingle(f => f.TestName == "TodoApp.Tests.TodoServiceTests.AddTodo_IncreasesCount");
    }

    [Fact]
    public void ChangedAssertionError_SameTest_ClassifiesAsChangedRegression()
    {
        var taskOutput = """
            [xUnit.net 00:00:01.23]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Hata İletisi:
               New modified assertion failure.
            """;

        var baselineOutput = """
            [xUnit.net 00:00:00.88]     TodoApp.Tests.LegacyCalculatorTests.AlwaysFails [FAIL]
              Hata İletisi:
               Intentional pre-existing fixture failure.
            """;

        var taskFailures = ExecutionDiagnosticEvidence.ParseAllTestFailures(taskOutput, null, null);
        var baselineFailures = ExecutionDiagnosticEvidence.ParseAllTestFailures(baselineOutput, null, null);

        var comparison = ExecutionDiagnosticEvidence.CompareFailureSets(
            taskFailures,
            baselineFailures,
            baselineCheckSucceeded: false);

        comparison.Classification.Should().Be(BaselineFailureClassification.ChangedRegression);
        comparison.PreExistingCount.Should().Be(0);
        comparison.ChangedCount.Should().Be(1);
        comparison.ChangedFailures.Should().ContainSingle(f => f.TestName == "TodoApp.Tests.LegacyCalculatorTests.AlwaysFails");
    }

    [Fact]
    public void SelectNext_UniqueCompilerPathOutsideModifiedFiles_ExpandsScopeByOne()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            WriteSource(workspace, "src/app.ts", "export const app = 1;");
            WriteSource(workspace, "src/config.ts", "export const config = 1;");

            var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
                "src/config.ts(1,14): error TS2322: Type 'number' is not assignable to type 'string'.",
                null,
                "npm build failed");
            var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
                evidence,
                new[] { "src/app.ts" },
                Array.Empty<string>(),
                lastRepairChangedFile: false,
                finalDiagnosticAttemptUsed: false,
                workspace);

            selection.FilePath.Should().Be("src/config.ts");
            selection.ScopeExpanded.Should().BeTrue();
            selection.IsFinalDiagnosticAttempt.Should().BeFalse();
            selection.Decision.Should().Be("CompilerScopeExpanded");
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void SelectNext_AmbiguousNonexistentOrOutsideWorkspacePath_CannotExpandScope()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            WriteSource(workspace, "src/app.ts", "export const app = 1;");
            WriteSource(workspace, "src/a.ts", "export const a = 1;");
            WriteSource(workspace, "src/b.ts", "export const b = 1;");

            var ambiguous = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
                ExecutionDiagnosticEvidence.ParseCompilerFailure(
                    "src/a.ts(1,1): error TS2322: a\nsrc/b.ts(1,1): error TS2322: b",
                    null,
                    "failed"),
                new[] { "src/app.ts" },
                Array.Empty<string>(),
                lastRepairChangedFile: false,
                finalDiagnosticAttemptUsed: false,
                workspace);
            ambiguous.FilePath.Should().BeNull();
            ambiguous.ScopeExpanded.Should().BeFalse();
            ambiguous.Decision.Should().Be("Uncorrelated");

            var missing = ExecutionDiagnosticEvidence.TryExpandCompilerRepairScope(
                ExecutionDiagnosticEvidence.ParseCompilerFailure(
                    "src/missing.ts(1,1): error TS2322: missing",
                    null,
                    "failed"),
                new[] { "src/app.ts" },
                workspace);
            missing.Should().BeNull();

            var outside = ExecutionDiagnosticEvidence.TryExpandCompilerRepairScope(
                ExecutionDiagnosticEvidence.ParseCompilerFailure(
                    "../outside.ts(1,1): error TS2322: escaped",
                    null,
                    "failed"),
                new[] { "src/app.ts" },
                workspace);
            outside.Should().BeNull();

            var absolute = ExecutionDiagnosticEvidence.TryResolveExactCompilerRepairPath(workspace, "C:\\Windows\\System32\\kernel.ts");
            absolute.Should().BeNull();
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    private static string CreateTempWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevPilotExpand_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteSource(string workspace, string relativePath, string content)
    {
        var full = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
