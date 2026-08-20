namespace DevPilot.Application.AiProviders;

public enum AiFailureKind
{
    None = 0,
    TransientServiceUnavailable,
    RateLimited,
    TimeoutOrConnection,
    Permanent,
    Cancelled,
    TokenLimitExceeded
}
