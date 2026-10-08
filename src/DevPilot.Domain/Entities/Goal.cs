using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

/// <summary>
/// Something the person wanted done, written in their own words, together with the tasks it was split into.
/// A goal owns the order and timing of its tasks; each task is an ordinary development task.
/// </summary>
public class Goal
{
    public Guid Id { get; set; }

    public Guid RepositoryWorkspaceId { get; set; }

    public RepositoryWorkspace RepositoryWorkspace { get; set; } = null!;

    public string Title { get; set; } = string.Empty;

    /// <summary>What the person wrote, kept so the goal can be understood later.</summary>
    public string Text { get; set; } = string.Empty;

    public GoalStatus Status { get; set; } = GoalStatus.Active;

    /// <summary>"ai" when a model split the text, "parser" when the text's own structure was used.</summary>
    public string PlanSource { get; set; } = "ai";

    /// <summary>What the person was shown before starting; kept to compare with what the goal really used.</summary>
    public long EstimatedInputTokens { get; set; }

    public long EstimatedOutputTokens { get; set; }

    public decimal? EstimatedUsd { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public ICollection<GoalTask> Tasks { get; set; } = new List<GoalTask>();
}
