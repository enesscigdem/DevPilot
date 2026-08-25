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
    public void CompilerErrors_ExplicitlyImplicatingTwoTouchedFiles_SelectBothAndNoMore()
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

        selected.Should().Equal("src/Todos/ITodoService.cs", "src/Todos/TodoService.cs");
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
    public void ScopeToFile_ExactTouchedFile_KeepsOnlyThatFilesDiagnostics()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            """
            src/app.ts(10,1): error TS2304: Cannot find name 'registerOrders'.
            src/orders.ts(4,14): error TS2552: Cannot find name 'Order'.
            src/app.ts(18,1): error TS2304: Cannot find name 'listen'.
            """,
            null,
            "npm build failed");

        var scoped = ExecutionDiagnosticEvidence.ScopeToFile(evidence, "src/app.ts");

        scoped.DiagnosticLines.Should().HaveCount(2);
        scoped.DiagnosticLines.Should().OnlyContain(line => line.Contains("src/app.ts"));
        scoped.DiagnosticLines.Should().NotContain(line => line.Contains("src/orders.ts"));
        scoped.Locations.Should().OnlyContain(location => location.FilePath.EndsWith("src/app.ts"));
    }

    [Fact]
    public void SelectNextCompilerRepairTarget_OneExactTouchedFile_SelectsThatFile()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            "src/Todos/TodoService.cs(42,17): error CS0103: The name 'filter' does not exist in the current context",
            null,
            "dotnet build failed");

        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            new[] { "src/Todos/ITodoService.cs", "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" });

        selection.FilePath.Should().Be("src/Todos/TodoService.cs");
        selection.Reason.Should().Be("NextUnattemptedFile");
        ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(
            evidence,
            new[] { "src/Todos/ITodoService.cs", "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" })
            .Should().Equal("src/Todos/TodoService.cs");
    }

    [Fact]
    public void SelectCompilerRepairFiles_TwoTouchedFiles_PreservesMasterTwoFileBehavior()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            """
            src/Todos/ITodoService.cs(9,12): error CS0246: The type 'TodoState' could not be found
            src/Todos/TodoService.cs(31,20): error CS0535: 'TodoService' does not implement interface member
            """,
            null,
            "dotnet build failed");

        var selected = ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(
            evidence,
            new[] { "src/Todos/ITodoService.cs", "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" });

        selected.Should().Equal("src/Todos/ITodoService.cs", "src/Todos/TodoService.cs");

        var next = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            new[] { "src/Todos/ITodoService.cs", "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" });
        next.FilePath.Should().Be("src/Todos/ITodoService.cs");
        next.ImplicatedFiles.Should().Equal("src/Todos/ITodoService.cs", "src/Todos/TodoService.cs");
    }

    [Fact]
    public void SelectNextCompilerRepairTarget_UncorrelatedDiagnostic_IsUncorrelated()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
            "src/Other/Startup.cs(10,5): error CS1002: ; expected",
            null,
            "dotnet build failed");

        var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
            evidence,
            new[] { "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" });

        selection.FilePath.Should().BeNull();
        selection.Reason.Should().Be("Uncorrelated");
        ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(
            evidence,
            new[] { "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" })
            .Should().BeEmpty();
    }

    [Fact]
    public void DiagnosticLineBelongsToFile_MatchesExactAndIgnoresOtherFiles()
    {
        const string appLine = "src/app.ts(10,1): error TS2304: Cannot find name 'registerOrders'.";
        const string otherLine = "src/orders.ts(4,14): error TS2552: Cannot find name 'Order'.";

        ExecutionDiagnosticEvidence.DiagnosticLineBelongsToFile(appLine, "src/app.ts").Should().BeTrue();
        ExecutionDiagnosticEvidence.DiagnosticLineBelongsToFile(otherLine, "src/app.ts").Should().BeFalse();
    }

    [Fact]
    public void SanitizeDiagnosticLinesForActivity_IsBoundedAndStripsWorkspaceRoot()
    {
        var lines = new[]
        {
            @"C:\work\repo\src\Todos\TodoService.cs(42,17): error CS0103: The name 'filter' does not exist in the current context " + new string('x', 80),
            "this is not a diagnostic",
            @"C:\work\repo\src\Todos\TodosController.cs(8,5): error CS0246: The type or namespace name 'TodoState' could not be found",
            @"C:\work\repo\src\Todos\ITodoService.cs(3,1): error CS0246: missing",
            @"C:\work\repo\src\A.cs(1,1): error CS0001: a",
            @"C:\work\repo\src\B.cs(1,1): error CS0002: b",
            @"C:\work\repo\src\C.cs(1,1): error CS0003: c"
        };

        var sanitized = ExecutionDiagnosticEvidence.SanitizeDiagnosticLinesForActivity(lines, @"C:\work\repo");

        sanitized.Should().HaveCount(5);
        sanitized.Should().OnlyContain(line => line.Length <= ExecutionDiagnosticEvidence.MaxSanitizedActivityDiagnosticChars);
        sanitized.Should().OnlyContain(line => !line.Contains(@"C:\work\repo", StringComparison.OrdinalIgnoreCase));
        sanitized[0].Should().Contain("src/Todos/TodoService.cs");
        sanitized.Should().NotContain("this is not a diagnostic");
    }

    [Fact]
    public void UntouchedFailingTest_WithOneImplicatedProductionFile_SelectsProductionNotTheTest()
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

        var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(
            evidence,
            new[] { "src/Todos/TodoService.cs", "src/Todos/TodosController.cs" },
            workspacePath: "/repo",
            fileContents: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tests/TodoServiceTests.cs"] = """
                    using Xunit;
                    public class TodoServiceTests
                    {
                        [Fact]
                        public void Filters_completed_todos()
                        {
                            var service = new TodoService();
                            Assert.Single(service.Filter(true));
                        }
                    }
                    """
            });

        selection.Reason.Should().Be("TouchedProductionFromUntouchedTest");
        selection.FilePaths.Should().Equal("src/Todos/TodoService.cs");
        selection.FilePaths.Should().NotContain("tests/TodoServiceTests.cs");
        selection.FailingTestName.Should().Be("DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos");
        selection.EvidenceLines.Should().Contain(line => line.Contains("Failed test:"));
    }

    [Fact]
    public void UntouchedFailingTest_AmbiguousProductionEvidence_RemainsUncorrelated()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed DevPilot.Tests.Todos.TodoFlowTests.Creates_todo [12 ms]
              Error Message:
               Assert.True() Failure
              Stack Trace:
                 at DevPilot.Tests.Todos.TodoFlowTests.Creates_todo() in /repo/tests/TodoFlowTests.cs:line 20
            """,
            null,
            "dotnet test failed");

        var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(
            evidence,
            new[] { "src/Todos/TodoService.cs", "src/Todos/TodosController.cs", "src/Todos/TodoRepository.cs" },
            workspacePath: "/repo",
            fileContents: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tests/TodoFlowTests.cs"] = """
                    public class TodoFlowTests
                    {
                        public void Creates_todo()
                        {
                            var service = new TodoService();
                            var controller = new TodosController();
                            var repository = new TodoRepository();
                        }
                    }
                    """
            });

        selection.Reason.Should().Be("Uncorrelated");
        selection.FilePaths.Should().BeEmpty();
    }

    [Fact]
    public void UntouchedFailingTest_DoesNotSelectTheTestFileJustToMakeItPass()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos [12 ms]
              Error Message:
               Expected result to contain 1 item, but found 2.
              Stack Trace:
                 at DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos() in /repo/tests/TodoServiceTests.cs:line 88
            """,
            null,
            "dotnet test failed");

        var selected = ExecutionDiagnosticEvidence.SelectTestRepairFiles(
            evidence,
            new[] { "src/Todos/TodoService.cs" },
            workspacePath: "/repo",
            fileContents: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tests/TodoServiceTests.cs"] = "public class TodoServiceTests { var s = new TodoService(); }"
            });

        selected.Should().Equal("src/Todos/TodoService.cs");
        selected.Should().NotContain(path => path.Contains("TodoServiceTests", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UntouchedFailingTest_ViaExactHelper_SelectsTouchedProduction()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed NetCase.Tests.ProductsApiTests.Get_Products_Should_Support_ETag_304 [18 ms]
              Error Message:
               Services for database providers Microsoft.EntityFrameworkCore.SqlServer and Microsoft.EntityFrameworkCore.InMemory have both been registered.
              Stack Trace:
                 at NetCase.Tests.ProductsApiTests.Get_Products_Should_Support_ETag_304() in /repo/tests/ProductsApiTests.cs:line 40
            """,
            null,
            "dotnet test failed");

        var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(
            evidence,
            new[] { "src/Program.cs", "src/appsettings.json" },
            workspacePath: "/repo",
            fileContents: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tests/ProductsApiTests.cs"] = """
                    using Xunit;
                    public class ProductsApiTests : IClassFixture<CustomWebApplicationFactory>
                    {
                        private readonly CustomWebApplicationFactory _factory;
                        public ProductsApiTests(CustomWebApplicationFactory factory) => _factory = factory;
                    }
                    """,
                ["tests/CustomWebApplicationFactory.cs"] = """
                    using Microsoft.AspNetCore.Mvc.Testing;
                    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
                    {
                        protected override void ConfigureWebHost(IWebHostBuilder builder) { }
                    }
                    """,
                ["src/Program.cs"] = "public class Program { public static void Main() {} }"
            });

        selection.Reason.Should().Be("TouchedProductionViaTestHelper");
        selection.FilePaths.Should().Equal("src/Program.cs");
        selection.FilePaths.Should().NotContain("tests/ProductsApiTests.cs");
    }

    [Fact]
    public void UntouchedFailingTest_AmbiguousHelperChain_RemainsUncorrelated()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed NetCase.Tests.ProductsApiTests.Get_Products [12 ms]
              Error Message:
               Assert.True() Failure
              Stack Trace:
                 at NetCase.Tests.ProductsApiTests.Get_Products() in /repo/tests/ProductsApiTests.cs:line 20
            """,
            null,
            "dotnet test failed");

        var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(
            evidence,
            new[] { "src/Program.cs", "src/Startup.cs" },
            workspacePath: "/repo",
            fileContents: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tests/ProductsApiTests.cs"] = """
                    public class ProductsApiTests
                    {
                        private readonly CustomWebApplicationFactory _factory;
                    }
                    """,
                ["tests/CustomWebApplicationFactory.cs"] = """
                    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
                    {
                        public Startup Startup { get; set; }
                    }
                    """,
                ["src/Program.cs"] = "public class Program {}",
                ["src/Startup.cs"] = "public class Startup {}"
            });

        selection.Reason.Should().Be("Uncorrelated");
        selection.FilePaths.Should().BeEmpty();
    }

    [Fact]
    public void VerificationEligibleSet_IncludesResolvedNoChangeWithoutTreatingItAsModified()
    {
        var eligible = ExecutionDiagnosticEvidence.BuildVerificationEligiblePlannedFiles(
            new[] { "src/Program.cs" },
            new[] { "tests/TestUtils/CustomWebApplicationFactory.cs" });

        eligible.Should().Equal("src/Program.cs", "tests/TestUtils/CustomWebApplicationFactory.cs");
        eligible.Should().Contain("tests/TestUtils/CustomWebApplicationFactory.cs");
    }

    [Fact]
    public void PlannedNoChangeAlone_IsInsufficientToSelectVerificationFile()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed NetCaseStudy.Tests.PipelineTests.Boots [12 ms]
              Error Message:
               Assert.True() Failure
              Stack Trace:
                 at NetCaseStudy.Tests.PipelineTests.Boots() in /repo/tests/PipelineTests.cs:line 20
            """,
            null,
            "dotnet test failed");

        var eligible = ExecutionDiagnosticEvidence.BuildVerificationEligiblePlannedFiles(
            new[] { "src/Program.cs" },
            new[] { "tests/TestUtils/CustomWebApplicationFactory.cs" });
        var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(
            evidence,
            eligible,
            workspacePath: "/repo",
            fileContents: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tests/PipelineTests.cs"] = """
                    public class PipelineTests
                    {
                        public void Boots()
                        {
                            Assert.True(false);
                        }
                    }
                    """
            });

        selection.Reason.Should().Be("Uncorrelated");
        selection.FilePaths.Should().BeEmpty();
    }

    [Fact]
    public void ExactFailingStack_CanSelectResolvedNoChangeVerificationFile()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed NetCaseStudy.Tests.PipelineTests.Boots [14 ms]
              Error Message:
               Services for database providers Microsoft.EntityFrameworkCore.SqlServer and Microsoft.EntityFrameworkCore.InMemory have been registered.
              Stack Trace:
                 at NetCaseStudy.Tests.TestUtils.CustomWebApplicationFactory.ConfigureWebHost() in /repo/tests/TestUtils/CustomWebApplicationFactory.cs:line 24
                 at NetCaseStudy.Tests.PipelineTests.Boots() in /repo/tests/PipelineTests.cs:line 18
            """,
            null,
            "dotnet test failed");

        var eligible = ExecutionDiagnosticEvidence.BuildVerificationEligiblePlannedFiles(
            new[] { "src/Program.cs" },
            new[] { "tests/TestUtils/CustomWebApplicationFactory.cs" });
        var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(evidence, eligible);

        selection.Reason.Should().Be("TouchedFileFromStack");
        selection.FilePaths.Should().Equal("tests/TestUtils/CustomWebApplicationFactory.cs");
    }

    [Fact]
    public void ExactFailingHelper_WithDirectLocalReference_CanSelectResolvedNoChangeFile()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed NetCaseStudy.Tests.PipelineTests.Boots [18 ms]
              Error Message:
               Services for database providers Microsoft.EntityFrameworkCore.SqlServer and Microsoft.EntityFrameworkCore.InMemory have been registered.
              Stack Trace:
                 at NetCaseStudy.Tests.PipelineTests.Boots() in /repo/tests/PipelineTests.cs:line 18
            """,
            null,
            "dotnet test failed");

        var eligible = ExecutionDiagnosticEvidence.BuildVerificationEligiblePlannedFiles(
            new[] { "src/Program.cs" },
            new[] { "tests/TestUtils/CustomWebApplicationFactory.cs" });
        var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(
            evidence,
            eligible,
            workspacePath: "/repo",
            fileContents: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tests/PipelineTests.cs"] = """
                    using Xunit;
                    public class PipelineTests : IClassFixture<CustomWebApplicationFactory>
                    {
                        private readonly CustomWebApplicationFactory _factory;
                        public PipelineTests(CustomWebApplicationFactory factory) => _factory = factory;
                    }
                    """,
                ["tests/TestUtils/CustomWebApplicationFactory.cs"] = """
                    using Microsoft.AspNetCore.Mvc.Testing;
                    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
                    {
                        protected override void ConfigureWebHost(IWebHostBuilder builder) { }
                    }
                    """,
                ["src/Program.cs"] = "public class Program { public static void Main() {} }"
            });

        selection.Reason.Should().Be("TouchedTestHelper");
        selection.FilePaths.Should().Equal("tests/TestUtils/CustomWebApplicationFactory.cs");
        selection.FilePaths.Should().NotContain("src/Program.cs");
    }

    [Fact]
    public void AmbiguousNoChangeCandidates_RemainUncorrelated()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed NetCaseStudy.Tests.PipelineTests.Boots [12 ms]
              Error Message:
               Assert.True() Failure
              Stack Trace:
                 at NetCaseStudy.Tests.PipelineTests.Boots() in /repo/tests/PipelineTests.cs:line 20
            """,
            null,
            "dotnet test failed");

        var eligible = ExecutionDiagnosticEvidence.BuildVerificationEligiblePlannedFiles(
            new[] { "src/Program.cs" },
            new[]
            {
                "tests/TestUtils/CustomWebApplicationFactory.cs",
                "tests/TestUtils/OtherWebApplicationFactory.cs"
            });
        var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(
            evidence,
            eligible,
            workspacePath: "/repo",
            fileContents: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tests/PipelineTests.cs"] = """
                    public class PipelineTests
                    {
                        private readonly CustomWebApplicationFactory _factory;
                        private readonly OtherWebApplicationFactory _other;
                    }
                    """,
                ["tests/TestUtils/CustomWebApplicationFactory.cs"] = """
                    public class CustomWebApplicationFactory : WebApplicationFactory<Program> { }
                    """,
                ["tests/TestUtils/OtherWebApplicationFactory.cs"] = """
                    public class OtherWebApplicationFactory : WebApplicationFactory<Program> { }
                    """,
                ["src/Program.cs"] = "public class Program {}"
            });

        selection.Reason.Should().Be("Uncorrelated");
        selection.FilePaths.Should().BeEmpty();
    }

    [Fact]
    public void OrdinaryModifiedFileTestRepair_RemainsUnchangedWhenNoChangeIsAbsent()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos [12 ms]
              Error Message:
               Expected result to contain 1 item, but found 2.
              Stack Trace:
                 at DevPilot.Todos.TodoService.Filter(Boolean completed) in /repo/src/Todos/TodoService.cs:line 41
            """,
            null,
            "dotnet test failed");

        var selected = ExecutionDiagnosticEvidence.SelectTestRepairFiles(
            evidence,
            new[] { "src/Todos/TodoService.cs", "src/Todos/Valid.cs" });

        selected.Should().Equal("src/Todos/TodoService.cs");
    }

    [Fact]
    public void SanitizeTestEvidenceForActivity_ExposesBoundedTestNameErrorAndStack()
    {
        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(
            """
            Failed DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos [12 ms]
              Error Message:
               Expected result to contain 1 item, but found 2.
              Stack Trace:
                 at DevPilot.Todos.TodoService.Filter(Boolean completed) in /repo/src/Todos/TodoService.cs:line 41
                 at DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos() in /repo/tests/TodoServiceTests.cs:line 88
            """,
            null,
            "dotnet test failed");

        var sanitized = ExecutionDiagnosticEvidence.SanitizeTestEvidenceForActivity(evidence);

        sanitized.Should().Contain(line => line.Contains("Failed test: DevPilot.Tests.Todos.TodoServiceTests.Filters_completed_todos"));
        sanitized.Should().Contain(line => line.Contains("Error: Expected result to contain 1 item, but found 2."));
        sanitized.Should().Contain(line => line.Contains("TodoService.cs:line 41"));
        sanitized.Should().HaveCountLessThanOrEqualTo(ExecutionDiagnosticEvidence.MaxSanitizedActivityDiagnosticLines);
        sanitized.Should().OnlyContain(line => line.Length <= ExecutionDiagnosticEvidence.MaxSanitizedActivityDiagnosticChars);
    }
}
