namespace DevPilot.Domain.Entities;

/// <summary>Links a goal to one of its development tasks and records what the planner knew about it.</summary>
public class GoalTask
{
    public Guid Id { get; set; }

    public Guid GoalId { get; set; }

    public Goal Goal { get; set; } = null!;

    public Guid DevelopmentTaskId { get; set; }

    public DevelopmentTask DevelopmentTask { get; set; } = null!;

    /// <summary>Stable name inside the goal ("t1", "t2", ...); the dependency fields refer to it.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Order the planner listed the tasks in; earlier tasks win when two tasks would clash.</summary>
    public int Position { get; set; }

    /// <summary>Display group of tasks expected to run together.</summary>
    public int Wave { get; set; }

    /// <summary>small, medium or large.</summary>
    public string Size { get; set; } = "medium";

    /// <summary>Newline-separated paths the planner expects this task to change.</summary>
    public string Areas { get; set; } = string.Empty;

    /// <summary>Comma-separated keys of tasks this one needs the result of.</summary>
    public string DependsOn { get; set; } = string.Empty;

    /// <summary>Comma-separated keys of earlier tasks that must be settled first (dependencies plus expected clashes).</summary>
    public string BlockedBy { get; set; } = string.Empty;

    public int AnalysisAttempts { get; set; }

    public DateTime? AnalysisRequestedAt { get; set; }

    /// <summary>Why the task is waiting or needs a person, in plain words; cleared when it moves on.</summary>
    public string? Note { get; set; }
}
