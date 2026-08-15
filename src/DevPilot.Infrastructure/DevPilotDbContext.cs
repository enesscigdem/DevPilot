using DevPilot.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure;

public class DevPilotDbContext : DbContext
{
    public DevPilotDbContext(DbContextOptions<DevPilotDbContext> options)
        : base(options)
    {
    }

    public DbSet<RepositoryWorkspace> RepositoryWorkspaces => Set<RepositoryWorkspace>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RepositoryWorkspace>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Owner, e.Repository, e.Branch }).IsUnique();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Owner).HasMaxLength(200);
            entity.Property(e => e.Repository).HasMaxLength(200);
            entity.Property(e => e.Branch).HasMaxLength(200);
            entity.Property(e => e.CommitSha).HasMaxLength(100);
            entity.Property(e => e.LocalPath).HasMaxLength(500);
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
        });
    }
}
