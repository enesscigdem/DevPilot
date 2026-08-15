using DevPilot.Application.AiProviders;
using DevPilot.Infrastructure.AiProviders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DevPilot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DevPilotDb")
            ?? throw new InvalidOperationException("Connection string 'DevPilotDb' is not configured.");

        services.AddDbContext<DevPilotDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(DependencyInjection).Assembly.FullName)));

        services.AddAiProviders(configuration);

        return services;
    }

    private static IServiceCollection AddAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var providerName = configuration["AiProvider:Provider"] ?? string.Empty;

        switch (providerName)
        {
            case AiProviderNames.Kimi:
                services.AddScoped<IAiProvider, KimiAiProvider>();
                break;
            case AiProviderNames.OpenAI:
                services.AddScoped<IAiProvider, OpenAiAiProvider>();
                break;
            case AiProviderNames.Claude:
                services.AddScoped<IAiProvider, ClaudeAiProvider>();
                break;
            case AiProviderNames.Gemini:
                services.AddScoped<IAiProvider, GeminiAiProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported AI provider '{providerName}'. Supported providers: {AiProviderNames.Kimi}, {AiProviderNames.OpenAI}, {AiProviderNames.Claude}, {AiProviderNames.Gemini}.");
        }

        return services;
    }
}
