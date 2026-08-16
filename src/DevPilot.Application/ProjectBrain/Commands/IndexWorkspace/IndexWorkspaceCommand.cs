using DevPilot.Application.CodeAnalysis;

namespace DevPilot.Application.ProjectBrain.Commands.IndexWorkspace;

public sealed record IndexWorkspaceCommand(
    string WorkspacePath,
    string? WorkspaceName = null,
    RepositoryAnalysisResult? AnalysisResult = null,
    bool GenerateEmbeddings = true);

public sealed class IndexWorkspaceResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public Guid JobId { get; set; }

    public int FilesIndexed { get; set; }

    public int ChunksIndexed { get; set; }

    public int ChunksUpdated { get; set; }

    public int ChunksSkipped { get; set; }

    public int ChunksEmbedded { get; set; }

    public int ChunksDeleted { get; set; }

    public string? EmbeddingProviderStatus { get; set; }

    public bool EmbeddingsGenerated { get; set; }

    public TimeSpan Duration { get; set; }
}
