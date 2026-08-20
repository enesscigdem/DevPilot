using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Tasks;

public sealed class TaskImpactActionStateTests
{
    public enum ActionKind
    {
        ActiveExecution,
        SyncingExecution,
        AwaitingApproval,
        Approved,
        Failed,
        Rejected,
        None
    }

    public record ActionState(
        ActionKind Kind,
        bool CanStart,
        bool CanRetry,
        bool CanApprove,
        Guid? ActiveExecutionId,
        string? Message);

    public static ActionState DeriveActionState(
        DevelopmentTaskStatus? taskStatus,
        (Guid Id, TaskExecutionStatus Status)? activeExecution)
    {
        if (activeExecution.HasValue)
        {
            return new ActionState(
                ActionKind.ActiveExecution,
                CanStart: false,
                CanRetry: false,
                CanApprove: false,
                ActiveExecutionId: activeExecution.Value.Id,
                Message: activeExecution.Value.Status == TaskExecutionStatus.Running
                    ? "Agent is currently executing the task."
                    : "Execution is queued and will begin processing shortly.");
        }

        if (taskStatus == DevelopmentTaskStatus.Executing)
        {
            return new ActionState(
                ActionKind.SyncingExecution,
                CanStart: false,
                CanRetry: false,
                CanApprove: false,
                ActiveExecutionId: null,
                Message: "Execution state is syncing with the server…");
        }

        if (taskStatus == DevelopmentTaskStatus.Approved)
        {
            return new ActionState(
                ActionKind.Approved,
                CanStart: true,
                CanRetry: false,
                CanApprove: false,
                ActiveExecutionId: null,
                Message: null);
        }

        if (taskStatus == DevelopmentTaskStatus.Failed)
        {
            return new ActionState(
                ActionKind.Failed,
                CanStart: false,
                CanRetry: true,
                CanApprove: false,
                ActiveExecutionId: null,
                Message: null);
        }

        if (taskStatus == DevelopmentTaskStatus.AwaitingApproval)
        {
            return new ActionState(
                ActionKind.AwaitingApproval,
                CanStart: false,
                CanRetry: false,
                CanApprove: true,
                ActiveExecutionId: null,
                Message: null);
        }

        if (taskStatus == DevelopmentTaskStatus.Rejected)
        {
            return new ActionState(
                ActionKind.Rejected,
                CanStart: false,
                CanRetry: false,
                CanApprove: false,
                ActiveExecutionId: null,
                Message: null);
        }

        return new ActionState(
            ActionKind.None,
            CanStart: false,
            CanRetry: false,
            CanApprove: false,
            ActiveExecutionId: null,
            Message: null);
    }

    [Fact]
    public void TaskStatusExecuting_WhenActiveExecutionNull_MakesStartAndRetryUnavailable_AndHasNoLiveExecutionId()
    {
        // Scenario 1: task claims Executing, but server execution is temporarily missing (syncing state)
        var state = DeriveActionState(DevelopmentTaskStatus.Executing, null);

        state.Kind.Should().Be(ActionKind.SyncingExecution);
        state.CanStart.Should().BeFalse();
        state.CanRetry.Should().BeFalse();
        state.ActiveExecutionId.Should().BeNull();
        state.Message.Should().Contain("syncing");
    }

    [Fact]
    public void TaskStatusFailed_WhenActiveRunningExecutionExists_MakesRetryUnavailable_AndUsesActualExecutionId()
    {
        // Scenario 2: task is Failed in DB, but server reports a running execution exists
        var runningExecId = Guid.NewGuid();
        var state = DeriveActionState(DevelopmentTaskStatus.Failed, (runningExecId, TaskExecutionStatus.Running));

        state.Kind.Should().Be(ActionKind.ActiveExecution);
        state.CanStart.Should().BeFalse();
        state.CanRetry.Should().BeFalse();
        state.ActiveExecutionId.Should().Be(runningExecId);
        state.Message.Should().Contain("executing");
    }

    [Fact]
    public void TaskStatusApproved_WhenActiveExecutionNull_MakesStartAvailable()
    {
        // Scenario 3: task is Approved and no active execution exists
        var state = DeriveActionState(DevelopmentTaskStatus.Approved, null);

        state.Kind.Should().Be(ActionKind.Approved);
        state.CanStart.Should().BeTrue();
        state.CanRetry.Should().BeFalse();
        state.ActiveExecutionId.Should().BeNull();
    }
}
