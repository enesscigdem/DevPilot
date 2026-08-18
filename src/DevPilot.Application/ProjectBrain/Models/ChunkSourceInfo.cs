using DevPilot.Application.CodeAnalysis;

namespace DevPilot.Application.ProjectBrain.Models;

public sealed class ChunkSourceInfo
{
    public Guid? RepositoryWorkspaceId { get; set; }

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

    public int StartLine { get; set; }

    public int EndLine { get; set; }

    public int ChunkOrder { get; set; }

    public string Content { get; set; } = string.Empty;

    public int TokenCount { get; set; }
}

public sealed class ChunkMetadata
{
    public Guid? RepositoryWorkspaceId { get; set; }

    public string WorkspacePath { get; set; } = string.Empty;

    public string WorkspaceName { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string DeclaredSymbols { get; set; } = string.Empty;

    public RepositoryAnalysisResult? RoslynAnalysis { get; set; }
}
