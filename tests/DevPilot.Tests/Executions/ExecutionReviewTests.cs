using System.Diagnostics;
using System.Text;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Queries.GetExecutionReview;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class ExecutionReviewTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly string _workspaceDir;

    public ExecutionReviewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilot_ReviewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _repoDir = Path.Combine(_tempDir, "source_repo");
        Directory.CreateDirectory(_repoDir);
        _workspaceDir = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceDir);

        InitGitRepo(_repoDir);
        InitGitRepo(_workspaceDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task GetExecutionReview_CompletedExecution_ReturnsModifiedFileAndDiff()
    {
        // Arrange
        var fileRelPath = "src/Calculator.cs";
        var fullPath = Path.Combine(_workspaceDir, fileRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "public class Calculator { public int Add(int a, int b) => a + b; }");

        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Initial commit");

        // Modify file
        File.WriteAllText(fullPath, "public class Calculator { public int Add(int a, int b) => a + b;\npublic int Sub(int a, int b) => a - b; }");

        var executionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = taskId,
            DevelopmentTask = new DevelopmentTask { Id = taskId, Title = "Add Subtraction" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review.Should().NotBeNull();
        result.Review!.ExecutionStatus.Should().Be("Completed");
        result.Review.Build.Status.Should().Be("Passed");
        result.Review.Test.Status.Should().Be("Passed");
        result.Review.ChangedFileCount.Should().Be(1);
        result.Review.ChangedFiles[0].Path.Should().Be("src/Calculator.cs");
        result.Review.ChangedFiles[0].ChangeType.Should().Be("Modified");
        result.Review.Diff.Should().Contain("Sub");
        result.Review.DiffTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task GetExecutionReview_AddedUntrackedFile_RepresentedCorrectly()
    {
        // Arrange
        var initialFile = Path.Combine(_workspaceDir, "README.md");
        File.WriteAllText(initialFile, "# Project");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Initial commit");

        // Add new untracked file
        var newFile = Path.Combine(_workspaceDir, "src/Services/NewService.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.WriteAllText(newFile, "public class NewService {}");

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Add Service" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review!.ChangedFileCount.Should().Be(1);
        result.Review.ChangedFiles[0].Path.Should().Be("src/Services/NewService.cs");
        result.Review.ChangedFiles[0].ChangeType.Should().Be("Added");
        result.Review.Diff.Should().Contain("+++ b/src/Services/NewService.cs");
        result.Review.Diff.Should().Contain("+public class NewService {}");
    }

    [Fact]
    public async Task GetExecutionReview_FileWithSpacesAndUnicode_ParsedCleanly()
    {
        // Arrange
        var unicodeFile = Path.Combine(_workspaceDir, "docs/Gözlem Notları 2026.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(unicodeFile)!);
        File.WriteAllText(unicodeFile, "Türkçe içerik test 123");

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Unicode Test" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review!.ChangedFiles[0].Path.Should().Be("docs/Gözlem Notları 2026.txt");
        result.Review.Diff.Should().Contain("Türkçe içerik test 123");
    }

    [Fact]
    public async Task GetExecutionReview_FilePathStartingWithDash_HandledSafelyWithOptionDisambiguation()
    {
        // Arrange
        var dashFile = Path.Combine(_workspaceDir, "--config.json");
        File.WriteAllText(dashFile, "{ \"setting\": true }");

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Dash File Test" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review!.ChangedFiles[0].Path.Should().Be("--config.json");
        result.Review.Diff.Should().Contain("{ \"setting\": true }");
    }

    [Fact]
    public async Task GetExecutionReview_RenamedPathWithSpaces_ParsesZOutputCorrectly()
    {
        // Arrange
        var oldFile = Path.Combine(_workspaceDir, "old name.txt");
        File.WriteAllText(oldFile, "Renamed content");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Add file to rename");

        var newFile = Path.Combine(_workspaceDir, "yeni ad.txt");
        File.Move(oldFile, newFile);
        RunGit(_workspaceDir, "add", "-A");

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Rename Test" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review!.ChangedFiles.Should().HaveCount(1);
        result.Review.ChangedFiles[0].Path.Should().Be("yeni ad.txt");
        result.Review.ChangedFiles[0].ChangeType.Should().Be("Renamed");
    }

    [Fact]
    public async Task GetExecutionReview_CancelledExecution_ReturnsUnknownStageStatus()
    {
        // Arrange
        var file = Path.Combine(_workspaceDir, "App.cs");
        File.WriteAllText(file, "Console.WriteLine(\"Hello\");");

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Cancelled Task" },
            Status = TaskExecutionStatus.Cancelled,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review!.ExecutionStatus.Should().Be("Cancelled");
        result.Review.Build.Status.Should().Be("Unknown");
        result.Review.Test.Status.Should().Be("Unknown");
    }

    [Fact]
    public async Task GetExecutionReview_SensitiveFiles_NeverCapturedInReturnedDiff()
    {
        // Arrange
        var envFile = Path.Combine(_workspaceDir, ".env");
        File.WriteAllText(envFile, "SECRET_KEY=super_secret_key_12345");

        var secretsFile = Path.Combine(_workspaceDir, "secrets.json");
        File.WriteAllText(secretsFile, "{ \"apiKey\": \"123456\" }");

        var pemFile = Path.Combine(_workspaceDir, "cert.pem");
        File.WriteAllText(pemFile, "-----BEGIN PRIVATE KEY-----\nsecret_data\n-----END PRIVATE KEY-----");

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Sensitive Test" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review!.ChangedFiles.Should().HaveCount(3);
        result.Review.Diff.Should().NotContain("super_secret_key_12345");
        result.Review.Diff.Should().NotContain("BEGIN PRIVATE KEY");
        result.Review.Diff.Should().Contain("[Redacted sensitive file content: .env]");
        result.Review.Diff.Should().Contain("[Redacted sensitive file content: secrets.json]");
        result.Review.Diff.Should().Contain("[Redacted sensitive file content: cert.pem]");
    }

    [Fact]
    public async Task GetExecutionReview_BinaryFiles_RepresentedAsMarkerWithoutBinaryContents()
    {
        // Arrange
        var binaryFile = Path.Combine(_workspaceDir, "icon.ico");
        File.WriteAllBytes(binaryFile, new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF, 0xFE });

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Binary Test" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review!.ChangedFiles.Should().HaveCount(1);
        result.Review.ChangedFiles[0].Path.Should().Be("icon.ico");
        result.Review.Diff.Should().Contain("[Binary file diff not shown: icon.ico]");
    }

    [Fact]
    public async Task GetExecutionReview_LargeDiffExceeding512KB_TruncatedSafely()
    {
        // Arrange
        var largeFile = Path.Combine(_workspaceDir, "LargeFile.txt");
        var sb = new StringBuilder();
        for (int i = 0; i < 20000; i++)
        {
            sb.AppendLine($"Line {i}: This is a long repeated content line to reach the 512 KB payload size limit.");
        }
        File.WriteAllText(largeFile, sb.ToString());

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Large Diff Test" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review!.DiffTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(result.Review.Diff).Should().BeLessThanOrEqualTo(512 * 1024);
    }

    [Fact]
    public async Task GetExecutionReview_CleanWorktree_ReturnsZeroChangedFiles()
    {
        // Arrange
        var file = Path.Combine(_workspaceDir, "README.md");
        File.WriteAllText(file, "Clean workspace");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Initial");

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Clean Workspace" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review!.ChangedFileCount.Should().Be(0);
        result.Review.ChangedFiles.Should().BeEmpty();
        result.Review.Diff.Should().BeEmpty();
    }

    [Fact]
    public async Task GetExecutionReview_PendingOrRunningStatus_ReturnsConflict409()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Running,
            WorkspacePath = _workspaceDir,
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("currently Running");
    }

    [Fact]
    public async Task GetExecutionReview_MissingWorkspace_ReturnsConflict409()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = Path.Combine(_tempDir, "non_existent_dir"),
            BranchName = "main"
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: false, errorMessage: "Workspace directory does not exist.");
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("workspace verification failed");
    }

    [Fact]
    public async Task GetExecutionReview_ExecutionNotFound_ReturnsNotFound404()
    {
        // Arrange
        var repo = new FakeExecutionRepository(null);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(Guid.NewGuid()));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.NotFound);
        result.ErrorMessage.Should().Contain("Execution not found");
    }

    private static void InitGitRepo(string path)
    {
        RunGit(path, "init");
        RunGit(path, "config", "user.name", "Review Test");
        RunGit(path, "config", "user.email", "review@test.local");
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

    private sealed class FakeExecutionRepository : IExecutionRepository
    {
        private readonly TaskExecution? _execution;

        public FakeExecutionRepository(TaskExecution? execution)
        {
            _execution = execution;
        }

        public Task<TaskExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_execution);

        public Task<bool> StartExecutionAtomicAsync(TaskExecution execution, DevelopmentTask task, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskExecution>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> HasActiveExecutionForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ClaimAsRunningAsync(Guid executionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task CompleteAsync(Guid executionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task FailAsync(Guid executionId, string errorMessage, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateWorkspaceDetailsAsync(Guid executionId, string workspacePath, string branchName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> TrySetReviewDecisionAsync(Guid executionId, DevPilot.Domain.Enums.ExecutionReviewStatus expectedStatus, DevPilot.Domain.Enums.ExecutionReviewStatus newStatus, DateTime decidedAt, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeWorkspaceManager : IExecutionWorkspaceManager
    {
        private readonly bool _isValid;
        private readonly string? _errorMessage;

        public FakeWorkspaceManager(bool isValid, string? errorMessage = null)
        {
            _isValid = isValid;
            _errorMessage = errorMessage;
        }

        public Task<WorkspaceVerificationResult> VerifyWorkspaceStateAsync(
            string workspacePath,
            string expectedBranchName,
            bool requireClean = true,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkspaceVerificationResult(
                IsValid: _isValid,
                WorkspaceExists: _isValid,
                BranchMatches: _isValid,
                IsClean: false,
                ErrorMessage: _errorMessage));
        }

        public Task<ExecutionWorkspaceResult> PrepareWorkspaceAsync(Guid executionId, Guid taskId, string sourceRepositoryLocalPath, string? sourceBranch = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
