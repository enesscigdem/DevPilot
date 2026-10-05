using DevPilot.Application.ProjectBrain.Commands.AskBrain;
using DevPilot.Domain.ProjectBrain.Entities;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.ProjectBrain;

public sealed class BrainPromptBuilderTests
{
    private static ProjectBrainMessage Msg(string role, string content, int minute) =>
        new() { Role = role, Content = content, CreatedAt = new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc) };

    [Fact]
    public void SystemPrompt_StatesReadOnlyAndForbidsOfferingToImplement()
    {
        var prompt = BrainPromptBuilder.BuildSystemPrompt();

        prompt.Should().Contain("READ-ONLY");
        prompt.Should().Contain("cannot change code");
        prompt.Should().Contain("Never offer");
        prompt.Should().Contain("task title and description");
        prompt.Should().Contain("language of the user's latest message");
        prompt.Should().Contain("SOURCES:");
    }

    [Fact]
    public void HistoryBlock_IsOrdered_BoundedAndSkipsErrors()
    {
        var messages = Enumerable.Range(0, 20)
            .Select(i => Msg(i % 2 == 0 ? "user" : "assistant", $"turn-{i}", i))
            .Append(Msg("assistant", "Error: provider failed", 30))
            .ToList();

        var block = BrainPromptBuilder.BuildHistoryBlock(messages);

        block.Should().NotContain("provider failed");
        block.Should().NotContain("turn-0");
        block.Should().Contain("turn-19");
        block.Split('\n').Count(l => l.StartsWith("User:") || l.StartsWith("Assistant:"))
            .Should().Be(BrainPromptBuilder.MaxHistoryMessages);
        block.IndexOf("turn-14", StringComparison.Ordinal).Should().BeLessThan(block.IndexOf("turn-19", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryBlock_ShortensLongMessages_AndIsEmptyWithoutHistory()
    {
        BrainPromptBuilder.BuildHistoryBlock(Array.Empty<ProjectBrainMessage>()).Should().BeEmpty();

        var block = BrainPromptBuilder.BuildHistoryBlock(new[] { Msg("assistant", new string('x', 5000), 1) });

        block.Length.Should().BeLessThan(1200);
    }

    [Fact]
    public void SearchQuery_ShortFollowUp_IncludesPreviousUserQuestion()
    {
        var history = new[]
        {
            Msg("user", "How does the task execution pipeline retry failed builds?", 1),
            Msg("assistant", "It retries through the repair loop.", 2)
        };

        BrainPromptBuilder.BuildSearchQuery("peki bunu task yap", history)
            .Should().Contain("task execution pipeline").And.Contain("peki bunu task yap");
    }

    [Fact]
    public void SearchQuery_LongOrFirstQuestion_IsUnchanged()
    {
        var history = new[] { Msg("user", "earlier question about something", 1) };
        const string longQuestion = "Where is the review request changes flow implemented and which handler validates the revision number?";

        BrainPromptBuilder.BuildSearchQuery(longQuestion, history).Should().Be(longQuestion);
        BrainPromptBuilder.BuildSearchQuery("short one", Array.Empty<ProjectBrainMessage>()).Should().Be("short one");
    }
}
