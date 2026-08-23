namespace DevPilot.Domain.Enums;

public enum ExecutionVerificationOutcome
{
    Verified,
    NoNewRegressions,
    PartiallyVerified,
    VerificationUnavailable,
    VerificationInfrastructureError,
    NeedsReview,
    Failed,
    Blocked
}
