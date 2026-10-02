namespace DevPilot.Application.Executions.Options;

/// <summary>Price of one registered model, per million tokens (USD).</summary>
public sealed record AiModelPrice(string ModelName, decimal InputPerMillionTokensUsd, decimal OutputPerMillionTokensUsd);

/// <summary>
/// Price table used to turn recorded token counts into an estimated cost. Prices set on a model
/// in the panel win; the optional global price is the fallback for every other model.
/// When neither applies to a call, no cost is reported (never a made-up number).
/// </summary>
public sealed class AiPricingOptions
{
    public const string SectionName = "AiPricing";

    public decimal? InputPerMillionTokensUsd { get; set; }

    public decimal? OutputPerMillionTokensUsd { get; set; }

    /// <summary>Per-model prices, loaded from the models the user registered.</summary>
    public IReadOnlyList<AiModelPrice> ModelPrices { get; set; } = Array.Empty<AiModelPrice>();

    public bool HasGlobalPrice => InputPerMillionTokensUsd.HasValue && OutputPerMillionTokensUsd.HasValue;

    /// <summary>True when at least one price (global or per model) is available.</summary>
    public bool IsConfigured => HasGlobalPrice || ModelPrices.Count > 0;

    /// <summary>
    /// Finds the price for the model name a provider reported. Providers often return a dated or
    /// versioned name (for example "claude-sonnet-5-5-20260101"), so the longest registered name that
    /// prefixes the reported one also matches.
    /// </summary>
    public bool TryGetPrice(string? model, out decimal inputPerMillion, out decimal outputPerMillion)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            AiModelPrice? best = null;
            foreach (var price in ModelPrices)
            {
                var matches = model.Equals(price.ModelName, StringComparison.OrdinalIgnoreCase)
                              || model.StartsWith(price.ModelName, StringComparison.OrdinalIgnoreCase);
                if (matches && (best is null || price.ModelName.Length > best.ModelName.Length))
                {
                    best = price;
                }
            }

            if (best is not null)
            {
                inputPerMillion = best.InputPerMillionTokensUsd;
                outputPerMillion = best.OutputPerMillionTokensUsd;
                return true;
            }
        }

        if (HasGlobalPrice)
        {
            inputPerMillion = InputPerMillionTokensUsd!.Value;
            outputPerMillion = OutputPerMillionTokensUsd!.Value;
            return true;
        }

        inputPerMillion = 0;
        outputPerMillion = 0;
        return false;
    }
}
