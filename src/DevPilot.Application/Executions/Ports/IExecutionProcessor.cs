namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Performs the actual developer-agent work for a single execution.
/// In the MVP this is a no-op placeholder.  Future iterations will
/// implement real AI-driven code modifications here.
/// </summary>
/// <remarks>
/// Implementations MUST NOT modify repository source files, create
/// branches, commit, push, or call any AI provider until the full
/// Developer Agent is wired in.
/// </remarks>
public interface IExecutionProcessor
{
    /// <summary>
    /// Executes the work for the given execution context.
    /// Throw an exception to signal failure; return normally to signal success.
    /// </summary>
    Task ProcessAsync(
        ExecutionProcessingContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable context passed to <see cref="IExecutionProcessor"/> containing
/// all data loaded by the orchestrator before processing begins.
/// </summary>
public sealed record ExecutionProcessingContext(
    Guid ExecutionId,
    Guid TaskId,
    string TaskTitle,
    string TaskDescription,
    string? AcceptanceCriteria,
    Guid WorkspaceId,
    string WorkspaceLocalPath,
    string ImpactAnalysisSummary,
    string? RepositoryOwner = null,
    string? RepositoryName = null,
    string? BaseBranch = null,
    /// <summary>
    /// When set, the already generated worktree is verified again instead of generating code: no new
    /// workspace is prepared and the Developer Agent is not asked for edits. Bounded repair still runs.
    /// </summary>
    ExecutionVerifyOnlyWorkspace? VerifyOnlyWorkspace = null,
    /// <summary>
    /// When set together with <see cref="VerifyOnlyWorkspace"/>, the reviewer's feedback is applied to the existing
    /// worktree by the Developer Agent first, then build and test run again on the result.
    /// </summary>
    ExecutionChangeRequest? ChangeRequest = null)
{
    public bool IsVerifyOnly => VerifyOnlyWorkspace is not null;

    public bool IsRevision => ChangeRequest is not null && VerifyOnlyWorkspace is not null;
}

/// <summary>Reviewer feedback to apply on top of what the execution already produced.</summary>
public sealed record ExecutionChangeRequest(
    string Feedback,
    int RevisionNumber,
    string? CommittedBaseCommitSha = null,
    bool FeedbackAlreadyApplied = false);

/// <summary>The existing worktree of a finished execution, re-verified without regenerating code.</summary>
public sealed record ExecutionVerifyOnlyWorkspace(
    string WorkspacePath,
    string BranchName,
    string? BaseCommitSha = null);
