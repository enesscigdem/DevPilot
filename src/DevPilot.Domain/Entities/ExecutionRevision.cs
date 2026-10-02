namespace DevPilot.Domain.Entities;

/// <summary>
/// One requested fix ("Request changes") on an execution. Every fix keeps its own row so earlier ones can still be
/// opened later with their feedback, result and worktree snapshots.
/// </summary>
public class ExecutionRevision
{
    public Guid Id { get; set; }

    public Guid ExecutionId { get; set; }

    /// <summary>1 for the first requested fix, 2 for the next, and so on.</summary>
    public int Number { get; set; }

    public string Feedback { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Short outcome ("Applied: …", "No change: …", "Failed: …"); null while it is running.</summary>
    public string? Result { get; set; }

    public string? BaseSnapshotSha { get; set; }

    public string? ResultSnapshotSha { get; set; }
}
