namespace DevPilot.Application.Executions.Models;

/// <summary>One screenshot pair (or a single shot when no baseline could be built) of a page at a viewport size.</summary>
public sealed class VisualShot
{
    public string Page { get; set; } = "/";

    /// <summary>"desktop" or "mobile".</summary>
    public string Viewport { get; set; } = "desktop";

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>File name of the screenshot at the base commit; null when no baseline could be built.</summary>
    public string? BeforeFile { get; set; }

    /// <summary>File name of the screenshot with the changes of the execution.</summary>
    public string? AfterFile { get; set; }
}

/// <summary>
/// Outcome of the visual check of an execution. It is evidence for the reviewer and never fails an execution.
/// </summary>
public sealed class VisualCaptureManifest
{
    /// <summary>Captured, Partial (no baseline or some shots missing), Skipped (not applicable) or Failed.</summary>
    public string Status { get; set; } = "Skipped";

    public string? Reason { get; set; }

    /// <summary>True when UI files changed, so a person has to look at the result before approving.</summary>
    public bool RequiresReview { get; set; }

    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

    public string? BaseCommitSha { get; set; }

    public List<VisualShot> Shots { get; set; } = new();
}

public sealed record VisualCaptureRequest(
    Guid ExecutionId,
    string WorkspacePath,
    string? BaseCommitSha,
    IReadOnlyList<string> ChangedFiles);
