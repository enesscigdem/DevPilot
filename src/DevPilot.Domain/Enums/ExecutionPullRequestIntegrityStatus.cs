namespace DevPilot.Domain.Enums;

public enum ExecutionPullRequestIntegrityStatus
{
    Unknown = 0,
    Valid = 1,
    HeadChanged = 2,
    IdentityMismatch = 3
}
