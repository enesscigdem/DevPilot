using DevPilot.Domain.Enums;

namespace DevPilot.Application.Tasks.Commands.UpdateTaskStatus;

/// <summary>
/// What a person may set by hand. Approval, analysis, execution and completion belong to their own commands and to the
/// execution pipeline; letting the status be set freely would skip the plan approval and fake a delivery.
/// </summary>
public static class ManualStatusChangePolicy
{
    private static readonly DevelopmentTaskStatus[] SettableTargets =
    {
        DevelopmentTaskStatus.Draft,
        DevelopmentTaskStatus.ReadyForAnalysis,
        DevelopmentTaskStatus.Rejected,
    };

    /// <summary>Work that is being analysed or run, or that already ended in a delivery, is not changed by hand.</summary>
    private static readonly DevelopmentTaskStatus[] LockedSources =
    {
        DevelopmentTaskStatus.Analyzing,
        DevelopmentTaskStatus.Executing,
        DevelopmentTaskStatus.Completed,
    };

    public static bool IsAllowed(DevelopmentTaskStatus from, DevelopmentTaskStatus to) =>
        from != to && SettableTargets.Contains(to) && !LockedSources.Contains(from);
}
