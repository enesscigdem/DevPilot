using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure;

public class DevPilotDbContext : DbContext
{
    public DevPilotDbContext(DbContextOptions<DevPilotDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
