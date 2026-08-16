using Pgvector;

namespace DevPilot.Domain.ProjectBrain.Entities;

public class CodeChunk
{
    public Guid Id { get; set; }

    public string WorkspacePath { get; set; } = string.Empty;

    public string WorkspaceName { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string? SymbolName { get; set; }

    public string? TypeName { get; set; }

    public string? MethodName { get; set; }

    public string DeclaredSymbols { get; set; } = string.Empty;

    public int ChunkOrder { get; set; }

    public int StartLine { get; set; }

    public int EndLine { get; set; }

    public string Content { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public Vector? Embedding { get; set; }

    public int TokenCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? IndexJobId { get; set; }

    public static string BuildLookupKey(string relativePath, int chunkOrder)
    {
        return $"{relativePath}::{chunkOrder}";
    }

    public string GetLookupKey()
    {
        return BuildLookupKey(RelativePath, ChunkOrder);
    }
}
