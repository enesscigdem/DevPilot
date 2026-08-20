using System.Text.Json.Serialization;

namespace DevPilot.Application.RepositoryWorkspaces.Dtos;

public sealed class WorkspaceOverviewDto
{
    public WorkspaceHeaderDto Header { get; set; } = new();

    public List<WorkspaceAttentionItemDto> NeedsAttention { get; set; } = new();

    public WorkspaceActiveExecutionDto? ActiveExecution { get; set; }

    public WorkspaceActiveExecutionDto? ActiveAgentExecution { get; set; }

    public List<WorkspaceApprovalItemDto> AwaitingApproval { get; set; } = new();

    public List<WorkspaceFailedOrBlockedItemDto> FailedOrBlocked { get; set; } = new();

    public List<WorkspaceActivityItemDto> RecentActivity { get; set; } = new();

    public WorkspaceAnalysisOverviewDto RecentlyAnalyzed { get; set; } = new();

    public List<WorkspaceShippedItemDto> ShippedRecently { get; set; } = new();
}

public sealed class WorkspaceHeaderDto
{
    public Guid WorkspaceId { get; set; }

    public string RepositoryFullName { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public int FileCount { get; set; }

    public DateTime? LastIndexedAt { get; set; }

    public bool IsIndexed { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceAttentionKind
{
    ExecutionFailed,
    ReviewPending,
    PlanApprovalRequired,
    ReviewRejected,
    TaskRejected,
    BuildFailed,
    TestFailed,
    DeveloperAgentFailed,
    PullRequestFailed,
    CiFailed,
}

public sealed class WorkspaceAttentionItemDto
{
    public string Id { get; set; } = string.Empty;

    public WorkspaceAttentionKind Kind { get; set; }

    public Guid? TaskId { get; set; }

    public Guid? ExecutionId { get; set; }

    public string TaskDisplayId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string? MetaDetail { get; set; }

    public DateTime OccurredAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceStageState
{
    Todo,
    Active,
    Done,
    Failed,
    Blocked,
}

public sealed class WorkspaceStageStepDto
{
    public string StageKey { get; set; } = string.Empty; // "analyze", "plan", "approved", "implement", "build", "review", "pr"

    public WorkspaceStageState State { get; set; }
}

public sealed class WorkspaceActiveExecutionDto
{
    public Guid ExecutionId { get; set; }

    public Guid TaskId { get; set; }

    public string TaskDisplayId { get; set; } = string.Empty;

    public string TaskTitle { get; set; } = string.Empty;

    public string CurrentStageKey { get; set; } = "analyze";

    public List<WorkspaceStageStepDto> Stages { get; set; } = new();

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int? ElapsedSeconds { get; set; }

    public int? TokensUsed { get; set; }

    public decimal? EstimatedCost { get; set; }

    public int? ModifiedFileCount { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceApprovalKind
{
    PlanApproval,
    CodeReviewApproval,
}

public sealed class WorkspaceApprovalItemDto
{
    public string Id { get; set; } = string.Empty;

    public WorkspaceApprovalKind Kind { get; set; }

    public Guid TaskId { get; set; }

    public Guid? ExecutionId { get; set; }

    public string TaskDisplayId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public int? FilesTouched { get; set; }

    public DateTime RequestedAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceFailureKind
{
    BuildFailed,
    TestFailed,
    DeveloperAgentFailed,
    ExecutionFailed,
    ReviewRejected,
    TaskRejected,
    PullRequestFailed,
    CiFailed,
}

public sealed class WorkspaceFailedOrBlockedItemDto
{
    public string Id { get; set; } = string.Empty;

    public WorkspaceFailureKind Kind { get; set; }

    public Guid TaskId { get; set; }

    public Guid? ExecutionId { get; set; }

    public string TaskDisplayId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public DateTime FailedAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceActivityKind
{
    ExecutionStageCompleted,
    ExecutionStageFailed,
    ReviewApproved,
    ReviewRejected,
    PullRequestCreated,
    CiPassed,
    CiFailed,
    MergeCompleted,
    RepositoryIndexed,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceActivityActor
{
    Planner,
    Developer,
    Reviewer,
    System,
    User,
}

public sealed class WorkspaceActivityItemDto
{
    public string Id { get; set; } = string.Empty;

    public WorkspaceActivityKind Kind { get; set; }

    public WorkspaceActivityActor Actor { get; set; }

    public string Action { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public Guid? TaskId { get; set; }

    public Guid? ExecutionId { get; set; }

    public DateTime OccurredAt { get; set; }
}

public sealed class WorkspaceAnalysisOverviewDto
{
    public string RepositoryFullName { get; set; } = string.Empty;

    public string? Language { get; set; }

    public int? Loc { get; set; }

    public int SymbolsCount { get; set; }

    public int TypesCount { get; set; }

    public int? ReferencesCount { get; set; }

    public DateTime? LastIndexedAt { get; set; }

    public bool IsIndexed { get; set; }
}

public sealed class WorkspaceShippedItemDto
{
    public string Id { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    public Guid ExecutionId { get; set; }

    public string TaskDisplayId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int? PullRequestNumber { get; set; }

    public string? MergeCommitSha { get; set; }

    public DateTime MergedAt { get; set; }
}
