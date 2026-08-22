using DevPilot.Application.DeveloperAgent.Models;

namespace DevPilot.Application.DeveloperAgent.Ports;

public interface IBoundedEditApplier
{
    /// <summary>
    /// Evaluates and applies ordered bounded edit operations against in-memory content.
    /// Later operations operate against the evolving in-memory buffer produced by earlier operations.
    /// </summary>
    BoundedEditResult ApplyOperationsToContent(
        string originalContent,
        IReadOnlyList<BoundedEditOperation>? operations,
        string filePath,
        BoundedEditLimits? limits = null);

    /// <summary>
    /// Validates all files, expected hashes, and operations in-memory across the execution worktree,
    /// checks authorization and path safety, and atomically applies all file edits with rollback safety.
    /// </summary>
    Task<BoundedEditPlanResult> ApplyBoundedEditsAsync(
        string workspacePath,
        string branchName,
        BoundedEditPlan editPlan,
        BoundedEditLimits? limits = null,
        IReadOnlyList<string>? authorizedRelativePaths = null,
        CancellationToken cancellationToken = default);
}
