using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class FullFileFallbackTests : IDisposable
{
    private const string TargetPath = "src/App.tsx";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "DevPilotFullFile_" + Guid.NewGuid().ToString("N"));

    public FullFileFallbackTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (Exception) { }
    }

    // ── Guard ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Guard_AcceptsAWholeFileThatKeepsTheOriginalAndAddsCode()
    {
        var original = BigFile(60);

        FullFileFallbackGuard.Reject(original, original + "export const added = true;\n").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Guard_RejectsEmptyOutput(string rewritten)
    {
        FullFileFallbackGuard.Reject(BigFile(60), rewritten).Should().NotBeNull();
    }

    [Fact]
    public void Guard_RejectsAnIdenticalFile_SoAFalseSuccessIsNotCounted()
    {
        var original = BigFile(60);

        FullFileFallbackGuard.Reject(original, original.Replace("\n", "\r\n")).Should().Contain("identical");
    }

    [Theory]
    [InlineData("// ... rest of the file")]
    [InlineData("// existing code")]
    [InlineData("/* unchanged */")]
    [InlineData("{/* ... rest of component */}")]
    [InlineData("# remaining code")]
    public void Guard_RejectsElisionPlaceholders(string placeholder)
    {
        var original = BigFile(60);

        FullFileFallbackGuard.Reject(original, original + placeholder + "\n").Should().Contain("elision");
    }

    [Fact]
    public void Guard_IgnoresAPlaceholderThatWasAlreadyInTheOriginal()
    {
        var original = BigFile(60) + "// existing code stays here on purpose\n";

        FullFileFallbackGuard.Reject(original, original + "export const added = 1;\n").Should().BeNull();
    }

    [Fact]
    public void Guard_RejectsAFileThatLostMostOfItsContent()
    {
        var original = BigFile(80);

        FullFileFallbackGuard.Reject(original, original[..(original.Length / 3)]).Should().Contain("dropped");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(FullFileFallbackGuard.MaxOriginalChars, true)]
    [InlineData(FullFileFallbackGuard.MaxOriginalChars + 1, false)]
    public void Guard_OnlyAllowsFilesUpToTheSizeLimit(int length, bool eligible)
    {
        FullFileFallbackGuard.IsEligible(new string('a', length)).Should().Be(eligible);
    }

    // ── Agent flow ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PatchThatNeverMatches_IsRecoveredByRewritingTheWholeFile()
    {
        var original = BigFile(120);
        var provider = await ArrangeAsync(original);
        var rewritten = original + "export const autosaveVisible = true;\n";
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope1 = 0;", "x"));
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope2 = 0;", "x"));
        provider.StructuredResponsesToReturn.Enqueue(WholeFile(rewritten));

        var result = await RunAsync(provider);

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(3);
        File.ReadAllText(Path.Combine(_workspace, TargetPath)).Should().Contain("autosaveVisible");
        provider.ReceivedRequests[2].UserPrompt.Should().Contain("COMPLETE updated file");
        provider.ReceivedRequests[2].MaxTokens.Should().BeLessThanOrEqualTo(32768);
    }

    [Fact]
    public async Task PreservesTheOriginalLineEndings_WhenTheFileIsRewritten()
    {
        var original = BigFile(120).Replace("\n", "\r\n");
        var provider = await ArrangeAsync(original);
        var rewritten = BigFile(120) + "export const added = true;\n";
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope1 = 0;", "x"));
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope2 = 0;", "x"));
        provider.StructuredResponsesToReturn.Enqueue(WholeFile(rewritten));

        var result = await RunAsync(provider);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var written = File.ReadAllText(Path.Combine(_workspace, TargetPath));
        written.Should().Contain("added = true");
        written.Replace("\r\n", "").Should().NotContain("\n");
    }

    [Fact]
    public async Task ATruncatedRewrite_IsRefused_AndTheOriginalFailureIsReported()
    {
        var original = BigFile(120);
        var provider = await ArrangeAsync(original);
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope1 = 0;", "x"));
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope2 = 0;", "x"));
        provider.StructuredResponsesToReturn.Enqueue(WholeFile(original[..(original.Length / 4)]));

        var result = await RunAsync(provider);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("after repair");
        File.ReadAllText(Path.Combine(_workspace, TargetPath)).Should().Be(original);
    }

    [Fact]
    public async Task ARewriteCutOffByTheTokenLimit_IsNeverApplied()
    {
        var original = BigFile(120);
        var provider = await ArrangeAsync(original);
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope1 = 0;", "x"));
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope2 = 0;", "x"));
        provider.StructuredResponsesToReturn.Enqueue(new AiResponse
        {
            IsSuccess = true,
            FinishReason = "length",
            Content = WholeFile(original + "export const added = 1;\n").Content,
        });

        var result = await RunAsync(provider);

        result.Success.Should().BeFalse();
        File.ReadAllText(Path.Combine(_workspace, TargetPath)).Should().Be(original);
    }

    [Fact]
    public async Task AFileOverTheSizeLimit_IsNotRewrittenWhole()
    {
        var original = BigFile(900);
        original.Length.Should().BeGreaterThan(FullFileFallbackGuard.MaxOriginalChars);
        var provider = await ArrangeAsync(original);
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope1 = 0;", "x"));
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const nope2 = 0;", "x"));

        var result = await RunAsync(provider);

        result.Success.Should().BeFalse();
        provider.SendAsyncCallCount.Should().Be(2);
    }

    [Fact]
    public async Task APatchThatMatchesOnTheFirstTry_NeverTriggersTheFallback()
    {
        var original = BigFile(120);
        var provider = await ArrangeAsync(original);
        provider.StructuredResponsesToReturn.Enqueue(Patch("export const value3 = 3;", "export const value3 = 33;"));

        var result = await RunAsync(provider);

        result.Success.Should().BeTrue(result.ErrorMessage);
        provider.SendAsyncCallCount.Should().Be(1);
        File.ReadAllText(Path.Combine(_workspace, TargetPath)).Should().Contain("value3 = 33");
    }

    [Fact]
    public void TheFallbackFlag_CannotBeSetByModelOutput()
    {
        var json = $$"""{"filePath":"{{TargetPath}}","action":"Modify","newContent":"x","WholeFileFallback":true,"wholeFileFallback":true}""";

        var spec = JsonSerializer.Deserialize<FileEditSpec>(json, JsonSerializerOptions.Web);

        spec!.WholeFileFallback.Should().BeFalse();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string BigFile(int lines)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            builder.Append("export const value").Append(i).Append(" = ").Append(i).Append(";  // padding to give the file some weight\n");
        }

        return builder.ToString();
    }

    private static AiResponse Patch(string search, string replace) => new()
    {
        IsSuccess = true,
        Content = JsonSerializer.Serialize(new
        {
            filePath = TargetPath,
            action = "Modify",
            searchReplaceEdits = new[] { new { search, replace } },
        }),
    };

    private static AiResponse WholeFile(string content) => new()
    {
        IsSuccess = true,
        Content = JsonSerializer.Serialize(new { filePath = TargetPath, action = "Modify", newContent = content }),
    };

    private async Task<FakeAiProvider> ArrangeAsync(string original)
    {
        File.WriteAllBytes(Path.Combine(_workspace, TargetPath), new UTF8Encoding(false).GetBytes(original));
        await RunGitAsync("init");
        await RunGitAsync("config", "user.name", "Test User");
        await RunGitAsync("config", "user.email", "test@example.com");
        await RunGitAsync("add", ".");
        await RunGitAsync("commit", "-m", "init");
        await RunGitAsync("checkout", "-b", "devpilot/fallback");
        return new FakeAiProvider();
    }

    private Task<DeveloperAgentResult> RunAsync(FakeAiProvider provider)
    {
        var agent = new DeveloperAgent(
            provider,
            new WorktreeEditApplier(NullLogger<WorktreeEditApplier>.Instance),
            NullLogger<DeveloperAgent>.Instance);

        return agent.GenerateAndApplyEditsAsync(new DeveloperAgentRequest(
            TaskId: Guid.NewGuid(),
            ExecutionId: Guid.Empty,
            TaskTitle: "Show autosave status",
            TaskDescription: "Make the autosave state visible",
            AcceptanceCriteria: "Status is visible",
            ImpactAnalysisSummary: "Edit App",
            ProposedPlan: "Edit src/App.tsx",
            ImpactedFilePaths: new[] { TargetPath },
            WorkspacePath: _workspace,
            BranchName: "devpilot/fallback",
            ImpactedFiles: new[] { new ImpactedFileDetail(TargetPath, "Modify", "Show autosave status") }));
    }

    private async Task RunGitAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
        }
    }
}
