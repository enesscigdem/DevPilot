using DevPilot.Application.Goals;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Goals;

public class GoalNeedsPersonTests
{
    private static GoalExecutionState Run(bool held = false) =>
        new(
            Guid.NewGuid(),
            TaskExecutionStatus.Completed,
            ExecutionReviewStatus.Pending,
            ExecutionCommitStatus.None,
            ExecutionPushStatus.None,
            ExecutionPullRequestStatus.None,
            ExecutionPullRequestRemoteState.Unknown,
            ExecutionMergeStatus.None,
            null,
            null,
            null,
            DateTime.UtcNow,
            held);

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void AReadyPlan_WaitsForAPersonOnlyWhenAutomationDoesNotApprovePlans(bool active, bool delivers, bool expected) =>
        GoalDtoMapper.NeedsPerson(GoalPhase.PlanReady, null, active, delivers).Should().Be(expected);

    [Fact]
    public void AFinishedRun_UnderManualOrSemiAuto_WaitsForAPerson() =>
        GoalDtoMapper.NeedsPerson(GoalPhase.InReview, Run(), automationActive: true, automationDelivers: false).Should().BeTrue();

    [Fact]
    public void AFinishedRun_ThatAutomationHasNotJudgedYet_IsNotCalledOutAsNeedingAPerson() =>
        GoalDtoMapper.NeedsPerson(GoalPhase.InReview, Run(), automationActive: true, automationDelivers: true).Should().BeFalse();

    [Fact]
    public void AFinishedRun_TheSafetyGateHandedBack_WaitsForAPerson() =>
        GoalDtoMapper.NeedsPerson(GoalPhase.InReview, Run(held: true), automationActive: true, automationDelivers: true).Should().BeTrue();

    [Theory]
    [InlineData(GoalPhase.Failed, true)]
    [InlineData(GoalPhase.Running, false)]
    [InlineData(GoalPhase.Delivering, false)]
    [InlineData(GoalPhase.PullRequest, false)]
    [InlineData(GoalPhase.Merged, false)]
    public void OtherPhases_AskForAPersonOnlyWhenTheyFailed(GoalPhase phase, bool expected) =>
        GoalDtoMapper.NeedsPerson(phase, Run(), automationActive: true, automationDelivers: true).Should().Be(expected);
}
