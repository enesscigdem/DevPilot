namespace DevPilot.Application.Executions.Dtos;

public sealed record ExecutionReviewDecisionDto(
    Guid ExecutionId,
    string ReviewStatus,
    DateTime DecidedAt,
    string? RejectionReason);
