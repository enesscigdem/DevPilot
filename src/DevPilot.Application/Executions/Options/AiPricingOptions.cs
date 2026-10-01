namespace DevPilot.Application.Executions.Options;

/// <summary>
/// Optional price table used to turn recorded token counts into an estimated cost.
/// When either price is not configured, no cost is reported (never a made-up number).
/// </summary>
public sealed class AiPricingOptions
{
    public const string SectionName = "AiPricing";

    public decimal? InputPerMillionTokensUsd { get; set; }

    public decimal? OutputPerMillionTokensUsd { get; set; }

    public bool IsConfigured => InputPerMillionTokensUsd.HasValue && OutputPerMillionTokensUsd.HasValue;
}
