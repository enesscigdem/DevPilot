using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Automation;

/// <summary>
/// When automation restarts a failed execution by itself. Model output varies from run to run, so a run that
/// died while the code was being written is worth one more try. A run that reached build or test already went
/// through the repair loop, and repeating it would only repeat the same failure at the same cost.
/// </summary>
public static class AutomationRetryRules
{
    /// <summary>Automatic restarts per task. Anything beyond this is left for a person, so cost can never loop.</summary>
    public const int MaxAutomaticRetries = 1;

    /// <summary>Executions a task may have had before automation stops restarting it (the first run plus the retries).</summary>
    public const int MaxExecutionsPerTask = 1 + MaxAutomaticRetries;

    /// <summary>True when the run failed while generating code and never reached build or test verification.</summary>
    public static bool IsGenerationFailure(IReadOnlyCollection<ExecutionActivity> activities) =>
        activities.Any(a => a.Stage == ExecutionStage.DeveloperAgent && a.Status == ExecutionActivityStatus.Failed) &&
        !activities.Any(a => a.Stage is ExecutionStage.Build or ExecutionStage.Test);

    public static bool HasRetryLeft(int executionsSoFar) => executionsSoFar < MaxExecutionsPerTask;

    /// <summary>True when a finished run has no usable change: verification failed or was unavailable, or the worktree is empty.</summary>
    public static bool IsRestartableOutcome(ExecutionVerificationOutcome outcome, int changedFileCount) =>
        changedFileCount == 0 ||
        outcome is ExecutionVerificationOutcome.NeedsReview
            or ExecutionVerificationOutcome.Failed
            or ExecutionVerificationOutcome.VerificationUnavailable;

    /// <summary>
    /// Restarts on the current base after a pull request turned out to conflict. Each restart is a full run, and other
    /// pull requests can land while it works, so it is capped: the third conflict is left for a person.
    /// </summary>
    public const int MaxConflictRestarts = 2;

    public static bool HasConflictRestartLeft(int executionsSoFar) => executionsSoFar < 1 + MaxConflictRestarts;
}
