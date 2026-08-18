namespace DevPilot.Application.RepositoryWorkspaces.Dtos;

public sealed class WorkspaceAnalysisDto
{
    public WorkspaceRepositoryInfoDto Repository { get; set; } = new();

    public WorkspaceAnalysisSummaryDto Summary { get; set; } = new();

    public List<WorkspaceFileNodeDto> FileTree { get; set; } = new();

    public List<WorkspaceProjectDto> Projects { get; set; } = new();

    public List<WorkspaceTechnologyDto> Technologies { get; set; } = new();

    public List<WorkspaceEndpointDto> Endpoints { get; set; } = new();

    public List<string> Warnings { get; set; } = new();
}

public sealed class WorkspaceRepositoryInfoDto
{
    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;
}

public sealed class WorkspaceAnalysisSummaryDto
{
    public string Status { get; set; } = "Ready";

    public string Engine { get; set; } = "Roslyn Workspace Analysis";

    /// <summary>
    /// Total count of member symbols (methods, constructors, properties, and enum values) extracted across analyzed types.
    /// </summary>
    public int SymbolsCount { get; set; }

    /// <summary>
    /// Total count of declared type definitions (classes, interfaces, records, enums, controllers) discovered.
    /// </summary>
    public int TypesCount { get; set; }

    /// <summary>
    /// Total count of project-to-project references resolved across analyzed projects.
    /// </summary>
    public int ReferencesCount { get; set; }

    public DateTime AnalyzedAt { get; set; }

    public List<WorkspaceAnalysisStepDto> Steps { get; set; } = new();
}

public sealed class WorkspaceAnalysisStepDto
{
    public string Label { get; set; } = string.Empty;

    public bool Done { get; set; }
}

public sealed class WorkspaceFileNodeDto
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Type { get; set; } = "file"; // "folder" | "file"

    public string? Lang { get; set; }

    public List<WorkspaceFileNodeDto>? Children { get; set; }
}

public sealed class WorkspaceProjectDto
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string ProjectType { get; set; } = "Unknown";

    public string Layer { get; set; } = "Other";

    public int FileCount { get; set; }

    public string? TargetFramework { get; set; }

    public List<WorkspaceProjectReferenceDto> ProjectReferences { get; set; } = new();

    public bool CompilationSucceeded { get; set; }

    public List<string> CompilationErrors { get; set; } = new();

    public List<string> Warnings { get; set; } = new();
}

public sealed class WorkspaceProjectReferenceDto
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

public sealed class WorkspaceTechnologyDto
{
    public string Name { get; set; } = string.Empty;

    public string? Version { get; set; }

    public string Kind { get; set; } = "library";
}

public sealed class WorkspaceEndpointDto
{
    public string Method { get; set; } = "GET";

    public string Route { get; set; } = string.Empty;

    public string Controller { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public bool Auth { get; set; }

    public string SourcePath { get; set; } = string.Empty;
}
