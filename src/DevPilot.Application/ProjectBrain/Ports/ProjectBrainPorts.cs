using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.ProjectBrain.Models;
using DevPilot.Domain.ProjectBrain.Entities;

namespace DevPilot.Application.ProjectBrain.Ports;

public interface IRepositoryChunker
{
    Task<IReadOnlyList<CodeChunk>> ChunkRepositoryAsync(
        ChunkMetadata metadata,
        RepositoryAnalysisResult? analysisResult,
        CancellationToken cancellationToken = default);
}

public interface ICodeChunkRepository
{
    Task<IReadOnlyDictionary<string, CodeChunk>> GetExistingChunksAsync(
        string workspacePath,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeChunk>> GetAllByWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken = default);

    Task AddRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default);

    Task UpdateRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default);

    Task DeleteRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default);

    Task<int> CountByWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default);
}

public interface IIndexJobRepository
{
    Task AddAsync(IndexJob job, CancellationToken cancellationToken = default);

    Task UpdateAsync(IndexJob job, CancellationToken cancellationToken = default);

    Task<IndexJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndexJob>> GetByWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken = default);
}

public interface ISemanticSearchService
{
    Task<SemanticSearchResult> SearchAsync(
        SemanticSearchQuery query,
        float[] queryEmbedding,
        CancellationToken cancellationToken = default);
}

public sealed class SemanticSearchQuery
{
    public string WorkspacePath { get; set; } = string.Empty;

    public string QueryText { get; set; } = string.Empty;

    public int MaxResults { get; set; } = 10;

    public double? MinScore { get; set; }
}

public sealed class SemanticSearchResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public List<SemanticSearchHit> Hits { get; set; } = new();

    public string? ProviderName { get; set; }
}

public sealed class SemanticSearchHit
{
    public CodeChunk Chunk { get; set; } = null!;

    public double Score { get; set; }
}
