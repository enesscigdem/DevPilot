using System.Diagnostics;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public sealed class AgenticRepairBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DevPilotAgenticBudget_" + Guid.NewGuid().ToString("N"));

    public AgenticRepairBudgetTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.ts"), "export const a = 1;\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task HangingModelCall_IsCutAtTheCallTimeout_InsteadOfBlockingTheExecution()
    {
        var provider = new FakeAiProvider
        {
            CustomHandler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new AiResponse();
            },
        };
        var service = new AgenticRepairService(
            provider,
            NullLogger<AgenticRepairService>.Instance,
            new ExecutionReliabilityOptions { AgenticRepairCallTimeoutSeconds = 1, AgenticRepairMaxMinutes = 4 });

        var clock = Stopwatch.StartNew();
        var outcome = await service.RunAsync(Request(CancellationToken.None));
        clock.Stop();

        outcome.Success.Should().BeFalse();
        outcome.StopReason.Should().Be("ProviderTimeout");
        outcome.Turns.Should().Be(1);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancellingTheExecution_StillSurfacesAsCancellation_NotAsATimeout()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakeAiProvider
        {
            CustomHandler = async (_, ct) =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return new AiResponse();
            },
        };
        var service = new AgenticRepairService(
            provider,
            NullLogger<AgenticRepairService>.Instance,
            new ExecutionReliabilityOptions { AgenticRepairCallTimeoutSeconds = 60 });

        var act = () => service.RunAsync(Request(cts.Token), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadOnlyTurns_AreRecordedSoALongLoopIsVisible()
    {
        var provider = new FakeAiProvider();
        provider.ResponsesToReturn.Enqueue("""{"tool":"read_file","path":"src/a.ts"}""");
        provider.ResponsesToReturn.Enqueue("""{"tool":"search","pattern":"export"}""");
        provider.ResponsesToReturn.Enqueue("""{"tool":"done"}""");
        var recorder = new CapturingRecorder();
        var service = new AgenticRepairService(
            provider,
            NullLogger<AgenticRepairService>.Instance,
            new ExecutionReliabilityOptions { MaxAgenticTurns = 5 },
            recorder);

        var outcome = await service.RunAsync(Request(CancellationToken.None));

        outcome.StopReason.Should().Be("ModelDone");
        recorder.Messages.Should().Contain(m => m.Contains("turn 1/5") && m.Contains("read_file"));
        recorder.Messages.Should().Contain(m => m.Contains("turn 2/5") && m.Contains("search"));
    }

    [Fact]
    public void BudgetOptions_AreClampedToSaneBounds()
    {
        var options = new ExecutionReliabilityOptions
        {
            AgenticRepairMaxMinutes = 0,
            AgenticRepairCallTimeoutSeconds = 5,
        }.Normalize();

        options.AgenticRepairMaxMinutes.Should().Be(1);
        options.AgenticRepairCallTimeoutSeconds.Should().Be(15);
        new ExecutionReliabilityOptions().AgenticRepairMaxMinutes.Should().Be(4);
        new ExecutionReliabilityOptions().AgenticRepairCallTimeoutSeconds.Should().Be(90);
    }

    private AgenticRepairRequest Request(CancellationToken _) =>
        new(Guid.NewGuid(), "Fix", null, _root, "failing", Array.Empty<string>(), null,
            _ => Task.FromResult(new AgenticCheckObservation(false, "failing", 1, "f")));

    private sealed class CapturingRecorder : IExecutionActivityRecorder
    {
        public List<string> Messages { get; } = new();

        public Task RecordActivityAsync(
            Guid executionId,
            ExecutionStage stage,
            ExecutionActivityStatus status,
            string message,
            ExecutionActivityMetadata? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
