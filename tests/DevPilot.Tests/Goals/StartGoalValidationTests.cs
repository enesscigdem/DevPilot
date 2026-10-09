using DevPilot.Application.Goals;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Goals;

/// <summary>A goal whose dependencies cannot be satisfied must be refused before any task is created.</summary>
public sealed class StartGoalValidationTests
{
    // Validation fails before the handler touches any collaborator, so none are needed here.
    private static readonly StartGoalCommandHandler Handler =
        new(null!, null!, null!, NullLogger<StartGoalCommandHandler>.Instance);

    private static GoalTaskInput Task(string key, params string[] dependsOn) =>
        new() { Key = key, Title = $"Task {key}", DependsOn = dependsOn.ToList() };

    private static Task<StartGoalResult> Start(params GoalTaskInput[] tasks) =>
        Handler.HandleAsync(new StartGoalCommand(Guid.NewGuid(), "goal", "ai", tasks, 0, 0, null));

    [Fact]
    public async Task A_task_cannot_depend_on_itself()
    {
        var result = await Start(Task("t1", "t1"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("itself");
    }

    [Fact]
    public async Task A_dependency_on_an_unknown_task_is_refused_not_dropped()
    {
        var result = await Start(Task("t1", "nope"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("nope");
    }

    [Fact]
    public async Task Tasks_waiting_on_each_other_in_a_circle_are_refused()
    {
        var result = await Start(Task("t1", "t2"), Task("t2", "t1"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("circle");
    }
}
