using DevPilot.Application.AiProviders;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.GitProviders;
using DevPilot.Application.RepositoryClone;
using DevPilot.Infrastructure.AiProviders;
using DevPilot.Infrastructure.CodeAnalysis;
using DevPilot.Infrastructure.GitProviders;
using DevPilot.Infrastructure.RepositoryClone;
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
        services.AddGitProviders(configuration);
        services.AddRepositoryClone(configuration);
        services.AddScoped<IRepositoryAnalyzer, RoslynRepositoryAnalyzer>();

        return services;
    }

    private static IServiceCollection AddRepositoryClone(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RepositoryCloneOptions>(
            configuration.GetSection(RepositoryCloneOptions.SectionName));

        services.AddScoped<IRepositoryCloneService, RepositoryCloneService>();

        return services;
    }

    private static IServiceCollection AddAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var providerName = configuration["AiProvider:Provider"] ?? string.Empty;

        if (providerName == AiProviderNames.Kimi)
        {
            services.AddHttpClient("Kimi", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(60);
            });
        }

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

    private static IServiceCollection AddGitProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var providerName = configuration["GitProvider:Provider"] ?? string.Empty;

        switch (providerName)
        {
            case GitProviderNames.GitHub:
                services.AddHttpClient(GitHubGitProvider.HttpClientName, client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
                services.AddScoped<IGitProvider, GitHubGitProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Git provider '{providerName}'. Supported providers: {GitProviderNames.GitHub}.");
        }

        return services;
    }
}
