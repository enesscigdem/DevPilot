namespace DevPilot.Application.AiProviders;

public sealed class AiResponse
{
    public string Model { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int? ReasoningTokens { get; set; }

    public TimeSpan Duration { get; set; }

    public bool IsSuccess { get; set; }

    public string? ErrorMessage { get; set; }

    public string? FinishReason { get; set; }

    public int? StatusCode { get; set; }

    public int? AttemptCount { get; set; }

    public string? RequestId { get; set; }

    public AiFailureKind FailureKind { get; set; } = AiFailureKind.None;

    /// <summary>The registered model that answered (set by the router): its id and name.</summary>
    public Guid? ModelConfigId { get; set; }

    public string? ModelConfigName { get; set; }

    /// <summary>Pinned, StageAssignment, Default, OnlyModel or Legacy: how the router chose the model.</summary>
    public string? RoutingSource { get; set; }

    /// <summary>Set when the model is not the one assigned to the stage, with the reason.</summary>
    public string? FallbackReason { get; set; }

    public bool IsTransient =>
        FailureKind is AiFailureKind.TransientServiceUnavailable
                    or AiFailureKind.RateLimited
                    or AiFailureKind.TimeoutOrConnection;
}
