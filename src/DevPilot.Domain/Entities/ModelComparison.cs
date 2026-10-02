namespace DevPilot.Domain.Entities;

/// <summary>
/// Runs the same approved task once per chosen model, one after another, so the results can be
/// compared side by side. Runs are sequential because a task can only have one active execution.
/// </summary>
public class ModelComparison
{
    public Guid Id { get; set; }

    public Guid DevelopmentTaskId { get; set; }

    public DevelopmentTask DevelopmentTask { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    /// <summary>When set, runs that have not started yet are skipped.</summary>
    public DateTime? CancelledAt { get; set; }

    public ICollection<ModelComparisonRun> Runs { get; set; } = new List<ModelComparisonRun>();
}

/// <summary>One model's turn in a comparison. It has started once an execution references its id.</summary>
public class ModelComparisonRun
{
    public Guid Id { get; set; }

    public Guid ModelComparisonId { get; set; }

    public ModelComparison ModelComparison { get; set; } = null!;

    /// <summary>Order in which the runs start (0-based).</summary>
    public int Position { get; set; }

    public Guid AiModelConfigId { get; set; }

    /// <summary>Model name at creation time, kept for display if the model is later deleted.</summary>
    public string ModelName { get; set; } = string.Empty;
}
