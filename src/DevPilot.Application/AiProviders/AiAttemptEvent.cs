namespace DevPilot.Application.AiProviders;

/// <summary>
/// Something that happened to one attempt of a provider call and is worth showing on the execution: a time limit
/// that ran out, an error status, a stream that stopped, a retry that is about to start or a cancellation.
/// </summary>
public sealed record AiAttemptEvent(
    int Attempt,
    int MaxAttempts,
    /// <summary>TotalTimeout | IdleTimeout | Timeout | HttpStatus | StreamFailed | NetworkError | Cancelled | Retrying.</summary>
    string Kind,
    long ElapsedMs,
    bool WillRetry,
    int? DelayMs = null,
    int? StatusCode = null,
    string? Detail = null,
    string? Model = null);
