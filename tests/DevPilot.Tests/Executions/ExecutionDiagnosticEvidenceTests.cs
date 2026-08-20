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
}
