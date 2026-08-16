using DevPilot.Application.Executions.Models;

namespace DevPilot.Application.Executions.Dtos;

public sealed record ExecutionActivityDto(
    Guid Id,
    Guid ExecutionId,
    string Stage,
    string Status,
    DateTime CreatedAt,
    string Message,
    ExecutionActivityMetadata? Metadata);
