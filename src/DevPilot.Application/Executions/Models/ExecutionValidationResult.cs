namespace DevPilot.Application.Executions.Models;

/// <summary>
/// Base result object for build and test validation operations inside an execution worktree.
/// </summary>
public record ExecutionValidationResult
{
    public bool Success { get; init; }
    public int? ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? CompletionTime { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? StdOut { get; init; }
    public string? StdErr { get; init; }
    public bool IsTruncated { get; init; }
    public bool IsTimedOut { get; init; }
    public string? TargetPath { get; init; }
}
