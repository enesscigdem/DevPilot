namespace DevPilot.Application.RepositoryWorkspaces.Dtos;

public sealed class WorkspaceArchitectureDto
{
    public WorkspaceRepositoryInfoDto Repository { get; set; } = new();

    public WorkspaceArchitectureSummaryDto Summary { get; set; } = new();

    public List<WorkspaceArchitectureNodeDto> Nodes { get; set; } = new();

    public List<WorkspaceArchitectureEdgeDto> Edges { get; set; } = new();
}

public sealed class WorkspaceArchitectureNodeDto
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Sub { get; set; } = string.Empty;

    public string Layer { get; set; } = string.Empty;

    public string ProjectType { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public List<string> KeyFiles { get; set; } = new();

    public List<string> Incoming { get; set; } = new();

    public List<string> Outgoing { get; set; } = new();

    public bool Impacted { get; set; }

    public string Why { get; set; } = string.Empty;
}

public sealed class WorkspaceArchitectureEdgeDto
{
    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    public string Type { get; set; } = "ProjectReference";
}

public sealed class WorkspaceArchitectureSummaryDto
{
    public string Status { get; set; } = "Ready";

    public int NodesCount { get; set; }

    public int EdgesCount { get; set; }

    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}
