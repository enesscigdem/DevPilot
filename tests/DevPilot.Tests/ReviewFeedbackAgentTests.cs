using System.Diagnostics;
using System.Text.Json;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class ReviewFeedbackAgentTests : IDisposable
{
    private const string Branch = "devpilot/review-feedback-test";
    private const string CalcOriginal = "public class Calc\n{\n    public const int TimeoutSeconds = 30;\n\n    public int Add(int a, int b) => a + b;\n}\n";

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly string _worktreeDir;
    private readonly FakeAiProvider _ai = new();
    private readonly DeveloperAgent _agent;

    public ReviewFeedbackAgentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DevPilotReviewFeedback_" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        _worktreeDir = Path.Combine(_tempDir, "worktree");
        Directory.CreateDirectory(_repoDir);

        RunGit(_repoDir, "init");
        RunGit(_repoDir, "config", "user.name", "Test User");
        RunGit(_repoDir, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_repoDir, "Calc.cs"), CalcOriginal);
        File.WriteAllText(Path.Combine(_repoDir, ".env"), "SECRET=1\n");
        File.WriteAllText(Path.Combine(_repoDir, "Other.cs"), "public class Other { }\n");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "initial");
        RunGit(_repoDir, "worktree", "add", "-b", Branch, _worktreeDir, "HEAD");

        _agent = new DeveloperAgent(
            _ai,
            new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
            NullLogger<DeveloperAgent>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_repoDir))
            {
                RunGit(_repoDir, "worktree", "prune");
            }

            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Temporary directory cleanup is best effort.
        }
    }

    [Fact]
    public async Task AppliesTheFeedbackToTheExistingFilesOfTheWorktree()
    {
        _ai.ResponseToReturn = Plan(Modify("Calc.cs", "TimeoutSeconds = 30;", "TimeoutSeconds = 5;"));

        var result = await _agent.ApplyReviewFeedbackAsync(Request("Lower the timeout to 5 seconds.", "Calc.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ModifiedFiles.Should().Contain("Calc.cs");
        File.ReadAllText(Path.Combine(_worktreeDir, "Calc.cs")).Should().Contain("TimeoutSeconds = 5;");

        var prompt = _ai.ReceivedRequests.Single();
        prompt.UserPrompt.Should().Contain("Lower the timeout to 5 seconds.").And.Contain("TimeoutSeconds = 30;");
        prompt.Stage.Should().Be(DevPilot.Domain.Enums.AiStage.CodeGeneration);
    }

    [Fact]
    public async Task CanCreateANewFileWhenTheFeedbackNeedsOne()
    {
        _ai.ResponseToReturn = JsonSerializer.Serialize(new
        {
            files = new object[]
            {
                new { filePath = "CalcOptions.cs", action = "Create", newContent = "public class CalcOptions { }\n" }
            }
        });

        var result = await _agent.ApplyReviewFeedbackAsync(Request("Move the settings into an options class.", "Calc.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        File.Exists(Path.Combine(_worktreeDir, "CalcOptions.cs")).Should().BeTrue();
    }

    [Fact]
    public async Task RetriesOnceWhenTheFirstAnswerIsNotUsable_AndTellsTheModelWhy()
    {
        _ai.ResponsesToReturn.Enqueue("Sure! Here is what I would change: ...");
        _ai.ResponsesToReturn.Enqueue(Plan(Modify("Calc.cs", "TimeoutSeconds = 30;", "TimeoutSeconds = 10;")));

        var result = await _agent.ApplyReviewFeedbackAsync(Request("Use 10 seconds.", "Calc.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _ai.SendAsyncCallCount.Should().Be(2);
        _ai.ReceivedRequests[1].UserPrompt.Should().Contain("Your previous answer could not be used");
        File.ReadAllText(Path.Combine(_worktreeDir, "Calc.cs")).Should().Contain("TimeoutSeconds = 10;");
    }

    [Fact]
    public async Task RetriesOnceWhenASearchExcerptDoesNotMatch_AndLeavesTheFileAloneIfItNeverDoes()
    {
        _ai.ResponseToReturn = Plan(Modify("Calc.cs", "TimeoutSeconds = 99;", "TimeoutSeconds = 5;"));

        var result = await _agent.ApplyReviewFeedbackAsync(Request("Lower the timeout.", "Calc.cs"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("could not be applied");
        _ai.SendAsyncCallCount.Should().Be(2);
        Read("Calc.cs").Should().Be(CalcOriginal);
    }

    [Fact]
    public async Task RefusesToModifyAFileThatWasNotShownToTheModel()
    {
        _ai.ResponseToReturn = Plan(Modify("Other.cs", "public class Other { }", "public class Other { int X; }"));

        var result = await _agent.ApplyReviewFeedbackAsync(Request("Touch the other class.", "Calc.cs"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("was not provided as context");
        Read("Other.cs").Should().Be("public class Other { }\n");
    }

    [Fact]
    public async Task NeverReadsOrChangesSensitiveFiles()
    {
        _ai.ResponseToReturn = Plan(Modify(".env", "SECRET=1", "SECRET=2"));

        var result = await _agent.ApplyReviewFeedbackAsync(Request("Change the secret.", "Calc.cs", ".env"));

        result.Success.Should().BeFalse();
        _ai.ReceivedRequests.Should().OnlyContain(r => !r.UserPrompt.Contains("--- .env ---"));
        Read(".env").Should().Be("SECRET=1\n");
    }

    [Fact]
    public async Task ReportsANoChangeAnswerWithoutTouchingTheWorktree()
    {
        _ai.ResponseToReturn = JsonSerializer.Serialize(new
        {
            files = new object[]
            {
                new { filePath = "Calc.cs", action = "Modify", noChange = true, reason = "Already uses a constant." }
            }
        });

        var result = await _agent.ApplyReviewFeedbackAsync(Request("Use a constant for the timeout.", "Calc.cs"));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ModifiedFiles.Should().BeNullOrEmpty();
        result.HasResolvedNoChange.Should().BeTrue();
        Read("Calc.cs").Should().Be(CalcOriginal);
    }

    [Fact]
    public async Task EmptyFeedbackFailsWithoutCallingTheModel()
    {
        var result = await _agent.ApplyReviewFeedbackAsync(Request("   ", "Calc.cs"));

        result.Success.Should().BeFalse();
        _ai.SendAsyncCallCount.Should().Be(0);
    }

    [Fact]
    public async Task FailsWhenNoneOfTheChangedFilesCanBeRead()
    {
        var result = await _agent.ApplyReviewFeedbackAsync(Request("Fix it.", "Missing.cs"));

        result.Success.Should().BeFalse();
        _ai.SendAsyncCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ATooLargeAnswerAsksForASmallerChange()
    {
        _ai.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            Content = "{\"files\":[",
            FinishReason = "length"
        });

        var result = await _agent.ApplyReviewFeedbackAsync(Request("Rewrite everything.", "Calc.cs"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("too large");
    }

    [Fact]
    public void ModifyWithFullContentIsRejected_BecauseExistingFilesAreOnlyPatched()
    {
        var plan = new StructuredEditPlan(new[]
        {
            new FileEditSpec("Calc.cs", FileEditAction.Modify, NewContent: "class Calc { }", TargetContentHash: "x")
        });
        var context = new Dictionary<string, string> { ["Calc.cs"] = CalcOriginal };

        var act = () => DeveloperAgent.ValidateReviewFeedbackPlan(plan, context);

        act.Should().Throw<FormatException>().WithMessage("*searchReplaceEdits*");
    }

    [Fact]
    public void PromptContainsTheFeedbackTheTaskAndTheCurrentFiles()
    {
        var request = Request("Please add a null check.", "Calc.cs") with { TaskDescription = "Add a calculator.", RevisionNumber = 2 };

        var prompt = DeveloperAgent.BuildReviewFeedbackUserPrompt(
            request,
            new Dictionary<string, string> { ["Calc.cs"] = CalcOriginal });

        prompt.Should().Contain("Please add a null check.")
            .And.Contain("Add a calculator.")
            .And.Contain("Revision: 2")
            .And.Contain("--- Calc.cs ---")
            .And.Contain("TimeoutSeconds = 30;");
    }

    // git may check files out with CRLF on Windows; the tests care about content, not line endings.
    private string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(_worktreeDir, relativePath)).Replace("\r\n", "\n");

    private ReviewFeedbackRequest Request(string feedback, params string[] changedFiles) =>
        new(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            TaskTitle: "Add calculator",
            TaskDescription: string.Empty,
            AcceptanceCriteria: null,
            Feedback: feedback,
            WorkspacePath: _worktreeDir,
            BranchName: Branch,
            ChangedFiles: changedFiles);

    private static object Modify(string path, string search, string replace) =>
        new
        {
            filePath = path,
            action = "Modify",
            searchReplaceEdits = new[] { new { search, replace } }
        };

    private static string Plan(params object[] files) => JsonSerializer.Serialize(new { files });

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
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }
}
