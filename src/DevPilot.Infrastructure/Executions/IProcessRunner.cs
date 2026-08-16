namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Execution result of a process invocation.
/// </summary>
public sealed record ProcessExecutionResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    DateTimeOffset StartTime,
    DateTimeOffset CompletionTime,
    TimeSpan Duration,
    bool IsTimedOut,
    bool IsTruncated,
    string? ErrorMessage = null);

/// <summary>
/// Abstraction for executing external processes without invoking shell interpreters.
/// Allows unit testing by mocking process execution.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
