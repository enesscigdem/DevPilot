namespace DevPilot.Application.Executions.Models;

public enum ExecutionFailureCategory
{
    None = 0,
    SecurityViolation,
    PathGroundingFailure,
    EditApplicabilityFailure,
    ProviderFailure,
    ProviderOutputTruncated,
    CompilationFailure,
    TestFailure,
    ArchitecturePolicyViolation,
    InfrastructureFailure,
    UserDecisionRequired
}
