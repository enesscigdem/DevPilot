using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

/// <summary>Per-workspace rules for what DevPilot may do on its own. One row per repository workspace.</summary>
public class AutomationPolicy
{
    public const int DefaultMaxFilesChanged = 15;
    public const int DefaultMaxLinesChanged = 600;
    public const int DefaultMaxParallelExecutions = 2;

    public Guid Id { get; set; }

    public Guid RepositoryWorkspaceId { get; set; }

    public RepositoryWorkspace RepositoryWorkspace { get; set; } = null!;

    public AutomationLevel Level { get; set; } = AutomationLevel.Manual;

    /// <summary>Kill switch: keeps the configuration but stops every automatic action.</summary>
    public bool Paused { get; set; }

    /// <summary>
    /// Automation only acts on tasks created after this moment, so enabling it never sweeps up work that was
    /// deliberately left waiting. Reset whenever the level moves from Manual to an automatic level.
    /// </summary>
    public DateTime? ActiveSince { get; set; }

    /// <summary>A change touching more files than this is left for a person.</summary>
    public int MaxFilesChanged { get; set; } = DefaultMaxFilesChanged;

    /// <summary>A change adding plus deleting more lines than this is left for a person.</summary>
    public int MaxLinesChanged { get; set; } = DefaultMaxLinesChanged;

    /// <summary>How many executions automation keeps running at once for this workspace.</summary>
    public int MaxParallelExecutions { get; set; } = DefaultMaxParallelExecutions;

    /// <summary>Newline-separated glob patterns; a change touching a match is never handled automatically.</summary>
    public string ProtectedPaths { get; set; } = DefaultProtectedPaths;

    /// <summary>When true the merge waits for passing CI checks; a repository without checks is never auto-merged.</summary>
    public bool RequireGreenCiForMerge { get; set; } = true;

    public DateTime UpdatedAt { get; set; }

    public const string DefaultProtectedPaths =
        "**/Migrations/**\n.github/workflows/**\n**/*.env\n**/.env*\n**/appsettings.Production.json\n**/*.pem\n**/*.pfx";
}
