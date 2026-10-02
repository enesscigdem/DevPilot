using DevPilot.Application.Executions.Commands.VerifyExecution;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Executions;

public class VerifyExecutionCommandTests
{
    [Fact]
    public void ApprovalIsStale_WhenOutcomeChanges()
    {
        VerifyExecutionCommandHandler.ApprovalIsStale(
            wasApproved: true,
            previousOutcome: "VerificationUnavailable",
            newOutcome: "Verified",
            approvedFingerprint: "same",
            fingerprintSucceeded: true,
            recomputedFingerprint: "same")
            .Should().BeTrue();
    }

    [Fact]
    public void ApprovalIsStale_WhenFingerprintChanges()
    {
        VerifyExecutionCommandHandler.ApprovalIsStale(
            wasApproved: true,
            previousOutcome: "Verified",
            newOutcome: "Verified",
            approvedFingerprint: "before",
            fingerprintSucceeded: true,
            recomputedFingerprint: "after")
            .Should().BeTrue();
    }

    [Fact]
    public void ApprovalStays_WhenVerdictAndFingerprintAreUnchanged()
    {
        VerifyExecutionCommandHandler.ApprovalIsStale(
            wasApproved: true,
            previousOutcome: "VerificationUnavailable",
            newOutcome: "VerificationUnavailable",
            approvedFingerprint: "same",
            fingerprintSucceeded: true,
            recomputedFingerprint: "same")
            .Should().BeFalse();
    }

    [Fact]
    public void ApprovalIsStale_WhenTheNewFingerprintCannotBeProven()
    {
        VerifyExecutionCommandHandler.ApprovalIsStale(
            wasApproved: true,
            previousOutcome: "VerificationUnavailable",
            newOutcome: "VerificationUnavailable",
            approvedFingerprint: "same",
            fingerprintSucceeded: false,
            recomputedFingerprint: null)
            .Should().BeTrue();
    }

    [Fact]
    public void PendingReview_IsNotCleared()
    {
        VerifyExecutionCommandHandler.ApprovalIsStale(
            wasApproved: false,
            previousOutcome: "VerificationUnavailable",
            newOutcome: "Verified",
            approvedFingerprint: null,
            fingerprintSucceeded: true,
            recomputedFingerprint: "after")
            .Should().BeFalse();
    }
}
