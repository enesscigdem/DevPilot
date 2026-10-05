using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class AgenticRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DevPilotAgentic_" + Guid.NewGuid().ToString("N"));

    public AgenticRepairTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_root, "src", "Editor.ts"), "export function bold() {\n  document.execCommand('bold');\n}\n");
        File.WriteAllText(Path.Combine(_root, "package-lock.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "x.js"), "execCommand");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("""Sure: {"tool":"read_file","path":"a.ts"} thanks""", "read_file")]
    [InlineData("```json\n{\"tool\":\"run_checks\"}\n```", "run_checks")]
    [InlineData("""{"tool":"edit_file","path":"a.ts","searchReplaceEdits":[{"search":"a { b","replace":"c"}]}""", "edit_file")]
    public void Action_IsExtractedFromProseAndFences(string reply, string expectedTool) =>
        AgenticAction.TryParse(reply)!.Tool.Should().Be(expectedTool);

    [Theory]
    [InlineData("no json here")]
    [InlineData("{\"path\":\"a.ts\"}")]
    [InlineData("")]
    public void Action_WithoutTool_IsRejected(string reply) => AgenticAction.TryParse(reply).Should().BeNull();

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("node_modules/pkg/x.js")]
    [InlineData(".git/config")]
    public void ReadFile_OutsideWorkspaceOrVendored_IsDenied(string path)
    {
        var tools = new AgenticRepairTools(_root);

        tools.Execute(new AgenticAction { Tool = "read_file", Path = path }).Should().StartWith("ERROR");
    }

    [Fact]
    public void EditFile_AppliesAndTracksOriginal_AndRefusesLockfile()
    {
        var tools = new AgenticRepairTools(_root);

        var ok = tools.Execute(new AgenticAction
        {
            Tool = "edit_file",
            Path = "src/Editor.ts",
            SearchReplaceEdits = new() { new SearchReplaceEdit("document.execCommand('bold');", "wrapSelection('b');") }
        });
        var locked = tools.Execute(new AgenticAction
        {
            Tool = "edit_file",
            Path = "package-lock.json",
            SearchReplaceEdits = new() { new SearchReplaceEdit("{}", "{ }") }
        });

        ok.Should().StartWith("OK");
        File.ReadAllText(Path.Combine(_root, "src", "Editor.ts")).Should().Contain("wrapSelection");
        tools.ChangedFiles.Should().Equal("src/Editor.ts");
        tools.OriginalContents["src/Editor.ts"].Should().Contain("execCommand");
        locked.Should().StartWith("ERROR").And.Contain("lockfile");
    }

    [Fact]
    public void EditFile_WithNonMatchingSearch_ReportsErrorAndLeavesFileUntouched()
    {
        var tools = new AgenticRepairTools(_root);
        var before = File.ReadAllText(Path.Combine(_root, "src", "Editor.ts"));

        var result = tools.Execute(new AgenticAction
        {
            Tool = "edit_file",
            Path = "src/Editor.ts",
            SearchReplaceEdits = new() { new SearchReplaceEdit("does not exist", "x") }
        });

        result.Should().StartWith("ERROR");
        File.ReadAllText(Path.Combine(_root, "src", "Editor.ts")).Should().Be(before);
        tools.ChangedFiles.Should().BeEmpty();
    }

    [Fact]
    public void Search_SkipsVendoredDirectories()
    {
        var tools = new AgenticRepairTools(_root);

        var result = tools.Execute(new AgenticAction { Tool = "search", Pattern = "execCommand" });

        result.Should().Contain("src/Editor.ts:2").And.NotContain("node_modules");
    }

    [Fact]
    public async Task Loop_ReadsEditsRunsChecks_AndSucceedsWhenChecksPass()
    {
        var provider = new FakeAiProvider();
        provider.ResponsesToReturn.Enqueue("""{"tool":"read_file","path":"src/Editor.ts"}""");
        provider.ResponsesToReturn.Enqueue("""{"tool":"edit_file","path":"src/Editor.ts","searchReplaceEdits":[{"search":"document.execCommand('bold');","replace":"wrapSelection('b');"}]}""");
        provider.ResponsesToReturn.Enqueue("""{"tool":"run_checks"}""");
        var service = new AgenticRepairService(provider, NullLogger<AgenticRepairService>.Instance);
        var runs = 0;

        var outcome = await service.RunAsync(new AgenticRepairRequest(
            Guid.NewGuid(), "Fix bold", null, _root, "execCommand is not a function", Array.Empty<string>(), null,
            _ =>
            {
                runs++;
                var fixedNow = File.ReadAllText(Path.Combine(_root, "src", "Editor.ts")).Contains("wrapSelection");
                return Task.FromResult(new AgenticCheckObservation(fixedNow, fixedNow ? "ok" : "still failing", fixedNow ? 0 : 1, "f"));
            }));

        outcome.Success.Should().BeTrue();
        outcome.StopReason.Should().Be("ChecksPassed");
        outcome.CheckRuns.Should().Be(1);
        runs.Should().Be(1);
        outcome.ChangedFiles.Should().Equal("src/Editor.ts");
        provider.ReceivedRequests[1].UserPrompt.Should().Contain("document.execCommand('bold')");
    }

    [Fact]
    public async Task Loop_StopsWithNoProgress_WhenFailuresDoNotShrink()
    {
        var provider = new FakeAiProvider();
        for (var i = 0; i < 6; i++)
        {
            provider.ResponsesToReturn.Enqueue("{\"tool\":\"write_file\",\"path\":\"src/n" + i + ".ts\",\"content\":\"x\"}");
            provider.ResponsesToReturn.Enqueue("""{"tool":"run_checks"}""");
        }

        var service = new AgenticRepairService(provider, NullLogger<AgenticRepairService>.Instance);

        var outcome = await service.RunAsync(new AgenticRepairRequest(
            Guid.NewGuid(), "t", null, _root, "fail", Array.Empty<string>(), null,
            _ => Task.FromResult(new AgenticCheckObservation(false, "same", 4, "f"))));

        outcome.Success.Should().BeFalse();
        outcome.StopReason.Should().Be("NoProgress");
        outcome.CheckRuns.Should().Be(4);
    }

    [Fact]
    public async Task Loop_RefusesToRerunChecksWithoutAnEdit()
    {
        var provider = new FakeAiProvider();
        provider.ResponsesToReturn.Enqueue("""{"tool":"run_checks"}""");
        provider.ResponsesToReturn.Enqueue("""{"tool":"run_checks"}""");
        provider.ResponsesToReturn.Enqueue("""{"tool":"done","summary":"stuck"}""");
        var service = new AgenticRepairService(provider, NullLogger<AgenticRepairService>.Instance);
        var runs = 0;

        var outcome = await service.RunAsync(new AgenticRepairRequest(
            Guid.NewGuid(), "t", null, _root, "fail", Array.Empty<string>(), null,
            _ => { runs++; return Task.FromResult(new AgenticCheckObservation(false, "fail", 2, "f")); }));

        runs.Should().Be(1);
        outcome.StopReason.Should().Be("ModelDone");
    }

    [Fact]
    public async Task Loop_StopsAfterRepeatedUnparsableReplies()
    {
        var provider = new FakeAiProvider { ResponseToReturn = "I think the problem is the test." };
        var service = new AgenticRepairService(provider, NullLogger<AgenticRepairService>.Instance);

        var outcome = await service.RunAsync(new AgenticRepairRequest(
            Guid.NewGuid(), "t", null, _root, "fail", Array.Empty<string>(), null,
            _ => Task.FromResult(new AgenticCheckObservation(false, "x", 1, null))));

        outcome.StopReason.Should().Be("UnparsableReplies");
        provider.SendAsyncCallCount.Should().Be(3);
    }

    [Fact]
    public async Task Steps_AreNeverRecordedOnTheTestStage()
    {
        var provider = new FakeAiProvider();
        provider.ResponsesToReturn.Enqueue("{\"tool\":\"write_file\",\"path\":\"src/n.ts\",\"content\":\"x\"}");
        provider.ResponsesToReturn.Enqueue("{\"tool\":\"run_checks\"}");
        var recorder = new StageRecorder();
        var service = new AgenticRepairService(provider, NullLogger<AgenticRepairService>.Instance, null, recorder);

        await service.RunAsync(new AgenticRepairRequest(
            Guid.NewGuid(), "t", null, _root, "fail", Array.Empty<string>(), null,
            _ => Task.FromResult(new AgenticCheckObservation(true, "ok", 0, null))));

        recorder.Stages.Should().NotBeEmpty().And.NotContain(DevPilot.Domain.Enums.ExecutionStage.Test);
    }

    private sealed class StageRecorder : DevPilot.Application.Executions.Ports.IExecutionActivityRecorder
    {
        public List<DevPilot.Domain.Enums.ExecutionStage> Stages { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            DevPilot.Domain.Enums.ExecutionStage stage,
            DevPilot.Domain.Enums.ExecutionActivityStatus status,
            string message,
            DevPilot.Application.Executions.Models.ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Stages.Add(stage);
            return Task.CompletedTask;
        }
    }
}
