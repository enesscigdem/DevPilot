namespace DevPilot.Application.Executions.Models;

/// <summary>
/// Execution validation request specifying the workspace, branch, optional target path, and optional timeout.
/// </summary>
public sealed record ExecutionValidationRequest(
    string WorkspacePath,
    string BranchName,
    string? TargetPath = null,
    TimeSpan? Timeout = null);
