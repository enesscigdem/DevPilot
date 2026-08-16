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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

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
        var fingerprintCalculator = new StubFingerprintCalculator();
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalculator, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(Guid.NewGuid()));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.NotFound);
        result.ErrorMessage.Should().Contain("Execution not found");
    }

    [Fact]
    public async Task GetExecutionReview_CommittedExecution_ReadsDiffFromBaseCommitToCommitSha_WhenWorktreeIsClean()
    {
        // Arrange
        var fileRelPath = "src/Services/PaymentService.cs";
        var fullPath = Path.Combine(_workspaceDir, fileRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "public class PaymentService {}");

        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Initial base commit");
        var baseCommitSha = RunGitOutput(_workspaceDir, "rev-parse", "HEAD").Trim();

        // Worktree modification
        File.WriteAllText(fullPath, "public class PaymentService { public void Process() {} }");
        var executionId = Guid.NewGuid();

        // Calculate expected fingerprint
        var fingerprintCalc = new GitExecutionChangeFingerprintCalculator(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);
        var fpResult = await fingerprintCalc.ComputeFingerprintAsync(_workspaceDir);
        fpResult.Success.Should().BeTrue();

        // Create committed commit with DevPilot-Execution trailer
        RunGit(_workspaceDir, "add", ".");
        var treeSha = RunGitOutput(_workspaceDir, "write-tree").Trim();
        var commitMsg = $"Execute task changes\n\nDevPilot-Execution: {executionId}\n";
        var commitSha = RunGitOutput(_workspaceDir, "commit-tree", treeSha, "-p", baseCommitSha, "-m", commitMsg).Trim();
        RunGit(_workspaceDir, "update-ref", "refs/heads/main", commitSha);

        // Worktree is now clean post-commit
        RunGit(_workspaceDir, "reset", "--hard", commitSha);

        var execution = new TaskExecution
        {
            Id = executionId,
            DevelopmentTaskId = Guid.NewGuid(),
            DevelopmentTask = new DevelopmentTask { Title = "Committed Task" },
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main",
            CommitStatus = ExecutionCommitStatus.Committed,
            BaseCommitSha = baseCommitSha,
            CommitSha = commitSha,
            ApprovedChangeFingerprint = fpResult.Fingerprint,
            ReviewStatus = ExecutionReviewStatus.Approved
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalc, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Success);
        result.Review.Should().NotBeNull();
        result.Review!.ChangedFileCount.Should().Be(1);
        result.Review.ChangedFiles[0].Path.Should().Be("src/Services/PaymentService.cs");
        result.Review.Diff.Should().Contain("Process()");
        result.Review.ApprovedSnapshotMatchesCurrent.Should().BeTrue();
        result.Review.CommitEligible.Should().BeFalse();
        result.Review.CommitStatus.Should().Be("Committed");
        result.Review.CommitSha.Should().Be(commitSha);
    }

    [Fact]
    public async Task GetExecutionReview_CommittedExecution_ParentMismatch_ReturnsConflict()
    {
        // Arrange
        var fileRelPath = "File1.txt";
        File.WriteAllText(Path.Combine(_workspaceDir, fileRelPath), "Base");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Base 1");
        var base1 = RunGitOutput(_workspaceDir, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(_workspaceDir, fileRelPath), "Base 2");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Base 2");
        var base2 = RunGitOutput(_workspaceDir, "rev-parse", "HEAD").Trim();

        var executionId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_workspaceDir, fileRelPath), "Mod");
        RunGit(_workspaceDir, "add", ".");
        var treeSha = RunGitOutput(_workspaceDir, "write-tree").Trim();
        var commitMsg = $"Execute\n\nDevPilot-Execution: {executionId}\n";
        // Commit parent is base2, but persisted BaseCommitSha is base1
        var commitSha = RunGitOutput(_workspaceDir, "commit-tree", treeSha, "-p", base2, "-m", commitMsg).Trim();

        var execution = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main",
            CommitStatus = ExecutionCommitStatus.Committed,
            BaseCommitSha = base1, // Mismatch with actual parent base2!
            CommitSha = commitSha,
            ApprovedChangeFingerprint = "sha256:dummy",
            ReviewStatus = ExecutionReviewStatus.Approved
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var fingerprintCalc = new GitExecutionChangeFingerprintCalculator(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalc, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("git integrity check failed");
        result.ErrorMessage.Should().Contain("parent commit does not match");
    }

    [Fact]
    public async Task GetExecutionReview_CommittedExecution_ObjectTypeNotCommit_ReturnsConflict()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_workspaceDir, "a.txt"), "hello");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Initial");
        var baseSha = RunGitOutput(_workspaceDir, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(_workspaceDir, "a.txt"), "world");
        RunGit(_workspaceDir, "add", ".");
        var treeSha = RunGitOutput(_workspaceDir, "write-tree").Trim(); // tree object, NOT commit!

        var executionId = Guid.NewGuid();
        var execution = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main",
            CommitStatus = ExecutionCommitStatus.Committed,
            BaseCommitSha = baseSha,
            CommitSha = treeSha, // Passed a tree object instead of commit!
            ApprovedChangeFingerprint = "sha256:dummy",
            ReviewStatus = ExecutionReviewStatus.Approved
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var fingerprintCalc = new GitExecutionChangeFingerprintCalculator(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalc, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("git integrity check failed");
        result.ErrorMessage.Should().Contain("not a valid Git commit object");
    }

    [Fact]
    public async Task GetExecutionReview_CommittedExecution_MissingOrWrongTrailer_ReturnsConflict()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_workspaceDir, "b.txt"), "hello");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Initial");
        var baseSha = RunGitOutput(_workspaceDir, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(_workspaceDir, "b.txt"), "world");
        RunGit(_workspaceDir, "add", ".");
        var treeSha = RunGitOutput(_workspaceDir, "write-tree").Trim();

        var executionId = Guid.NewGuid();
        var wrongId = Guid.NewGuid();
        var commitMsg = $"Execute\n\nDevPilot-Execution: {wrongId}\n"; // Wrong execution ID in trailer!
        var commitSha = RunGitOutput(_workspaceDir, "commit-tree", treeSha, "-p", baseSha, "-m", commitMsg).Trim();

        var execution = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main",
            CommitStatus = ExecutionCommitStatus.Committed,
            BaseCommitSha = baseSha,
            CommitSha = commitSha,
            ApprovedChangeFingerprint = "sha256:dummy",
            ReviewStatus = ExecutionReviewStatus.Approved
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var fingerprintCalc = new GitExecutionChangeFingerprintCalculator(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalc, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("git integrity check failed");
        result.ErrorMessage.Should().Contain("trailer");
    }

    [Fact]
    public async Task GetExecutionReview_CommittedExecution_FingerprintMismatch_ReturnsConflict()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_workspaceDir, "c.txt"), "hello");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Initial");
        var baseSha = RunGitOutput(_workspaceDir, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(_workspaceDir, "c.txt"), "world");
        RunGit(_workspaceDir, "add", ".");
        var treeSha = RunGitOutput(_workspaceDir, "write-tree").Trim();

        var executionId = Guid.NewGuid();
        var commitMsg = $"Execute\n\nDevPilot-Execution: {executionId}\n";
        var commitSha = RunGitOutput(_workspaceDir, "commit-tree", treeSha, "-p", baseSha, "-m", commitMsg).Trim();

        var execution = new TaskExecution
        {
            Id = executionId,
            Status = TaskExecutionStatus.Completed,
            WorkspacePath = _workspaceDir,
            BranchName = "main",
            CommitStatus = ExecutionCommitStatus.Committed,
            BaseCommitSha = baseSha,
            CommitSha = commitSha,
            ApprovedChangeFingerprint = "sha256:wrong_fingerprint_hash_value_1234567890",
            ReviewStatus = ExecutionReviewStatus.Approved
        };

        var repo = new FakeExecutionRepository(execution);
        var workspaceManager = new FakeWorkspaceManager(isValid: true);
        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);
        var fingerprintCalc = new GitExecutionChangeFingerprintCalculator(NullLogger<GitExecutionChangeFingerprintCalculator>.Instance);
        var handler = new GetExecutionReviewQueryHandler(repo, workspaceManager, diffReader, fingerprintCalc, NullLogger<GetExecutionReviewQueryHandler>.Instance);

        // Act
        var result = await handler.HandleAsync(new GetExecutionReviewQuery(executionId));

        // Assert
        result.Status.Should().Be(ExecutionReviewResultStatus.Conflict);
        result.ErrorMessage.Should().Contain("git integrity check failed");
        result.ErrorMessage.Should().Contain("fingerprint");
    }

    [Fact]
    public async Task ReadCommittedDiff_SensitiveRenamedFile_RedactsContent()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_workspaceDir, ".env"), "SECRET_KEY=12345");
        RunGit(_workspaceDir, "add", ".");
        RunGit(_workspaceDir, "commit", "-m", "Add env");
        var baseSha = RunGitOutput(_workspaceDir, "rev-parse", "HEAD").Trim();

        // Rename .env to harmless.txt
        var envPath = Path.Combine(_workspaceDir, ".env");
        var harmlessPath = Path.Combine(_workspaceDir, "harmless.txt");
        File.Move(envPath, harmlessPath);

        RunGit(_workspaceDir, "add", "-A");
        var treeSha = RunGitOutput(_workspaceDir, "write-tree").Trim();
        var commitMsg = "Rename secret\n";
        var commitSha = RunGitOutput(_workspaceDir, "commit-tree", treeSha, "-p", baseSha, "-m", commitMsg).Trim();
        RunGit(_workspaceDir, "update-ref", "refs/heads/main", commitSha);
        RunGit(_workspaceDir, "reset", "--hard", commitSha);

        var diffReader = new GitExecutionDiffReader(NullLogger<GitExecutionDiffReader>.Instance);

        // Act
        var result = await diffReader.ReadCommittedDiffAsync(_workspaceDir, baseSha, commitSha);

        // Assert
        result.Success.Should().BeTrue();
        result.DiffText.Should().NotContain("SECRET_KEY=12345");
        result.DiffText.Should().Contain("[Redacted sensitive file content:");
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

    private static string RunGitOutput(string workingDirectory, params string[] args)
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
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
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
        public Task<bool> TrySetReviewDecisionWithFingerprintAsync(Guid executionId, DevPilot.Domain.Enums.ExecutionReviewStatus expectedStatus, DevPilot.Domain.Enums.ExecutionReviewStatus newStatus, DateTime decidedAt, string fingerprint, string? rejectionReason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryClaimNewCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, string baseCommitSha, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStaleCommitLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetCommitCompletedAsync(Guid executionId, Guid attemptId, string commitSha, DateTime committedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetCommitFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryClaimNewPushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryReclaimStalePushLeaseAsync(Guid executionId, Guid attemptId, DateTime claimedAt, TimeSpan leaseTimeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetPushCompletedAsync(Guid executionId, Guid attemptId, string remoteBranchName, string remoteCommitSha, DateTime pushedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPushFailedAsync(Guid executionId, Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    private sealed class StubFingerprintCalculator : IExecutionChangeFingerprintCalculator
    {
        public string SampleFingerprint { get; set; } = "sha256:1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

        public Task<ExecutionFingerprintResult> ComputeFingerprintAsync(string workspacePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExecutionFingerprintResult(
                Success: true,
                Fingerprint: SampleFingerprint,
                BaseHeadSha: "base123",
                HasSensitiveFiles: false,
                ChangedFileCount: 1));
        }

        public Task<ExecutionFingerprintResult> ComputeStagedTreeFingerprintAsync(string workspacePath, string treeSha, string baseHeadSha, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExecutionFingerprintResult(
                Success: true,
                Fingerprint: SampleFingerprint,
                BaseHeadSha: baseHeadSha,
                HasSensitiveFiles: false,
                ChangedFileCount: 1));
        }
    }
}
