using DevPilot.Domain.Entities;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ProjectBrain.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace DevPilot.Infrastructure;

public class DevPilotDbContext : DbContext
{
    public DevPilotDbContext(DbContextOptions<DevPilotDbContext> options)
        : base(options)
    {
    }

    public DbSet<RepositoryWorkspace> RepositoryWorkspaces => Set<RepositoryWorkspace>();

    public DbSet<CodeChunk> CodeChunks => Set<CodeChunk>();

    public DbSet<IndexJob> IndexJobs => Set<IndexJob>();

    public DbSet<DevelopmentTask> DevelopmentTasks => Set<DevelopmentTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

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

        modelBuilder.Entity<CodeChunk>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.WorkspacePath, e.RelativePath, e.ChunkOrder }).IsUnique();
            entity.HasIndex(e => e.ContentHash);
            entity.Property(e => e.WorkspacePath).HasMaxLength(500);
            entity.Property(e => e.WorkspaceName).HasMaxLength(200);
            entity.Property(e => e.ProjectName).HasMaxLength(200);
            entity.Property(e => e.FilePath).HasMaxLength(500);
            entity.Property(e => e.RelativePath).HasMaxLength(500);
            entity.Property(e => e.Language).HasMaxLength(50);
            entity.Property(e => e.SymbolName).HasMaxLength(200);
            entity.Property(e => e.TypeName).HasMaxLength(200);
            entity.Property(e => e.MethodName).HasMaxLength(200);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            entity.Property(e => e.Embedding)
                .HasColumnType($"vector({ProjectBrainConstants.DefaultEmbeddingDimensions})");
        });

        modelBuilder.Entity<IndexJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspacePath);
            entity.HasIndex(e => new { e.WorkspacePath, e.StartedAt });
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.WorkspacePath).HasMaxLength(500);
            entity.Property(e => e.WorkspaceName).HasMaxLength(200);
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
            entity.Property(e => e.EmbeddingProviderStatus).HasMaxLength(500);
        });

        modelBuilder.Entity<DevelopmentTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RepositoryWorkspaceId);
            entity.HasIndex(e => new { e.RepositoryWorkspaceId, e.Status });
            entity.HasIndex(e => new { e.RepositoryWorkspaceId, e.Priority });
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Priority).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(4000);
            entity.Property(e => e.AcceptanceCriteria).HasMaxLength(4000);
            entity.HasOne(e => e.RepositoryWorkspace)
                .WithMany()
                .HasForeignKey(e => e.RepositoryWorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

