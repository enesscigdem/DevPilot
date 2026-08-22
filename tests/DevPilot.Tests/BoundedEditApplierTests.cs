using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class BoundedEditApplierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly WorktreeEditApplier _applier;

    public BoundedEditApplierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotBoundedEditTests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/test-bounded-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        InitGitRepo(_originalRepoDir);

        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Original Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");

        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _applier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_originalRepoDir))
            {
                RunGit(_originalRepoDir, "worktree", "prune");
            }
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    // 1. exact Replace succeeds
    [Fact]
    public void Contract_ExactReplace_Succeeds()
    {
        var original = "public class Calculator { public int Add(int a, int b) => a - b; }";
        var ops = new[]
        {
            BoundedEditOperation.Replace("a - b", "a + b")
        };

        var result = _applier.ApplyOperationsToContent(original, ops, "Calculator.cs");

        result.Success.Should().BeTrue();
        result.FailureReason.Should().Be(BoundedEditFailureReason.None);
        result.ModifiedContent.Should().Be("public class Calculator { public int Add(int a, int b) => a + b; }");
        result.TotalOperations.Should().Be(1);
    }

    // 2. exact Delete succeeds
    [Fact]
    public void Contract_ExactDelete_Succeeds()
    {
        var original = "public class Service { [Obsolete] public void Legacy() {} public void Modern() {} }";
        var ops = new[]
        {
            BoundedEditOperation.Delete("[Obsolete] public void Legacy() {} ")
        };

        var result = _applier.ApplyOperationsToContent(original, ops, "Service.cs");

        result.Success.Should().BeTrue();
        result.FailureReason.Should().Be(BoundedEditFailureReason.None);
        result.ModifiedContent.Should().Be("public class Service { public void Modern() {} }");
        result.TotalOperations.Should().Be(1);
    }

    // 3. InsertBefore succeeds
    [Fact]
    public void Contract_InsertBefore_Succeeds()
    {
        var original = "public class App\n{\n    public void Run() {}\n}";
        var ops = new[]
        {
            BoundedEditOperation.InsertBefore("    public void Run() {}\n", "    public void Init() {}\n")
        };

        var result = _applier.ApplyOperationsToContent(original, ops, "App.cs");

        result.Success.Should().BeTrue();
        result.FailureReason.Should().Be(BoundedEditFailureReason.None);
        result.ModifiedContent.Should().Be("public class App\n{\n    public void Init() {}\n    public void Run() {}\n}");
    }

    // 4. InsertAfter succeeds
    [Fact]
    public void Contract_InsertAfter_Succeeds()
    {
        var original = "public class App\n{\n    public void Run() {}\n}";
        var ops = new[]
        {
            BoundedEditOperation.InsertAfter("public void Run() {}\n", "    public void Shutdown() {}\n")
        };

        var result = _applier.ApplyOperationsToContent(original, ops, "App.cs");

        result.Success.Should().BeTrue();
        result.FailureReason.Should().Be(BoundedEditFailureReason.None);
        result.ModifiedContent.Should().Be("public class App\n{\n    public void Run() {}\n    public void Shutdown() {}\n}");
    }

    // 5. multiple ordered operations succeed
    [Fact]
    public void Contract_MultipleOrderedOperations_Succeed()
    {
        var original = "step1();\nstep2();\nstep3();";
        var ops = new[]
        {
            BoundedEditOperation.Replace("step1();", "stepA();"),
            BoundedEditOperation.Delete("step2();\n"),
            BoundedEditOperation.InsertAfter("step3();", "\nstep4();")
        };

        var result = _applier.ApplyOperationsToContent(original, ops, "Pipeline.cs");

        result.Success.Should().BeTrue();
        result.ModifiedContent.Should().Be("stepA();\nstep3();\nstep4();");
        result.TotalOperations.Should().Be(3);
    }

    // 6. second operation can target content introduced by first operation
    [Fact]
    public void Contract_SecondOperationCanTargetContentIntroducedByFirstOperation()
    {
        var original = "var x = 1;\nvar z = 3;";
        var ops = new[]
        {
            BoundedEditOperation.InsertAfter("var x = 1;\n", "var y = 2;\n"),
            BoundedEditOperation.Replace("var y = 2;", "var y = 20;")
        };

        var result = _applier.ApplyOperationsToContent(original, ops, "Sequence.cs");

        result.Success.Should().BeTrue();
        result.ModifiedContent.Should().Be("var x = 1;\nvar y = 20;\nvar z = 3;");
    }

    // 7. stale hash rejects entire edit
    [Fact]
    public async Task Apply_StaleHash_RejectsEntireEdit()
    {
        var relative = "stale_target.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        var originalContent = "public class Target { public int V = 1; }";
        await File.WriteAllTextAsync(fullPath, originalContent);

        var staleHash = "0000000000000000000000000000000000000000000000000000000000000000";
        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(relative, ExpectedFileHash: staleHash, Operations: new[]
            {
                BoundedEditOperation.Replace("int V = 1;", "int V = 2;")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.StaleSource);
        result.ErrorMessage.Should().Contain("stale target snapshot hash mismatch");
        (await File.ReadAllTextAsync(fullPath)).Should().Be(originalContent);
    }

    // 8. zero-match replace rejects entire edit
    [Fact]
    public async Task Apply_ZeroMatchReplace_RejectsEntireEdit()
    {
        var relative = "zero_replace.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        var originalContent = "public class Foo {}";
        await File.WriteAllTextAsync(fullPath, originalContent);

        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(relative, Operations: new[]
            {
                BoundedEditOperation.Replace("public class Bar", "public class Baz")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.AnchorNotFound);
        result.ErrorMessage.Should().Contain("Missing target match for Replace");
        result.FileEditResult.Should().NotBeNull();
        result.FileEditResult!.MatchCount.Should().Be(0);
        (await File.ReadAllTextAsync(fullPath)).Should().Be(originalContent);
    }

    // 9. ambiguous replace rejects entire edit
    [Fact]
    public async Task Apply_AmbiguousReplace_RejectsEntireEdit()
    {
        var relative = "ambiguous_replace.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        var originalContent = "int x = 1;\nint x = 1;\n";
        await File.WriteAllTextAsync(fullPath, originalContent);

        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(relative, Operations: new[]
            {
                BoundedEditOperation.Replace("int x = 1;", "int x = 2;")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.AmbiguousAnchor);
        result.ErrorMessage.Should().Contain("Ambiguous multiple target matches (2)");
        result.FileEditResult.Should().NotBeNull();
        result.FileEditResult!.MatchCount.Should().Be(2);
        (await File.ReadAllTextAsync(fullPath)).Should().Be(originalContent);
    }

    // 10. zero-match insertion anchor rejects
    [Fact]
    public async Task Apply_ZeroMatchInsertionAnchor_Rejects()
    {
        var relative = "zero_insert.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        var originalContent = "public class Foo {}";
        await File.WriteAllTextAsync(fullPath, originalContent);

        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(relative, Operations: new[]
            {
                BoundedEditOperation.InsertBefore("public class Bar", "// comment\n")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.AnchorNotFound);
        result.ErrorMessage.Should().Contain("Missing anchor match for InsertBefore");
        (await File.ReadAllTextAsync(fullPath)).Should().Be(originalContent);
    }

    // 11. ambiguous insertion anchor rejects
    [Fact]
    public async Task Apply_AmbiguousInsertionAnchor_Rejects()
    {
        var relative = "ambiguous_insert.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        var originalContent = "void Log();\nvoid Log();\n";
        await File.WriteAllTextAsync(fullPath, originalContent);

        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(relative, Operations: new[]
            {
                BoundedEditOperation.InsertAfter("void Log();", "\nvoid Flush();")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.AmbiguousAnchor);
        result.ErrorMessage.Should().Contain("Ambiguous multiple anchor matches (2)");
        (await File.ReadAllTextAsync(fullPath)).Should().Be(originalContent);
    }

    // 12. operation failure leaves file completely unchanged (atomic multi-op & multi-file)
    [Fact]
    public async Task Apply_OperationFailureInSecondFile_LeavesAllFilesCompletelyUnchanged()
    {
        var f1 = Path.Combine(_worktreeDir, "File1.cs");
        var f2 = Path.Combine(_worktreeDir, "File2.cs");
        var content1 = "class File1 { public int A = 1; }";
        var content2 = "class File2 { public int B = 1; }";
        await File.WriteAllTextAsync(f1, content1);
        await File.WriteAllTextAsync(f2, content2);

        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit("File1.cs", Operations: new[]
            {
                BoundedEditOperation.Replace("int A = 1;", "int A = 100;")
            }),
            new BoundedFileEdit("File2.cs", Operations: new[]
            {
                BoundedEditOperation.Replace("nonexistent target", "replacement")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.AnchorNotFound);
        (await File.ReadAllTextAsync(f1)).Should().Be(content1);
        (await File.ReadAllTextAsync(f2)).Should().Be(content2);
    }

    // 13. path traversal rejected
    [Fact]
    public async Task Apply_PathTraversal_Rejected()
    {
        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit("../outside.txt", Operations: new[]
            {
                BoundedEditOperation.Replace("old", "new")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.PathOutsideWorktree);
    }

    // 14. unauthorized target rejected
    [Fact]
    public async Task Apply_UnauthorizedTarget_Rejected()
    {
        var relative = "authorized_check.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        await File.WriteAllTextAsync(fullPath, "class Foo {}");

        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(relative, Operations: new[]
            {
                BoundedEditOperation.Replace("class Foo", "class Bar")
            })
        });

        var authorizedList = new[] { "other/path.cs" };

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan, authorizedRelativePaths: authorizedList);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.UnauthorizedTarget);
        result.ErrorMessage.Should().Contain("not in the authorized modification target list");
        (await File.ReadAllTextAsync(fullPath)).Should().Be("class Foo {}");
    }

    // 15. outside-worktree path rejected
    [Fact]
    public async Task Apply_OutsideWorktreePath_Rejected()
    {
        var absolutePath = Path.Combine(_tempDir, "outside.txt");
        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(absolutePath, Operations: new[]
            {
                BoundedEditOperation.Replace("a", "b")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.PathOutsideWorktree);
    }

    // 16. BOM preserved
    [Fact]
    public async Task Apply_BomPreserved()
    {
        var relative = "with_bom.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        var utf8WithBom = new UTF8Encoding(true);
        await File.WriteAllTextAsync(fullPath, "using System;\nclass Program {}", utf8WithBom);

        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(relative, Operations: new[]
            {
                BoundedEditOperation.Replace("class Program {}", "class ModernProgram {}")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeTrue();
        var bytes = await File.ReadAllBytesAsync(fullPath);
        WorktreeEditApplier.HasUtf8Bom(bytes).Should().BeTrue();
        var content = WorktreeEditApplier.DecodeUtf8Text(bytes, out var hasBom);
        hasBom.Should().BeTrue();
        content.Should().Be("using System;\nclass ModernProgram {}");
    }

    // 17. CRLF preserved
    [Fact]
    public async Task Apply_CrlfPreserved()
    {
        var relative = "crlf_target.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        await File.WriteAllTextAsync(fullPath, "public class Sample\r\n{\r\n    public int Value = 1;\r\n}\r\n");

        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(relative, Operations: new[]
            {
                BoundedEditOperation.Replace("int Value = 1;", "int Value = 42;")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeTrue();
        var written = await File.ReadAllTextAsync(fullPath);
        written.Should().Contain("\r\n");
        written.Should().Be("public class Sample\r\n{\r\n    public int Value = 42;\r\n}\r\n");
    }

    // 18. LF preserved
    [Fact]
    public async Task Apply_LfPreserved()
    {
        var relative = "lf_target.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        await File.WriteAllTextAsync(fullPath, "public class Sample\n{\n    public int Value = 1;\n}\n");

        var plan = new BoundedEditPlan(new[]
        {
            new BoundedFileEdit(relative, Operations: new[]
            {
                BoundedEditOperation.Replace("int Value = 1;", "int Value = 42;")
            })
        });

        var result = await _applier.ApplyBoundedEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeTrue();
        var written = await File.ReadAllTextAsync(fullPath);
        written.Should().NotContain("\r\n");
        written.Should().Be("public class Sample\n{\n    public int Value = 42;\n}\n");
    }

    // 19. final-newline behavior preserved
    [Fact]
    public void Contract_FinalNewlineBehavior_Preserved()
    {
        var withTrailing = "class A {}\n";
        var ops = new[] { BoundedEditOperation.Replace("class A", "class B") };
        var resWith = _applier.ApplyOperationsToContent(withTrailing, ops, "A.cs");
        resWith.ModifiedContent.Should().Be("class B {}\n");

        var withoutTrailing = "class A {}";
        var resWithout = _applier.ApplyOperationsToContent(withoutTrailing, ops, "A.cs");
        resWithout.ModifiedContent.Should().Be("class B {}");
    }

    // 20. operation-count bound enforced
    [Fact]
    public void Contract_OperationCountBound_Enforced()
    {
        var original = "item0;\n";
        var ops = Enumerable.Range(1, 10)
            .Select(i => BoundedEditOperation.InsertAfter($"item{i - 1};", $"\nitem{i};"))
            .ToList();

        var limits = new BoundedEditLimits(MaxOperationsPerFile: 5);
        var result = _applier.ApplyOperationsToContent(original, ops, "Items.cs", limits);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.MaxOperationsExceeded);
        result.ErrorMessage.Should().Contain("operation count (10) exceeds maximum limit (5)");
    }

    // 21. aggregate-content bound enforced
    [Fact]
    public void Contract_AggregateContentBound_Enforced()
    {
        var original = "var x = 1;";
        var largeContent = new string('A', 500);
        var ops = new[]
        {
            BoundedEditOperation.Replace("1", largeContent)
        };

        var limits = new BoundedEditLimits(MaxAggregateContentChars: 300);
        var result = _applier.ApplyOperationsToContent(original, ops, "Large.cs", limits);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(BoundedEditFailureReason.MaxContentSizeExceeded);
        result.ErrorMessage.Should().Contain("aggregate content size");
    }

    // Forward compatibility: JSON serialization and deserialization
    [Fact]
    public void Contract_JsonSerialization_RoundTripsAndDeserializesCompactOutput()
    {
        var json = """
            {
              "filePath": "src/App.cs",
              "expectedHash": "abc123hash",
              "operations": [
                { "type": "replace", "oldText": "int a = 1;", "newText": "int a = 2;" },
                { "type": "insertBefore", "anchor": "int b = 2;", "content": "// helper\n" },
                { "type": "insertAfter", "anchor": "int c = 3;", "content": "\nint d = 4;" },
                { "type": "delete", "oldText": "int e = 5;" }
              ]
            }
            """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var fileEdit = JsonSerializer.Deserialize<BoundedFileEdit>(json, options);

        fileEdit.Should().NotBeNull();
        fileEdit!.FilePath.Should().Be("src/App.cs");
        fileEdit.EffectiveExpectedHash.Should().Be("abc123hash");
        fileEdit.Operations.Should().HaveCount(4);

        fileEdit.Operations![0].Type.Should().Be(BoundedEditOperationType.Replace);
        fileEdit.Operations[0].TargetText.Should().Be("int a = 1;");
        fileEdit.Operations[0].ReplacementText.Should().Be("int a = 2;");

        fileEdit.Operations[1].Type.Should().Be(BoundedEditOperationType.InsertBefore);
        fileEdit.Operations[1].TargetText.Should().Be("int b = 2;");
        fileEdit.Operations[1].ReplacementText.Should().Be("// helper\n");

        fileEdit.Operations[2].Type.Should().Be(BoundedEditOperationType.InsertAfter);
        fileEdit.Operations[2].TargetText.Should().Be("int c = 3;");
        fileEdit.Operations[2].ReplacementText.Should().Be("\nint d = 4;");

        fileEdit.Operations[3].Type.Should().Be(BoundedEditOperationType.Delete);
        fileEdit.Operations[3].TargetText.Should().Be("int e = 5;");
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "Test User");
        RunGit(path, "config", "user.email", "test@example.com");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
