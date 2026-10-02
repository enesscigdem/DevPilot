using System.Text.Json.Serialization;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.ModelComparisons;

public sealed class StartModelComparisonRequest
{
    public Guid TaskId { get; set; }

    /// <summary>Two or three distinct, enabled models. They run in this order.</summary>
    public List<Guid> ModelIds { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelComparisonRunState
{
    /// <summary>Waiting for the previous run to finish.</summary>
    Queued,

    Running,

    Finished,

    /// <summary>The comparison was cancelled before this run started.</summary>
    Skipped,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelComparisonStatus
{
    Running,

    Completed,

    Cancelled,
}

public sealed class ModelComparisonRunDto
{
    public Guid Id { get; set; }

    public int Position { get; set; }

    public Guid ModelId { get; set; }

    public string ModelName { get; set; } = string.Empty;

    public ModelComparisonRunState State { get; set; }

    public Guid? ExecutionId { get; set; }

    public TaskExecutionStatus? ExecutionStatus { get; set; }
}

public sealed class ModelComparisonDto
{
    public Guid Id { get; set; }

    public Guid TaskId { get; set; }

    public string TaskTitle { get; set; } = string.Empty;

    public Guid RepositoryWorkspaceId { get; set; }

    public DateTime CreatedAt { get; set; }

    public ModelComparisonStatus Status { get; set; }

    public List<ModelComparisonRunDto> Runs { get; set; } = new();
}
