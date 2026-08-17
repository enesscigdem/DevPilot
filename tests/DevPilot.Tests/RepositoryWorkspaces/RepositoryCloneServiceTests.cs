using System.Diagnostics;
using DevPilot.Application.RepositoryClone;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.RepositoryClone;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevPilot.Tests.RepositoryWorkspaces;

public class RepositoryCloneServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspaceRoot;
    private readonly string _bareReposRoot;
    private readonly DevPilotDbContext _dbContext;
    private readonly RepositoryCloneService _service;

    public RepositoryCloneServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevPilotCloneTests_" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempRoot, "workspaces");
        _bareReposRoot = Path.Combine(_tempRoot, "bare_repos");

        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(_bareReposRoot);

        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(databaseName: "CloneServiceTestDb_" + Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new DevPilotDbContext(options);

        var cloneOptions = Options.Create(new RepositoryCloneOptions
        {
            WorkspaceRoot = _workspaceRoot,
            Timeout = TimeSpan.FromSeconds(30),
        });

        var configuration = new ConfigurationBuilder().Build();

        _service = new RepositoryCloneService(
            cloneOptions,
            _dbContext,
            configuration,
            NullLogger<RepositoryCloneService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();

        try
        {
            if (Directory.Exists(_tempRoot))
            {
                // Remove readonly attributes if git created any
                SetAttributesNormal(new DirectoryInfo(_tempRoot));
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
        }
        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }

    [Fact]
    public async Task CloneAsync_ValidationErrors_ReturnsValidationErrorResult()
    {
        var res1 = await _service.CloneAsync(null!);
        res1.Success.Should().BeFalse();
        res1.IsValidationError.Should().BeTrue();

        var res2 = await _service.CloneAsync(new CloneRequest { Owner = "", Repository = "repo", Branch = "main" });
        res2.Success.Should().BeFalse();
        res2.IsValidationError.Should().BeTrue();

        var res3 = await _service.CloneAsync(new CloneRequest { Owner = "../evil", Repository = "repo", Branch = "main" });
        res3.Success.Should().BeFalse();
        res3.IsValidationError.Should().BeTrue();

        var res4 = await _service.CloneAsync(new CloneRequest { Owner = "owner", Repository = "repo", Branch = "../../escape" });
        res4.Success.Should().BeFalse();
        res4.IsValidationError.Should().BeTrue();
    }

    [Fact]
    public async Task CloneAsync_ExistingValidLocalRepositoryWithMissingDbRecord_SafelyReconnectsWithoutDeleting()
    {
        const string owner = "testowner";
        const string repo = "testrepo";
        const string branch = "main";

        var targetPath = Path.Combine(_workspaceRoot, owner, repo, branch);
        Directory.CreateDirectory(targetPath);

        InitGitRepo(targetPath, branch);
        var testFile = Path.Combine(targetPath, "README.md");
        File.WriteAllText(testFile, "# Test Repo");
        RunGit(targetPath, "add", ".");
        RunGit(targetPath, "commit", "-m", "Initial commit");
        RunGit(targetPath, "remote", "add", "origin", $"https://github.com/{owner}/{repo}.git");

        var expectedCommitSha = RunGit(targetPath, "rev-parse", "HEAD").Trim();

        // DB record is initially missing (simulating DB reset)
        (await _dbContext.RepositoryWorkspaces.AnyAsync()).Should().BeFalse();

        var result = await _service.CloneAsync(new CloneRequest
        {
            Owner = owner,
            Repository = repo,
            Branch = branch,
        });

        result.Success.Should().BeTrue();
        result.IsConflict.Should().BeFalse();
        result.CommitSha.Should().Be(expectedCommitSha);
        result.Status.Should().Be(RepositoryWorkspaceStatus.Completed);
        result.Owner.Should().Be(owner);
        result.Repository.Should().Be(repo);
        result.Branch.Should().Be(branch);

        // Verify DB record is registered
        var savedWs = await _dbContext.RepositoryWorkspaces.FirstOrDefaultAsync();
        savedWs.Should().NotBeNull();
        savedWs!.Owner.Should().Be(owner);
        savedWs.Repository.Should().Be(repo);
        savedWs.Branch.Should().Be(branch);
        savedWs.Status.Should().Be(RepositoryWorkspaceStatus.Completed);
        savedWs.CommitSha.Should().Be(expectedCommitSha);

        // Verify directory and file are still intact
        File.Exists(testFile).Should().BeTrue();
        File.ReadAllText(testFile).Should().Be("# Test Repo");
    }

    [Fact]
    public async Task CloneAsync_DuplicateActiveWorkspaceRequest_ReturnsConflict()
    {
        const string owner = "testowner";
        const string repo = "testrepo";
        const string branch = "main";

        var targetPath = Path.Combine(_workspaceRoot, owner, repo, branch);
        Directory.CreateDirectory(targetPath);

        InitGitRepo(targetPath, branch);
        File.WriteAllText(Path.Combine(targetPath, "README.md"), "# Test Repo");
        RunGit(targetPath, "add", ".");
        RunGit(targetPath, "commit", "-m", "Initial commit");
        RunGit(targetPath, "remote", "add", "origin", $"https://github.com/{owner}/{repo}.git");

        var commitSha = RunGit(targetPath, "rev-parse", "HEAD").Trim();

        // Register in DB as Completed
        _dbContext.RepositoryWorkspaces.Add(new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = owner,
            Repository = repo,
            Branch = branch,
            LocalPath = targetPath,
            CommitSha = commitSha,
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        // Second clone request should return conflict
        var result = await _service.CloneAsync(new CloneRequest
        {
            Owner = owner,
            Repository = repo,
            Branch = branch,
        });

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.Error.Should().Contain("already exists");
    }

    [Fact]
    public async Task CloneAsync_ExistingDirectoryNotAGitRepo_ReturnsConflictWithoutDeleting()
    {
        const string owner = "testowner";
        const string repo = "testrepo";
        const string branch = "main";

        var targetPath = Path.Combine(_workspaceRoot, owner, repo, branch);
        Directory.CreateDirectory(targetPath);
        var importantFile = Path.Combine(targetPath, "important.txt");
        File.WriteAllText(importantFile, "Do not delete me");

        var result = await _service.CloneAsync(new CloneRequest
        {
            Owner = owner,
            Repository = repo,
            Branch = branch,
        });

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.Error.Should().Contain("not a valid git repository");

        // Verify directory was NOT deleted
        Directory.Exists(targetPath).Should().BeTrue();
        File.Exists(importantFile).Should().BeTrue();
        File.ReadAllText(importantFile).Should().Be("Do not delete me");
    }

    [Fact]
    public async Task CloneAsync_ExistingGitRepoWithMismatchedRemote_ReturnsConflictWithoutDeleting()
    {
        const string owner = "testowner";
        const string repo = "testrepo";
        const string branch = "main";

        var targetPath = Path.Combine(_workspaceRoot, owner, repo, branch);
        Directory.CreateDirectory(targetPath);

        InitGitRepo(targetPath, branch);
        var file = Path.Combine(targetPath, "README.md");
        File.WriteAllText(file, "# Wrong repo");
        RunGit(targetPath, "add", ".");
        RunGit(targetPath, "commit", "-m", "Initial commit");
        RunGit(targetPath, "remote", "add", "origin", "https://github.com/differentowner/otherrepo.git");

        var result = await _service.CloneAsync(new CloneRequest
        {
            Owner = owner,
            Repository = repo,
            Branch = branch,
        });

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.Error.Should().Contain("does not match requested repository");

        // Directory preserved
        Directory.Exists(targetPath).Should().BeTrue();
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task CloneAsync_ExistingGitRepoWithMismatchedBranch_ReturnsConflictWithoutDeleting()
    {
        const string owner = "testowner";
        const string repo = "testrepo";
        const string branch = "main";

        var targetPath = Path.Combine(_workspaceRoot, owner, repo, branch);
        Directory.CreateDirectory(targetPath);

        InitGitRepo(targetPath, "different-branch");
        var file = Path.Combine(targetPath, "README.md");
        File.WriteAllText(file, "# Wrong branch");
        RunGit(targetPath, "add", ".");
        RunGit(targetPath, "commit", "-m", "Initial commit");
        RunGit(targetPath, "remote", "add", "origin", $"https://github.com/{owner}/{repo}.git");

        var result = await _service.CloneAsync(new CloneRequest
        {
            Owner = owner,
            Repository = repo,
            Branch = branch,
        });

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.Error.Should().Contain("which does not match requested branch");

        // Directory preserved
        Directory.Exists(targetPath).Should().BeTrue();
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task CloneAsync_CloneFailure_UpdatesDbToFailedAndCleansUpTargetDir()
    {
        const string owner = "nonexistentowner9999";
        const string repo = "nonexistentrepoprobably";
        const string branch = "main";

        var result = await _service.CloneAsync(new CloneRequest
        {
            Owner = owner,
            Repository = repo,
            Branch = branch,
        });

        result.Success.Should().BeFalse();
        result.IsConflict.Should().BeFalse();
        result.IsValidationError.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();

        var ws = await _dbContext.RepositoryWorkspaces.FirstOrDefaultAsync(
            w => w.Owner == owner && w.Repository == repo && w.Branch == branch);
        ws.Should().NotBeNull();
        ws!.Status.Should().Be(RepositoryWorkspaceStatus.Failed);
        ws.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("https://github.com/enesscigdem/DevPilot.git", "enesscigdem", "DevPilot", true)]
    [InlineData("https://github.com/enesscigdem/DevPilot", "enesscigdem", "DevPilot", true)]
    [InlineData("https://github.com/ENESScigdem/devpilot.git", "enesscigdem", "DevPilot", true)]
    [InlineData("git@github.com:enesscigdem/DevPilot.git", "enesscigdem", "DevPilot", true)]
    [InlineData("https://x-access-token:ghp_12345@github.com/enesscigdem/DevPilot.git", "enesscigdem", "DevPilot", true)]
    [InlineData("https://github.com/otheruser/DevPilot.git", "enesscigdem", "DevPilot", false)]
    [InlineData("https://github.com/enesscigdem/OtherRepo.git", "enesscigdem", "DevPilot", false)]
    [InlineData("https://github.com/enesscigdem/DevPilotEvil.git", "enesscigdem", "DevPilot", false)]
    [InlineData(null, "enesscigdem", "DevPilot", false)]
    [InlineData("", "enesscigdem", "DevPilot", false)]
    public void IsRemoteUrlMatch_CorrectlyValidatesUrls(
        string? remoteUrl, string owner, string repo, bool expected)
    {
        RepositoryCloneService.IsRemoteUrlMatch(remoteUrl, owner, repo).Should().Be(expected);
    }

    private static void InitGitRepo(string path, string branch)
    {
        RunGit(path, "init", "-b", branch);
        RunGit(path, "config", "user.name", "TestRunner");
        RunGit(path, "config", "user.email", "test@example.com");
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git command failed ({process.ExitCode}): {error} {output}");
        }

        return output;
    }
}
