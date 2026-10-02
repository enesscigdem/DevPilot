using DevPilot.Application.Executions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DevPilot.Infrastructure.AiProviders;

/// <summary>Builds <see cref="AiPricingOptions"/> from the global config plus per-model prices stored in the database.</summary>
internal static class AiPricingOptionsFactory
{
    public static AiPricingOptions Build(IConfigurationSection globalSection, DevPilotDbContext db)
    {
        var options = globalSection.Get<AiPricingOptions>() ?? new AiPricingOptions();

        // Disabled models are included on purpose: past executions that used them still need a price.
        // Both prices are required; a half-priced model would produce a wrong cost.
        options.ModelPrices = db.AiModelConfigs
            .AsNoTracking()
            .Where(m => m.InputPricePerMillionTokensUsd != null && m.OutputPricePerMillionTokensUsd != null)
            .Select(m => new AiModelPrice(
                m.ModelName,
                m.InputPricePerMillionTokensUsd!.Value,
                m.OutputPricePerMillionTokensUsd!.Value))
            .ToList();

        return options;
    }
}
