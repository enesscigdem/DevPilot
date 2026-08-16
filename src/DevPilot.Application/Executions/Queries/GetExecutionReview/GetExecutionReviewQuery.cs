using DevPilot.Application.Executions.Dtos;

namespace DevPilot.Application.Executions.Queries.GetExecutionReview;

public sealed record GetExecutionReviewQuery(Guid ExecutionId);

public enum ExecutionReviewResultStatus
{
    Success,
    NotFound,
    Conflict
}

public sealed class GetExecutionReviewResult
{
    public ExecutionReviewResultStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public ExecutionReviewDto? Review { get; set; }

    public static GetExecutionReviewResult Ok(ExecutionReviewDto review) =>
        new() { Status = ExecutionReviewResultStatus.Success, Review = review };

    public static GetExecutionReviewResult NotFound(string message) =>
        new() { Status = ExecutionReviewResultStatus.NotFound, ErrorMessage = message };

    public static GetExecutionReviewResult Conflict(string message) =>
        new() { Status = ExecutionReviewResultStatus.Conflict, ErrorMessage = message };
}

public interface IGetExecutionReviewQueryHandler
{
    Task<GetExecutionReviewResult> HandleAsync(
        GetExecutionReviewQuery query,
        CancellationToken cancellationToken = default);
}
