using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Infrastructure implementation of process runner using ProcessStartInfo.
/// Restricts execution strictly to direct binary execution without shell interpolation (no sh/bash/zsh/cmd/powershell/eval).
/// </summary>
public sealed class DotnetProcessRunner : IProcessRunner
{
    private const int MaxCapturedOutputChars = 1_048_576; // 1 MB limit
    private readonly ILogger<DotnetProcessRunner> _logger;

    public DotnetProcessRunner(ILogger<DotnetProcessRunner> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProcessExecutionResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Process file name cannot be empty.", nameof(fileName));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory));
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var startTime = DateTimeOffset.UtcNow;
        using var process = new Process { StartInfo = psi };

        _logger.LogInformation(
            "Starting process '{FileName}' with {ArgCount} arguments in directory '{WorkingDirectory}'. Timeout: {TimeoutSeconds}s.",
            fileName,
            arguments.Count,
            workingDirectory,
            timeout.TotalSeconds);

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            var endTime = DateTimeOffset.UtcNow;
            return new ProcessExecutionResult(
                ExitCode: -1,
                StdOut: string.Empty,
                StdErr: string.Empty,
                StartTime: startTime,
                CompletionTime: endTime,
                Duration: endTime - startTime,
                IsTimedOut: false,
                IsTruncated: false,
                ErrorMessage: $"Failed to start process '{fileName}': {ex.Message}");
        }

        var outTask = ReadBoundedStreamAsync(process.StandardOutput, MaxCapturedOutputChars, linkedCts.Token);
        var errTask = ReadBoundedStreamAsync(process.StandardError, MaxCapturedOutputChars, linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var endTime = DateTimeOffset.UtcNow;
            var duration = endTime - startTime;

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to terminate process tree for '{FileName}'.", fileName);
            }

            // Distinguish caller cancellation from timeout
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Process '{FileName}' was canceled by caller.", fileName);
                throw;
            }

            if (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning("Process '{FileName}' timed out after {TimeoutSeconds}s.", fileName, timeout.TotalSeconds);
                return new ProcessExecutionResult(
                    ExitCode: -1,
                    StdOut: string.Empty,
                    StdErr: string.Empty,
                    StartTime: startTime,
                    CompletionTime: endTime,
                    Duration: duration,
                    IsTimedOut: true,
                    IsTruncated: false,
                    ErrorMessage: $"Process timed out after {timeout.TotalSeconds} seconds.");
            }

            throw;
        }

        var completionTime = DateTimeOffset.UtcNow;
        var (stdOut, outTruncated) = await outTask.ConfigureAwait(false);
        var (stdErr, errTruncated) = await errTask.ConfigureAwait(false);

        return new ProcessExecutionResult(
            ExitCode: process.ExitCode,
            StdOut: stdOut,
            StdErr: stdErr,
            StartTime: startTime,
            CompletionTime: completionTime,
            Duration: completionTime - startTime,
            IsTimedOut: false,
            IsTruncated: outTruncated || errTruncated);
    }

    private static async Task<(string Content, bool Truncated)> ReadBoundedStreamAsync(
        StreamReader reader,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        Memory<char> buffer = new char[4096];
        bool truncated = false;
        int totalRead = 0;

        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;

                if (!truncated)
                {
                    if (totalRead + read > maxChars)
                    {
                        int allowed = maxChars - totalRead;
                        if (allowed > 0)
                        {
                            sb.Append(buffer.Span.Slice(0, allowed));
                            totalRead += allowed;
                        }
                        truncated = true;
                    }
                    else
                    {
                        sb.Append(buffer.Span.Slice(0, read));
                        totalRead += read;
                    }
                }
                // Once truncated, we continue reading from the stream without appending to sb
                // to continuously drain stdout/stderr until the process completes and stream closes.
            }
        }
        catch (OperationCanceledException)
        {
            // Stream read canceled due to timeout or caller cancellation
        }

        return (sb.ToString(), truncated);
    }
}
