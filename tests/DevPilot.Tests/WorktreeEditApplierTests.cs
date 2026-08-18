using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class WorktreeEditApplierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly WorktreeEditApplier _applier;

    public WorktreeEditApplierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotTests_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/test-execution-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);

        // Setup real temporary Git repositories for accurate Git branch validation
        InitGitRepo(_originalRepoDir);

        // Create a dummy file in original repo
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Original Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");

        // Create worktree on specified branch
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
            // Ignore cleanup errors in temporary directory
        }
    }

    [Fact]
    public void ValidateAndResolvePath_ValidRelativePath_ReturnsCanonicalPathInsideWorkspace()
    {
        var relative = "src/SubFolder/File.cs";
        var resolved = WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, relative);

        resolved.Should().StartWith(WorktreeEditApplier.GetCanonicalRealPath(_worktreeDir));
        resolved.Should().EndWith("File.cs");
    }

    [Fact]
    public void ValidateAndResolvePath_AbsolutePath_ThrowsInvalidOperationException()
    {
        var absolute = Path.Combine(_tempDir, "outside.txt");
        var act = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, absolute);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Absolute paths are rejected*");
    }

    [Theory]
    [InlineData("src/foo/bar.cs")]
    [InlineData(@"src\foo\bar.cs")]
    [InlineData(@"\src\foo\bar.cs")]
    public void ValidateAndResolvePath_ValidRelativeVariants_AcceptedOnAllHosts(string validPath)
    {
        var resolved = WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, validPath);
        resolved.Should().StartWith(WorktreeEditApplier.GetCanonicalRealPath(_worktreeDir));
        resolved.Should().EndWith("bar.cs");
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData(@"..\secret.txt")]
    [InlineData("src/../../secret.txt")]
    [InlineData(@"src\..\..\secret.txt")]
    [InlineData(@"C:\secret.txt")]
    [InlineData("C:/secret.txt")]
    [InlineData(@"\\server\share\secret.txt")]
    [InlineData("//server/share/secret.txt")]
    public void ValidateAndResolvePath_UnsafeOrAbsolutePaths_RejectedOnAllHosts(string unsafePath)
    {
        var act = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, unsafePath);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ValidateAndResolvePath_SiblingPrefixEscape_ThrowsInvalidOperationException()
    {
        var siblingPath = Path.Combine(_tempDir, "worktree_sibling", "secret.txt");
        var act = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, siblingPath);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Absolute paths are rejected*");
    }

    [Fact]
    public void ValidateAndResolvePath_ParentTraversal_ThrowsInvalidOperationException()
    {
        var traversal = "../outside.txt";
        var act = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, traversal);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Path safety violation*");
    }

    [Fact]
    public void ValidateAndResolvePath_GitDirectoryOrFile_ThrowsInvalidOperationException()
    {
        var gitPath = ".git/config";
        var act = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, gitPath);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Modification of .git directory or files is rejected*");
    }

    [Fact]
    public void ValidateAndResolvePath_SensitiveFiles_ThrowsInvalidOperationException()
    {
        var envPath = ".env";
        var act1 = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, envPath);
        act1.Should().Throw<InvalidOperationException>().WithMessage("*sensitive configuration/credential file*");

        var pemPath = "certs/private.pem";
        var act2 = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, pemPath);
        act2.Should().Throw<InvalidOperationException>().WithMessage("*sensitive configuration/credential file*");
    }

    [Fact]
    public void ValidateAndResolvePath_SymlinkPointingOutsideWorkspace_ThrowsInvalidOperationException()
    {
        var outsideTargetDir = Path.Combine(_tempDir, "outside_dir");
        Directory.CreateDirectory(outsideTargetDir);
        var outsideFile = Path.Combine(outsideTargetDir, "secret.txt");
        File.WriteAllText(outsideFile, "outside secret");

        var symlinkPath = Path.Combine(_worktreeDir, "symlink_outside");

        try
        {
            Directory.CreateSymbolicLink(symlinkPath, outsideTargetDir);
        }
        catch
        {
            // If symlinks cannot be created without admin privileges on OS, skip symlink test assertion
            return;
        }

        var act = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, "symlink_outside/secret.txt");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Path safety violation*");
    }

    [Fact]
    public async Task ApplyEdits_StrictCreate_FileAlreadyExists_FailsWithoutModifyingFile()
    {
        var existingFile = Path.Combine(_worktreeDir, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "initial content");

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("existing.txt", Action: FileEditAction.Create, NewContent: "new content")
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Strict Create action failed: file already exists");
        (await File.ReadAllTextAsync(existingFile)).Should().Be("initial content");
    }

    [Fact]
    public async Task ApplyEdits_StrictModify_FileDoesNotExist_FailsWithoutCreatingFile()
    {
        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("nonexistent.txt", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("old", "new")
            })
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Strict Modify action failed: target file does not exist");
        File.Exists(Path.Combine(_worktreeDir, "nonexistent.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyEdits_MissingSearchText_FailsAndDoesNotModifyFile()
    {
        var file = Path.Combine(_worktreeDir, "code.cs");
        await File.WriteAllTextAsync(file, "public class Foo {}");

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("code.cs", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("public class Bar", "public class Baz")
            })
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Missing search match in 'code.cs'");
        (await File.ReadAllTextAsync(file)).Should().Be("public class Foo {}");
    }

    [Fact]
    public async Task ApplyEdits_AmbiguousMultipleSearchMatches_FailsAndDoesNotModifyFile()
    {
        var file = Path.Combine(_worktreeDir, "code.cs");
        await File.WriteAllTextAsync(file, "int x = 1;\nint x = 2;");

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("code.cs", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("int x", "int y")
            })
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Ambiguous multiple search matches (2) in 'code.cs'");
        (await File.ReadAllTextAsync(file)).Should().Be("int x = 1;\nint x = 2;");
    }

    [Fact]
    public async Task ApplyEdits_SequentialEdits_AppliedInEvolvingOrder()
    {
        var file = Path.Combine(_worktreeDir, "code.cs");
        await File.WriteAllTextAsync(file, "alpha beta gamma");

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("code.cs", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("alpha", "delta"),
                new SearchReplaceEdit("delta beta", "omega")
            })
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeTrue();
        (await File.ReadAllTextAsync(file)).Should().Be("omega gamma");
    }

    [Fact]
    public async Task ApplyEdits_ValidEdit_ModifiesOnlyWorktree_OriginalRepoUnchanged()
    {
        var fileInWorktree = Path.Combine(_worktreeDir, "app.cs");
        await File.WriteAllTextAsync(fileInWorktree, "class Program { static void Main() {} }");

        var originalFile = Path.Combine(_originalRepoDir, "app.cs");
        await File.WriteAllTextAsync(originalFile, "class OriginalProgram {}");

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("app.cs", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("class Program", "class UpdatedProgram")
            })
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeTrue();
        (await File.ReadAllTextAsync(fileInWorktree)).Should().Be("class UpdatedProgram { static void Main() {} }");
        (await File.ReadAllTextAsync(originalFile)).Should().Be("class OriginalProgram {}");
    }

    [Fact]
    public async Task ApplyEdits_MultiFileChangeSet_InvalidSecondFile_CausesNoPartialEdits()
    {
        var file1 = Path.Combine(_worktreeDir, "file1.txt");
        await File.WriteAllTextAsync(file1, "file 1 content");

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("file1.txt", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("file 1 content", "modified 1 content")
            }),
            new FileEditSpec("invalid_file.txt", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("nonexistent", "replacement")
            })
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeFalse();
        (await File.ReadAllTextAsync(file1)).Should().Be("file 1 content");
    }

    [Fact]
    public async Task ReadContextFiles_ExceedsFileCountLimit_ThrowsInvalidOperationException()
    {
        var filePaths = Enumerable.Range(1, 25).Select(i => $"file{i}.txt").ToList();
        var limits = new ContextLimits(MaxFileCount: 20);

        var act = () => _applier.ReadContextFilesAsync(_worktreeDir, _branchName, filePaths, limits);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeds maximum context limit*");
    }

    [Fact]
    public async Task ReadContextFiles_OneMissingAndOneValidContextFile_LoadsValidFileSuccessfully()
    {
        var validFile = Path.Combine(_worktreeDir, "valid.txt");
        await File.WriteAllTextAsync(validFile, "valid content");

        var result = await _applier.ReadContextFilesAsync(
            _worktreeDir,
            _branchName,
            new[] { "nonexistent_file.txt", "valid.txt" });

        result.Should().HaveCount(1);
        result.Should().ContainKey("valid.txt");
        result["valid.txt"].Should().Be("valid content");
    }

    [Fact]
    public async Task ReadContextFiles_AllContextFilesMissing_ReturnsEmptyDictionaryWithoutThrowing()
    {
        var result = await _applier.ReadContextFilesAsync(
            _worktreeDir,
            _branchName,
            new[] { "missing1.txt", "missing2.txt" });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadContextFiles_UnsafeMissingPath_ThrowsInvalidOperationException()
    {
        var act = () => _applier.ReadContextFilesAsync(
            _worktreeDir,
            _branchName,
            new[] { "../missing_outside.cs" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Path safety violation*");
    }

    [Fact]
    public async Task ReadContextFiles_GitOrSensitiveMissingPath_ThrowsInvalidOperationException()
    {
        var actGit = () => _applier.ReadContextFilesAsync(
            _worktreeDir,
            _branchName,
            new[] { ".git/config" });

        await actGit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Modification of .git directory or files is rejected*");

        var actEnv = () => _applier.ReadContextFilesAsync(
            _worktreeDir,
            _branchName,
            new[] { ".env" });

        await actEnv.Should().ThrowAsync<InvalidOperationException>().WithMessage("*sensitive configuration/credential file*");
    }

    [Fact]
    public async Task ApplyEdits_BomLessUtf8File_Modify_RemainsBomLess()
    {
        var filePath = Path.Combine(_worktreeDir, "bomless.cs");
        var utf8WithoutBom = new UTF8Encoding(false);
        await File.WriteAllTextAsync(filePath, "using DevPilot.Application;\npublic class App {}", utf8WithoutBom);

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("bomless.cs", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("using DevPilot.Application;", "using DevPilot.Application.Services;")
            })
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeTrue();
        var bytes = await File.ReadAllBytesAsync(filePath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        hasBom.Should().BeFalse();
        utf8WithoutBom.GetString(bytes).Should().StartWith("using DevPilot.Application.Services;");
    }

    [Fact]
    public async Task ApplyEdits_Utf8WithBomFile_Modify_PreservesBom()
    {
        var filePath = Path.Combine(_worktreeDir, "withbom.cs");
        var utf8WithBom = new UTF8Encoding(true);
        await File.WriteAllTextAsync(filePath, "using DevPilot.Application;\npublic class App {}", utf8WithBom);

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("withbom.cs", Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("using DevPilot.Application;", "using DevPilot.Application.Services;")
            })
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeTrue();
        var bytes = await File.ReadAllBytesAsync(filePath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        hasBom.Should().BeTrue();
        var contentWithoutBom = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        contentWithoutBom.Should().StartWith("using DevPilot.Application.Services;");
    }

    [Fact]
    public async Task ApplyEdits_CreateAction_CreatesBomLessUtf8File()
    {
        var relativePath = "created.cs";
        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec(relativePath, Action: FileEditAction.Create, NewContent: "public class Created {}")
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeTrue();
        var fullPath = Path.Combine(_worktreeDir, relativePath);
        var bytes = await File.ReadAllBytesAsync(fullPath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        hasBom.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyEdits_ModifyBomLessFile_GitDiffShowsOnlySubstantiveChange()
    {
        var relativePath = "substantive.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);
        var utf8WithoutBom = new UTF8Encoding(false);
        await File.WriteAllTextAsync(fullPath, "using DevPilot.Application;\npublic class App {}", utf8WithoutBom);

        RunGit(_worktreeDir, "add", relativePath);
        RunGit(_worktreeDir, "commit", "-m", "Add substantive.cs");

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec(relativePath, Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("using DevPilot.Application;", "using DevPilot.Application.Services;")
            })
        });

        var result = await _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        result.Success.Should().BeTrue();

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _worktreeDir
        };
        psi.ArgumentList.Add("diff");
        using var p = Process.Start(psi)!;
        var diff = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();

        diff.Should().NotContain("\uFEFF");
        diff.Should().Contain("-using DevPilot.Application;");
        diff.Should().Contain("+using DevPilot.Application.Services;");
    }

    [Fact]
    public async Task ReadContextFiles_Utf8FileWithBom_ReturnsContentWithoutLeadingBomCharacter()
    {
        var relativePath = "context_bom.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);
        var utf8WithBom = new UTF8Encoding(true);
        await File.WriteAllTextAsync(fullPath, "using DevPilot.Application;\npublic class ContextApp {}", utf8WithBom);

        var result = await _applier.ReadContextFilesAsync(
            _worktreeDir,
            _branchName,
            new[] { relativePath });

        result.Should().ContainKey(relativePath);
        var content = result[relativePath];
        content.StartsWith("\uFEFF", StringComparison.Ordinal).Should().BeFalse();
        content.Should().StartWith("using DevPilot.Application;");
    }

    [Fact]
    public async Task ReadContextFiles_InvalidUtf8Content_ThrowsDecoderFallbackException()
    {
        var relativePath = "invalid_utf8.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);
        await File.WriteAllBytesAsync(fullPath, new byte[] { 0xC0, 0xAF, 0x80 });

        var act = () => _applier.ReadContextFilesAsync(
            _worktreeDir,
            _branchName,
            new[] { relativePath });

        await act.Should().ThrowAsync<DecoderFallbackException>();
    }

    [Fact]
    public async Task ApplyEdits_InvalidUtf8ModifyTarget_ThrowsDecoderFallbackException()
    {
        var relativePath = "invalid_utf8_modify.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);
        await File.WriteAllBytesAsync(fullPath, new byte[] { 0xC0, 0xAF });

        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec(relativePath, Action: FileEditAction.Modify, SearchReplaceEdits: new[]
            {
                new SearchReplaceEdit("search", "replace")
            })
        });

        var act = () => _applier.ApplyEditsAsync(_worktreeDir, _branchName, plan);

        await act.Should().ThrowAsync<DecoderFallbackException>();
    }

    [Fact]
    public async Task ReadContextFiles_ValidRepositoryRelativePath_LoadsSuccessfully()
    {
        var relativePath = "src/SubFolder/Valid.cs";
        var fullPath = Path.Combine(_worktreeDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "public class Valid {}");

        var result = await _applier.ReadContextFilesAsync(_worktreeDir, _branchName, new[] { relativePath });

        result.Should().ContainKey(relativePath);
        result[relativePath].Should().Be("public class Valid {}");
    }

    [Fact]
    public async Task ReadContextFiles_MissingOneOfMultipleFiles_SkipsMissingAndLoadsValid()
    {
        var validPath = "src/ValidFile.cs";
        var missingPath = "src/MissingFile.cs";

        var fullValidPath = Path.Combine(_worktreeDir, validPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullValidPath)!);
        await File.WriteAllTextAsync(fullValidPath, "public class ValidFile {}");

        var result = await _applier.ReadContextFilesAsync(_worktreeDir, _branchName, new[] { missingPath, validPath });

        result.Should().HaveCount(1);
        result.Should().ContainKey(validPath);
        result.Should().NotContainKey(missingPath);
    }

    [Fact]
    public async Task ReadContextFiles_AllFilesMissing_ReturnsEmptyDictionaryWithoutThrowing()
    {
        var missing1 = "Missing1.cs";
        var missing2 = "Missing2.cs";

        var result = await _applier.ReadContextFilesAsync(_worktreeDir, _branchName, new[] { missing1, missing2 });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadContextFiles_WindowsPathSeparatorsAndLeadingSlashes_ResolvesAndLoadsSuccessfully()
    {
        var relativePath = @"\src\SubFolder\WinFile.cs";
        var fullPath = Path.Combine(_worktreeDir, "src", "SubFolder", "WinFile.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "public class WinFile {}");

        var result = await _applier.ReadContextFilesAsync(_worktreeDir, _branchName, new[] { relativePath });

        result.Should().HaveCount(1);
        result.Values.First().Should().Be("public class WinFile {}");
    }

    [Fact]
    public void ValidateAndResolvePath_AbsoluteInWorkspacePath_ThrowsInvalidOperationException()
    {
        var relative = "src/Inside.cs";
        var absoluteInWorkspace = Path.Combine(_worktreeDir, relative);

        var act = () => WorktreeEditApplier.ValidateAndResolvePath(_worktreeDir, absoluteInWorkspace);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Absolute paths are rejected*");
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
