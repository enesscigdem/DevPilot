using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.AiProviders;

/// <summary>Connects the AI calls made while processing one execution to that execution's activity feed.</summary>
public static class AiExecutionBinding
{
    public static void Bind(
        IAiExecutionContext? context,
        Guid executionId,
        Guid? pinnedModelId,
        IExecutionActivityRecorder recorder)
    {
        if (context is null)
        {
            return;
        }

        context.PinnedModelId = pinnedModelId;
        context.ExecutionId = executionId;
        context.AttemptObserver = attempt => RecordAsync(recorder, executionId, attempt);
    }

    public static string Describe(AiAttemptEvent e)
    {
        var seconds = Math.Max(0, (int)Math.Round(e.ElapsedMs / 1000.0));
        var model = string.IsNullOrWhiteSpace(e.Model) ? string.Empty : $" [{e.Model}]";
        var retry = e.WillRetry
            ? e.DelayMs is > 0 ? $"; retrying in {Math.Max(1, (int)Math.Round(e.DelayMs.Value / 1000.0))}s" : "; retrying"
            : "; not retried";

        return e.Kind switch
        {
            "TotalTimeout" => $"Provider call exceeded the total time limit after {seconds}s (attempt {e.Attempt}/{e.MaxAttempts}){model}{retry}.",
            "IdleTimeout" => $"Provider stream stopped sending data for too long, {seconds}s into the call (attempt {e.Attempt}/{e.MaxAttempts}){model}{retry}.",
            "Timeout" => $"Provider call timed out after {seconds}s (attempt {e.Attempt}/{e.MaxAttempts}){model}{retry}.",
            "HttpStatus" => $"Provider answered HTTP {e.StatusCode} after {seconds}s (attempt {e.Attempt}/{e.MaxAttempts}){model}{retry}.",
            "StreamFailed" => $"Provider stream failed after {seconds}s (attempt {e.Attempt}/{e.MaxAttempts}){model}: {e.Detail}{retry}.",
            "NetworkError" => $"Provider connection error after {seconds}s (attempt {e.Attempt}/{e.MaxAttempts}){model}{retry}.",
            "Cancelled" => $"Provider call cancelled after {seconds}s (attempt {e.Attempt}/{e.MaxAttempts}){model}; not retried.",
            "ModelSelected" => $"Model selected for {e.Detail}: {e.Model}.",
            "ModelFallback" => $"Model fallback: {e.Detail}{(string.IsNullOrWhiteSpace(e.Model) ? string.Empty : $" Used: {e.Model}.")}",
            _ => $"Provider call attempt {e.Attempt}/{e.MaxAttempts} started{model}.",
        };
    }

    private static async Task RecordAsync(IExecutionActivityRecorder recorder, Guid executionId, AiAttemptEvent attempt)
    {
        try
        {
            // Status Started and the Attempt call kind keep this telemetry out of the verification verdict.
            await recorder.RecordActivityAsync(
                executionId,
                ExecutionStage.DeveloperAgent,
                ExecutionActivityStatus.Started,
                Describe(attempt),
                new ExecutionActivityMetadata(
                    EventKind: "ProviderAttempt",
                    ProviderCallKind: "Attempt",
                    ProviderAttemptCount: attempt.Attempt,
                    StageDurationMs: attempt.ElapsedMs,
                    Model: attempt.Model,
                    AttemptOutcome: attempt.Kind,
                    WillRetry: attempt.WillRetry),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Telemetry must never break the provider call it describes.
        }
    }
}
