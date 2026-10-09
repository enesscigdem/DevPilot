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

    public DbSet<GitHubInstallationConnection> GitHubInstallationConnections => Set<GitHubInstallationConnection>();

    public DbSet<GitConnection> GitConnections => Set<GitConnection>();

    public DbSet<TrackerConnection> TrackerConnections => Set<TrackerConnection>();

    public DbSet<RepositoryWorkspace> RepositoryWorkspaces => Set<RepositoryWorkspace>();

    public DbSet<CodeChunk> CodeChunks => Set<CodeChunk>();

    public DbSet<IndexJob> IndexJobs => Set<IndexJob>();

    public DbSet<DevelopmentTask> DevelopmentTasks => Set<DevelopmentTask>();

    public DbSet<TaskImpactAnalysis> TaskImpactAnalyses => Set<TaskImpactAnalysis>();

    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();

    public DbSet<ExecutionActivity> ExecutionActivities => Set<ExecutionActivity>();
    public DbSet<ExecutionRevision> ExecutionRevisions => Set<ExecutionRevision>();

    public DbSet<ExecutionCiCheck> ExecutionCiChecks => Set<ExecutionCiCheck>();

    public DbSet<ProjectBrainConversation> ProjectBrainConversations => Set<ProjectBrainConversation>();

    public DbSet<ProjectBrainMessage> ProjectBrainMessages => Set<ProjectBrainMessage>();

    public DbSet<AiModelConfig> AiModelConfigs => Set<AiModelConfig>();

    public DbSet<AiStageAssignment> AiStageAssignments => Set<AiStageAssignment>();

    public DbSet<ModelComparison> ModelComparisons => Set<ModelComparison>();

    public DbSet<ModelComparisonRun> ModelComparisonRuns => Set<ModelComparisonRun>();

    public DbSet<AutomationPolicy> AutomationPolicies => Set<AutomationPolicy>();

    public DbSet<Goal> Goals => Set<Goal>();

    public DbSet<GoalTask> GoalTasks => Set<GoalTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<GitHubInstallationConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExternalInstallationId).IsUnique();
            entity.HasIndex(e => e.AccountLogin);
            entity.Property(e => e.AccountLogin).HasMaxLength(200);
            entity.Property(e => e.AccountType).HasMaxLength(50);
            entity.Property(e => e.TargetAvatarUrl).HasMaxLength(500);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ConnectedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.LastVerifiedAt).HasColumnType("timestamp with time zone");
        });

        modelBuilder.Entity<TrackerConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.BaseUrl).HasMaxLength(500);
            entity.Property(e => e.Email).HasMaxLength(320);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.EncryptedToken).HasMaxLength(4000);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
        });

        modelBuilder.Entity<GitConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Provider, e.Host });
            entity.Property(e => e.Provider).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Host).HasMaxLength(255);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.Username).HasMaxLength(200);
            entity.Property(e => e.EncryptedToken).HasMaxLength(4000);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
        });

        modelBuilder.Entity<RepositoryWorkspace>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Host, e.Owner, e.Repository, e.Branch }).IsUnique();
            entity.HasIndex(e => e.GitConnectionId);
            entity.Property(e => e.Provider).HasConversion<string>().HasMaxLength(50).HasDefaultValue(GitProviderKind.GitHub);
            entity.Property(e => e.Host).HasMaxLength(255).HasDefaultValue("github.com");
            entity.HasOne(e => e.GitConnection)
                .WithMany(e => e.Workspaces)
                .HasForeignKey(e => e.GitConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.GitHubInstallationConnectionId);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Owner).HasMaxLength(200);
            entity.Property(e => e.Repository).HasMaxLength(200);
            entity.Property(e => e.Branch).HasMaxLength(200);
            entity.Property(e => e.CommitSha).HasMaxLength(100);
            entity.Property(e => e.LocalPath).HasMaxLength(500);
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
            entity.Property(e => e.RemoteUrl).HasMaxLength(500);
            entity.HasOne(e => e.GitHubInstallationConnection)
                .WithMany(e => e.Workspaces)
                .HasForeignKey(e => e.GitHubInstallationConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Goal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.RepositoryWorkspaceId, e.CreatedAt });
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.Text).HasMaxLength(20000);
            entity.Property(e => e.PlanSource).HasMaxLength(20);
            entity.HasOne(e => e.RepositoryWorkspace)
                .WithMany()
                .HasForeignKey(e => e.RepositoryWorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GoalTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.GoalId, e.Key }).IsUnique();
            entity.HasIndex(e => e.DevelopmentTaskId).IsUnique();
            entity.Property(e => e.Key).HasMaxLength(20);
            entity.Property(e => e.Size).HasMaxLength(20);
            entity.Property(e => e.Areas).HasMaxLength(8000);
            entity.Property(e => e.DependsOn).HasMaxLength(400);
            entity.Property(e => e.BlockedBy).HasMaxLength(400);
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.HasOne(e => e.Goal)
                .WithMany(g => g.Tasks)
                .HasForeignKey(e => e.GoalId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.DevelopmentTask)
                .WithMany()
                .HasForeignKey(e => e.DevelopmentTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationPolicy>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RepositoryWorkspaceId).IsUnique();
            entity.Property(e => e.Level).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ProtectedPaths).HasMaxLength(4000);
            entity.Property(e => e.ConflictMode).HasConversion<string>().HasMaxLength(20);
            entity.HasOne(e => e.RepositoryWorkspace)
                .WithMany()
                .HasForeignKey(e => e.RepositoryWorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
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
            entity.Property(e => e.ExternalSource).HasMaxLength(50);
            entity.Property(e => e.ExternalKey).HasMaxLength(100);
            entity.Property(e => e.ExternalOrigin).HasMaxLength(300).HasDefaultValue(string.Empty);
            entity.Property(e => e.ExternalUrl).HasMaxLength(500);
            entity.HasIndex(e => new { e.RepositoryWorkspaceId, e.ExternalSource, e.ExternalOrigin, e.ExternalKey })
                .IsUnique()
                .HasFilter("\"ExternalKey\" IS NOT NULL");
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
            entity.HasIndex(e => e.DevelopmentTaskId)
                .HasFilter("\"Status\" = 'InProgress'")
                .IsUnique()
                .HasDatabaseName("IX_TaskImpactAnalyses_ActivePerTask");
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

            entity.HasIndex(e => new { e.Status, e.LeaseExpiresAt })
                .HasDatabaseName("IX_TaskExecutions_Status_LeaseExpiresAt");

            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);
            entity.Property(e => e.VerificationOutcome).HasMaxLength(64);
            entity.Property(e => e.WorkspacePath).HasMaxLength(500);
            entity.Property(e => e.BranchName).HasMaxLength(200);
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.PinnedAiModelName).HasMaxLength(120);
            entity.HasIndex(e => e.ModelComparisonRunId).HasDatabaseName("IX_TaskExecutions_ModelComparisonRunId");
            entity.Property(e => e.LeaseToken).HasColumnType("uuid");
            entity.Property(e => e.HeartbeatAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.LeaseExpiresAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CancellationRequestedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CancelledAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CancellationReason).HasMaxLength(500);
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
            entity.Property(e => e.InitialBaseCommitSha).HasMaxLength(100);
            entity.Property(e => e.RevisionCount).HasDefaultValue(0);
            entity.Property(e => e.LastChangeRequest).HasMaxLength(2000);
            entity.Property(e => e.LastChangeRequestAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.LastChangeRequestResult).HasMaxLength(500);
            entity.Property(e => e.ChangeRequestCount).HasDefaultValue(0);
            entity.Property(e => e.InitialRunCompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.RevisionBaseSnapshotSha).HasMaxLength(100);
            entity.Property(e => e.RevisionResultSnapshotSha).HasMaxLength(100);
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

        modelBuilder.Entity<ExecutionRevision>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ExecutionId, e.Number }).IsUnique();
            entity.Property(e => e.Feedback).HasMaxLength(2000);
            entity.Property(e => e.Result).HasMaxLength(500);
            entity.Property(e => e.BaseSnapshotSha).HasMaxLength(100);
            entity.Property(e => e.ResultSnapshotSha).HasMaxLength(100);
            entity.Property(e => e.RequestedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamp with time zone");
            entity.HasOne<TaskExecution>().WithMany().HasForeignKey(e => e.ExecutionId).OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<ProjectBrainConversation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RepositoryWorkspaceId);
            entity.HasIndex(e => new { e.RepositoryWorkspaceId, e.UpdatedAt });
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            entity.HasOne<RepositoryWorkspace>()
                .WithMany()
                .HasForeignKey(e => e.RepositoryWorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectBrainMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ConversationId);
            entity.HasIndex(e => new { e.ConversationId, e.CreatedAt });
            entity.Property(e => e.Role).HasMaxLength(50);
            entity.Property(e => e.Content).HasColumnType("text");
            entity.Property(e => e.Elapsed).HasMaxLength(50);
            entity.Property(e => e.CitationsJson).HasColumnType("jsonb");
            entity.Property(e => e.ContextFilesJson).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");

            entity.HasOne(e => e.Conversation)
                .WithMany(e => e.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiModelConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            // At most one default model.
            entity.HasIndex(e => e.IsDefault).IsUnique().HasFilter("\"IsDefault\" = true");
            entity.Property(e => e.Name).HasMaxLength(120);
            entity.Property(e => e.AdapterType).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.BaseUrl).HasMaxLength(500);
            entity.Property(e => e.ModelName).HasMaxLength(200);
            entity.Property(e => e.ProtectedApiKey).HasColumnType("text");
            entity.Property(e => e.ApiKeyHint).HasMaxLength(20);
            entity.Property(e => e.InputPricePerMillionTokensUsd).HasPrecision(18, 6);
            entity.Property(e => e.OutputPricePerMillionTokensUsd).HasPrecision(18, 6);
            entity.Property(e => e.LastTestMessage).HasMaxLength(1000);
            entity.Property(e => e.LastTestOutcome).HasMaxLength(20);
            entity.Property(e => e.LastTestedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
        });

        modelBuilder.Entity<ModelComparison>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DevelopmentTaskId);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CancelledAt).HasColumnType("timestamp with time zone");

            entity.HasOne(e => e.DevelopmentTask)
                .WithMany()
                .HasForeignKey(e => e.DevelopmentTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModelComparisonRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ModelComparisonId, e.Position }).IsUnique();
            entity.Property(e => e.ModelName).HasMaxLength(120);

            entity.HasOne(e => e.ModelComparison)
                .WithMany(e => e.Runs)
                .HasForeignKey(e => e.ModelComparisonId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiStageAssignment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Stage).IsUnique();
            entity.Property(e => e.Stage).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            entity.HasOne(e => e.AiModelConfig)
                .WithMany()
                .HasForeignKey(e => e.AiModelConfigId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

