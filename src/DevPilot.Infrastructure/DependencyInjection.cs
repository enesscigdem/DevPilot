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

        return services;
    }
}
