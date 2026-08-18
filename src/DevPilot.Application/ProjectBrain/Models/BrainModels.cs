namespace DevPilot.Application.ProjectBrain.Models;

public sealed class BrainStatusDto
{
    public Guid WorkspaceId { get; set; }

    public string State { get; set; } = "unindexed"; // "ready" | "indexing" | "unindexed" | "stale" | "failed"

    public int TotalFiles { get; set; }

    public int TotalTypes { get; set; }

    public int TotalSymbols { get; set; }

    public int TotalChunks { get; set; }

    public DateTime? LastIndexedAt { get; set; }

    public string? LastIndexedRelative { get; set; }

    public string Engine { get; set; } = "Roslyn workspace analysis";

    public List<BrainIndexStepDto> Steps { get; set; } = new();

    public List<BrainSourceGroupDto> SourceGroups { get; set; } = new();

    public List<string> SuggestedQuestions { get; set; } = new();
}

public sealed class BrainIndexStepDto
{
    public string Label { get; set; } = string.Empty;

    public bool Done { get; set; }
}

public sealed class BrainSourceGroupDto
{
    public string Project { get; set; } = string.Empty;

    public string Layer { get; set; } = "Unknown"; // "Web" | "Application" | "Domain" | "Infrastructure" | "Tests" | "Unknown"

    public int Files { get; set; }

    public int Symbols { get; set; }

    public bool Indexed { get; set; }
}

public sealed class BrainCitationDto
{
    public string File { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Lines { get; set; } = string.Empty;

    public int StartLine { get; set; }

    public int EndLine { get; set; }

    public string? Symbol { get; set; }

    public string? Lang { get; set; }

    public string Snippet { get; set; } = string.Empty;
}

public sealed class BrainContextFileDto
{
    public string File { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public int Relevance { get; set; }
}

public sealed class BrainChatResult
{
    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public string Role { get; set; } = "assistant";

    public string Content { get; set; } = string.Empty;

    public int? Confidence { get; set; } // Grounding/retrieval score (0-100)

    public string Elapsed { get; set; } = string.Empty;

    public List<BrainCitationDto> Citations { get; set; } = new();

    public List<BrainContextFileDto> ContextFiles { get; set; } = new();

    public string RetrievalMode { get; set; } = "lexical";

    public bool IsUnindexed { get; set; }

    public bool IsStale { get; set; }
}
