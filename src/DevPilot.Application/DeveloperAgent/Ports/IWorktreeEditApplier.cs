using DevPilot.Application.DeveloperAgent.Models;

namespace DevPilot.Application.DeveloperAgent.Ports;

public interface IWorktreeEditApplier
{
    /// <summary>
    /// Reads allowed context files from the workspace under strict path safety and size limits.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ReadContextFilesAsync(
        string workspacePath,
        string branchName,
        IReadOnlyList<string> filePaths,
        ContextLimits? limits = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates path safety, Git branch/workspace, exact search/replace matching,
    /// strict Create/Modify semantics, and applies edits atomically with rollback protection.
    /// </summary>
    Task<DeveloperAgentResult> ApplyEditsAsync(
        string workspacePath,
        string branchName,
        StructuredEditPlan editPlan,
        CancellationToken cancellationToken = default);
}
