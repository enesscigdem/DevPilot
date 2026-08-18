using System.Text.Json;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ProjectBrain.Entities;
using DevPilot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;

namespace DevPilot.Infrastructure;

public class DevPilotDbContext : DbContext
{
    private static readonly JsonSerializerOptions StructuredResultJsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    public DevPilotDbContext(DbContextOptions<DevPilotDbContext> options)
        : base(options)
    {
    }

    public DbSet<RepositoryWorkspace> RepositoryWorkspaces => Set<RepositoryWorkspace>();

    public DbSet<CodeChunk> CodeChunks => Set<CodeChunk>();

    public DbSet<IndexJob> IndexJobs => Set<IndexJob>();

    public DbSet<DevelopmentTask> DevelopmentTasks => Set<DevelopmentTask>();

    public DbSet<TaskImpactAnalysis> TaskImpactAnalyses => Set<TaskImpactAnalysis>();

    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();

    public DbSet<ExecutionActivity> ExecutionActivities => Set<ExecutionActivity>();

    public DbSet<ExecutionCiCheck> ExecutionCiChecks => Set<ExecutionCiCheck>();

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
            entity.HasIndex(e => e.RepositoryWorkspaceId);
            entity.HasIndex(e => new { e.RepositoryWorkspaceId, e.RelativePath, e.ChunkOrder }).IsUnique();
            entity.HasIndex(e => new { e.WorkspacePath, e.RelativePath, e.ChunkOrder });
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
            if (Database.IsNpgsql())
            {
                entity.Property(e => e.Embedding)
                    .HasColumnType($"vector({ProjectBrainConstants.DefaultEmbeddingDimensions})");
            }
            else
            {
                entity.Property(e => e.Embedding)
                    .HasConversion(v => v == null ? null : v.ToString(), s => string.IsNullOrEmpty(s) ? null : new Pgvector.Vector(s));
            }
            entity.HasOne(e => e.RepositoryWorkspace)
                .WithMany()
                .HasForeignKey(e => e.RepositoryWorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IndexJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RepositoryWorkspaceId);
            entity.HasIndex(e => new { e.RepositoryWorkspaceId, e.StartedAt });
            entity.HasIndex(e => e.WorkspacePath);
            entity.HasIndex(e => new { e.WorkspacePath, e.StartedAt });
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.WorkspacePath).HasMaxLength(500);
            entity.Property(e => e.WorkspaceName).HasMaxLength(200);
            entity.Property(e => e.CommitSha).HasMaxLength(100);
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
            entity.Property(e => e.EmbeddingProviderStatus).HasMaxLength(500);
            entity.HasOne(e => e.RepositoryWorkspace)
                .WithMany()
                .HasForeignKey(e => e.RepositoryWorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
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
            entity.Property(e => e.Description).HasMaxLength(10000);
            entity.Property(e => e.AcceptanceCriteria).HasMaxLength(4000);
            entity.HasOne(e => e.RepositoryWorkspace)
                .WithMany()
                .HasForeignKey(e => e.RepositoryWorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskImpactAnalysis>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DevelopmentTaskId);
            entity.HasIndex(e => new { e.DevelopmentTaskId, e.CreatedAt });
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Summary).HasMaxLength(4000);
            entity.Property(e => e.Model).HasMaxLength(200);
            entity.Property(e => e.ProviderName).HasMaxLength(100);
            entity.Property(e => e.RawResponse).HasColumnType("text");
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.StructuredResult)
                .HasConversion(new ValueConverter<ImpactAnalysisResultData?, string?>(
                    v => v == null ? null : JsonSerializer.Serialize(v, StructuredResultJsonOptions),
                    v => string.IsNullOrEmpty(v) ? null : JsonSerializer.Deserialize<ImpactAnalysisResultData>(v, StructuredResultJsonOptions)))
                .HasColumnType("jsonb");

            entity.HasOne(e => e.DevelopmentTask)
                .WithMany()
                .HasForeignKey(e => e.DevelopmentTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskExecution>(entity =>
        {
            entity.HasKey(e => e.Id);

            // General lookup index — all executions for a given task.
            entity.HasIndex(e => new { e.DevelopmentTaskId, e.Status })
                .HasDatabaseName("IX_TaskExecutions_DevelopmentTaskId_Status");

            // Unique partial index: at most one Pending or Running execution per task.
            // This is the authoritative concurrent-request guard; SqlState 23505 is caught
            // in EfExecutionRepository.StartExecutionAtomicAsync and translated to a conflict.
            entity.HasIndex(e => e.DevelopmentTaskId)
                .HasFilter("\"Status\" IN ('Pending', 'Running')")
                .IsUnique()
                .HasDatabaseName("IX_TaskExecutions_ActivePerTask");

            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);
            entity.Property(e => e.WorkspacePath).HasMaxLength(500);
            entity.Property(e => e.BranchName).HasMaxLength(200);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.StartedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.ReviewStatus)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasDefaultValue(ExecutionReviewStatus.Pending);
            entity.Property(e => e.ReviewDecidedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.ReviewRejectionReason).HasMaxLength(1000);
            entity.Property(e => e.ApprovedChangeFingerprint).HasMaxLength(100);
            entity.Property(e => e.BaseCommitSha).HasMaxLength(100);
            entity.Property(e => e.CommitStatus)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasDefaultValue(ExecutionCommitStatus.None);
            entity.Property(e => e.CommitAttemptId).HasColumnType("uuid");
            entity.Property(e => e.CommitClaimedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CommitSha).HasMaxLength(100);
            entity.Property(e => e.CommittedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PushStatus)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasDefaultValue(ExecutionPushStatus.None);
            entity.Property(e => e.PushAttemptId).HasColumnType("uuid");
            entity.Property(e => e.PushClaimedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.RemoteBranchName).HasMaxLength(200);
            entity.Property(e => e.RemoteCommitSha).HasMaxLength(100);
            entity.Property(e => e.PushedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PullRequestStatus)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasDefaultValue(ExecutionPullRequestStatus.None);
            entity.Property(e => e.PullRequestAttemptId).HasColumnType("uuid");
            entity.Property(e => e.PullRequestClaimedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PullRequestNumber);
            entity.Property(e => e.PullRequestUrl).HasMaxLength(500);
            entity.Property(e => e.PullRequestCreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PullRequestBaseBranch).HasMaxLength(200);
            entity.Property(e => e.PullRequestRemoteState)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasDefaultValue(ExecutionPullRequestRemoteState.Unknown);
            entity.Property(e => e.PullRequestIntegrityStatus)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasDefaultValue(ExecutionPullRequestIntegrityStatus.Unknown);
            entity.Property(e => e.PullRequestLastSyncedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PullRequestLastSyncAttemptAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PullRequestMergedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PullRequestClosedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.PullRequestSyncAttemptId).HasColumnType("uuid");
            entity.Property(e => e.PullRequestSyncClaimedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CiStatus)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasDefaultValue(ExecutionCiStatus.Unknown);
            entity.Property(e => e.CiLastSyncedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.MergeStatus)
                .HasConversion<string>()
                .HasMaxLength(50)
                .HasDefaultValue(ExecutionMergeStatus.None);
            entity.Property(e => e.MergeAttemptId).HasColumnType("uuid");
            entity.Property(e => e.MergeClaimedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.MergeCommitSha).HasMaxLength(100);
            entity.Property(e => e.MergedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.MergeMethod).HasMaxLength(50);

            entity.HasOne(e => e.DevelopmentTask)
                .WithMany()
                .HasForeignKey(e => e.DevelopmentTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExecutionActivity>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.ExecutionId, e.CreatedAt, e.Id })
                .HasDatabaseName("IX_ExecutionActivities_ExecutionId_CreatedAt_Id");

            entity.Property(e => e.Stage)
                .HasConversion<string>()
                .HasMaxLength(50);

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(50);

            entity.Property(e => e.Message)
                .HasMaxLength(500);

            entity.Property(e => e.MetadataJson)
                .HasColumnType("jsonb");

            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamp with time zone");

            entity.HasOne(e => e.Execution)
                .WithMany()
                .HasForeignKey(e => e.ExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExecutionCiCheck>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TaskExecutionId, e.ExternalId, e.CheckType }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Source).HasMaxLength(100);
            entity.Property(e => e.CheckType).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Status).HasMaxLength(50);
            entity.Property(e => e.Conclusion).HasMaxLength(50);
            entity.Property(e => e.StartedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");

            entity.HasOne(e => e.TaskExecution)
                .WithMany(e => e.CiChecks)
                .HasForeignKey(e => e.TaskExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

