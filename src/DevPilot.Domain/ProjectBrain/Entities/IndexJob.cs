using DevPilot.Domain.Entities;

namespace DevPilot.Domain.ProjectBrain.Entities;

public class IndexJob
{
    public Guid Id { get; set; }

    public Guid? RepositoryWorkspaceId { get; set; }

    public RepositoryWorkspace? RepositoryWorkspace { get; set; }

    public string? CommitSha { get; set; }

    public string WorkspacePath { get; set; } = string.Empty;

    public string WorkspaceName { get; set; } = string.Empty;

    public IndexJobStatus Status { get; set; }

    public int TotalFiles { get; set; }

    public int ProcessedFiles { get; set; }

    public int TotalChunks { get; set; }

    public int ProcessedChunks { get; set; }

    public int ChunksEmbedded { get; set; }

    public int ChunksSkipped { get; set; }

    public string? ErrorMessage { get; set; }

    public string? EmbeddingProviderStatus { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
