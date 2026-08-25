using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class ModifyNoChangeContractTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalRepoDir;
    private readonly string _worktreeDir;
    private readonly string _branchName;
    private readonly FakeAiProvider _fakeAiProvider;
    private readonly WorktreeEditApplier _editApplier;
    private readonly RecordingActivityRecorder _recorder;
    private readonly DeveloperAgent _agent;

    public ModifyNoChangeContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotNoChange_" + Guid.NewGuid().ToString("N"));
        _originalRepoDir = Path.Combine(_tempDir, "original_repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        _branchName = "devpilot/nochange-branch";

        Directory.CreateDirectory(_originalRepoDir);
        Directory.CreateDirectory(_worktreeDir);
        InitGitRepo(_originalRepoDir);
        File.WriteAllText(Path.Combine(_originalRepoDir, "README.md"), "# Repo");
        RunGit(_originalRepoDir, "add", ".");
        RunGit(_originalRepoDir, "commit", "-m", "Initial commit");
        RunGit(_originalRepoDir, "worktree", "add", "-b", _branchName, _worktreeDir, "HEAD");

        _fakeAiProvider = new FakeAiProvider();
        _editApplier = new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance);
        _recorder = new RecordingActivityRecorder();
        _agent = new DeveloperAgent(
            _fakeAiProvider,
            _editApplier,
            NullLogger<DeveloperAgent>.Instance,
            activityRecorder: _recorder);
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
            // Ignore temp cleanup.
        }
    }

    [Fact]
    public void ExplicitModifyNoChange_IsValid()
    {
        var entry = new ManifestFileEntry("src/Notes.cs", FileEditAction.Modify);
        var spec = DeveloperAgent.ParseSingleFileEditSpec(
            """{"filePath":"src/Notes.cs","action":"Modify","noChange":true,"reason":"No matching change is required in the current target."}""",
            entry);

        spec.NoChange.Should().BeTrue();
        spec.NoChangeReason.Should().Contain("No matching change");
        spec.SearchReplaceEdits.Should().BeNull();
        var act = () => DeveloperAgent.ValidateSingleFileEditSpec(spec, entry, "class Notes {}");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task NoChange_LeavesTargetByteIdentical()
    {
        var relative = "src/Notes.cs";
        var fullPath = Path.Combine(_worktreeDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var original = "public class Notes { /* already clean */ }\n";
        await File.WriteAllTextAsync(fullPath, original);

        var result = await _editApplier.ApplyEditsAsync(
            _worktreeDir,
            _branchName,
            new StructuredEditPlan(new[]
            {
                new FileEditSpec(relative, FileEditAction.Modify, NoChange: true, NoChangeReason: "Already clean")
            }),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ModifiedFiles.Should().BeEmpty();
        result.ResolvedNoChangeFiles.Should().Equal(relative);
        (await File.ReadAllTextAsync(fullPath)).Should().Be(original);
    }

    [Fact]
    public async Task ExplicitNoChange_DoesNotTriggerApplicabilityRepair()
    {
        WriteWorktreeFile("src/Notes.cs", "public class Notes { /* already satisfies cleanup */ }");
        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/Notes.cs","action":"Modify","noChange":true,"reason":"No matching change is required in the current target."}""");

        var result = await _agent.GenerateAndApplyEditsAsync(BuildRequest(
            new[] { "src/Notes.cs" },
            new[] { new ImpactedFileDetail("src/Notes.cs", "Modify", "Remove leftover comments") }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ModifiedFiles.Should().BeEmpty();
        result.ResolvedNoChangeFiles.Should().Contain("src/Notes.cs");
        _fakeAiProvider.SendAsyncCallCount.Should().Be(1);
        _recorder.Metadata.Where(metadata => metadata != null).Should().NotContain(metadata =>
            metadata!.EventKind == "ApplicabilityRepair" || metadata.EventKind == "MicroApplicabilityRepair");
        _recorder.Metadata.Where(metadata => metadata != null).Should().Contain(metadata =>
            metadata!.EventKind == "ResolvedNoChange" &&
            metadata.TargetFile == "src/Notes.cs" &&
            !string.IsNullOrWhiteSpace(metadata.NoChangeReason));
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "Notes.cs"))
            .Should().Be("public class Notes { /* already satisfies cleanup */ }");
    }

    [Fact]
    public void EmptySearchReplaceEdits_WithoutExplicitNoChange_RemainsInvalid()
    {
        var entry = new ManifestFileEntry("src/Notes.cs", FileEditAction.Modify);
        var act = () => DeveloperAgent.ValidateSingleFileEditSpec(
            new FileEditSpec("src/Notes.cs", FileEditAction.Modify, SearchReplaceEdits: Array.Empty<SearchReplaceEdit>()),
            entry,
            "class Notes {}");

        act.Should().Throw<FormatException>().WithMessage("*requires at least one edit*");
        DeveloperAgent.TryMaterializeCompletedEdit(
            """{"filePath":"src/Notes.cs","action":"Modify","searchReplaceEdits":[]}""",
            entry,
            "class Notes {}",
            false,
            out var spec,
            out _).Should().BeFalse();
        spec.Should().BeNull();
    }

    [Fact]
    public void MalformedJson_CannotBecomeNoChange()
    {
        var entry = new ManifestFileEntry("src/Notes.cs", FileEditAction.Modify);
        var act = () => DeveloperAgent.ParseSingleFileEditSpec(
            """{"filePath":"src/Notes.cs","action":"Modify","noChange":""",
            entry);

        act.Should().Throw<Exception>();
        DeveloperAgent.TryMaterializeCompletedEdit(
            """{"filePath":"src/Notes.cs","action":"Modify","noChange":""",
            entry,
            "class Notes {}",
            false,
            out var spec,
            out _).Should().BeFalse();
        spec.Should().BeNull();
    }

    [Fact]
    public async Task CreateNoChange_IsRejected()
    {
        var entry = new ManifestFileEntry("src/NewFile.cs", FileEditAction.Create);
        var actParse = () => DeveloperAgent.ValidateSingleFileEditSpec(
            DeveloperAgent.ParseSingleFileEditSpec(
                """{"filePath":"src/NewFile.cs","action":"Create","noChange":true,"reason":"skip"}""",
                entry),
            entry);

        actParse.Should().Throw<FormatException>().WithMessage("*cannot use NoChange*");

        var applyResult = await _editApplier.ApplyEditsAsync(
            _worktreeDir,
            _branchName,
            new StructuredEditPlan(new[]
            {
                new FileEditSpec("src/NewFile.cs", FileEditAction.Create, NewContent: "class X {}", NoChange: true)
            }),
            CancellationToken.None);

        applyResult.Success.Should().BeFalse();
        applyResult.ErrorMessage.Should().Contain("cannot use NoChange");
    }

    [Fact]
    public async Task MixedChangedAndNoChange_AppliesOnlyRealEdits()
    {
        WriteWorktreeFile("src/Keep.cs", "public class Keep { public int V = 1; }");
        WriteWorktreeFile("src/Change.cs", "public class Change { public int V = 1; }");
        _fakeAiProvider.CustomHandler = (request, _) =>
        {
            var content = request.UserPrompt.Contains("src/Keep.cs", StringComparison.Ordinal)
                ? """{"filePath":"src/Keep.cs","action":"Modify","noChange":true,"reason":"No matching change is required in the current target."}"""
                : """{"filePath":"src/Change.cs","action":"Modify","searchReplaceEdits":[{"search":"public int V = 1;","replace":"public int V = 2;"}]}""";
            return Task.FromResult(new AiResponse
            {
                Provider = "StubFakeProvider",
                Content = content,
                IsSuccess = true,
                StatusCode = 200
            });
        };

        var result = await _agent.GenerateAndApplyEditsAsync(BuildRequest(
            new[] { "src/Keep.cs", "src/Change.cs" },
            new[]
            {
                new ImpactedFileDetail("src/Keep.cs", "Modify", "Cleanup comments if needed"),
                new ImpactedFileDetail("src/Change.cs", "Modify", "Update value")
            }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ModifiedFiles.Should().Equal("src/Change.cs");
        result.ResolvedNoChangeFiles.Should().Equal("src/Keep.cs");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "Keep.cs")).Should().Be("public class Keep { public int V = 1; }");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "Change.cs")).Should().Be("public class Change { public int V = 2; }");
        _recorder.Metadata.Where(metadata => metadata != null)
            .Should().NotContain(metadata => metadata!.EventKind == "ApplicabilityRepair");
    }

    [Fact]
    public async Task AllNoChange_DoesNotFailAsDeveloperAgentZeroModifiedError()
    {
        WriteWorktreeFile("src/A.cs", "public class A { }");
        WriteWorktreeFile("src/B.cs", "public class B { }");
        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/A.cs","action":"Modify","noChange":true,"reason":"Already clean"}""");
        _fakeAiProvider.ResponsesToReturn.Enqueue(
            """{"filePath":"src/B.cs","action":"Modify","noChange":true,"reason":"Already clean"}""");

        var result = await _agent.GenerateAndApplyEditsAsync(BuildRequest(
            new[] { "src/A.cs", "src/B.cs" },
            new[]
            {
                new ImpactedFileDetail("src/A.cs", "Modify", "Cleanup"),
                new ImpactedFileDetail("src/B.cs", "Modify", "Cleanup")
            }));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ErrorMessage.Should().BeNull();
        result.ModifiedFiles.Should().BeEmpty();
        result.ResolvedNoChangeFiles.Should().BeEquivalentTo(new[] { "src/A.cs", "src/B.cs" });
        result.HasResolvedNoChange.Should().BeTrue();
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "A.cs")).Should().Be("public class A { }");
        File.ReadAllText(Path.Combine(_worktreeDir, "src", "B.cs")).Should().Be("public class B { }");
    }

    [Fact]
    public void ModifyPrompts_DescribeExplicitNoChangeContract()
    {
        var entry = new ManifestFileEntry("src/Notes.cs", FileEditAction.Modify);
        var systemPrompt = DeveloperAgent.BuildSingleFileSystemPrompt(entry);
        systemPrompt.Should().Contain("noChange");
        systemPrompt.Should().Contain("If the target already satisfies the requested file-specific change");
        systemPrompt.Should().Contain("Do not use NoChange simply because the edit is difficult");
    }

    private DeveloperAgentRequest BuildRequest(
        IReadOnlyList<string> paths,
        IReadOnlyList<ImpactedFileDetail> details) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Cleanup comments",
            "Remove leftover comments if they still exist",
            "Files that already satisfy the cleanup require no invented patch",
            "Summary",
            "Cleanup planned files",
            paths,
            _worktreeDir,
            _branchName,
            details);

    private void WriteWorktreeFile(string relative, string content)
    {
        var fullPath = Path.Combine(_worktreeDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.email", "devpilot@test.local");
        RunGit(path, "config", "user.name", "DevPilot Tests");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    private sealed class RecordingActivityRecorder : IExecutionActivityRecorder
    {
        public List<ExecutionActivityMetadata?> Metadata { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Metadata.Add(metadata);
            return Task.CompletedTask;
        }
    }
}
