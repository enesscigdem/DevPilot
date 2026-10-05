using DevPilot.Domain.Entities;

namespace DevPilot.Application.Automation;

public interface IAutomationPolicyStore
{
    Task<AutomationPolicy?> GetAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the policy of a workspace; null when the workspace does not exist.</summary>
    Task<AutomationPolicy?> SaveAsync(AutomationPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>Policies automation currently acts on: an automatic level and not paused.</summary>
    Task<IReadOnlyList<AutomationPolicy>> ListActiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Finds the work automation may move forward. Only work created after the policy became active is returned.</summary>
public interface IAutomationWorkReader
{
    /// <summary>
    /// Tasks waiting for plan approval, plus approved tasks that have never been executed (a cancelled run returns
    /// its task to Approved, and restarting that must stay a human decision). Highest priority first.
    /// </summary>
    Task<IReadOnlyList<DevelopmentTask>> GetTasksReadyToStartAsync(
        Guid repositoryWorkspaceId,
        DateTime since,
        CancellationToken cancellationToken = default);

    Task<int> CountActiveExecutionsAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default);

    /// <summary>Finished executions whose pull request has not been opened yet and whose review was not rejected.</summary>
    Task<IReadOnlyList<Guid>> GetDeliverableExecutionIdsAsync(
        Guid repositoryWorkspaceId,
        DateTime since,
        CancellationToken cancellationToken = default);

    /// <summary>Executions with an open pull request that has not been merged.</summary>
    Task<IReadOnlyList<Guid>> GetOpenPullRequestExecutionIdsAsync(
        Guid repositoryWorkspaceId,
        DateTime since,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Remembers the last explained decision per execution so that a blocked execution is explained once in its
/// activity log instead of on every poll. In memory on purpose: after a restart one repeated note is harmless.
/// </summary>
public sealed class AutomationDecisionLedger
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _last = new();

    /// <summary>True when <paramref name="key"/> differs from the last one recorded for the execution.</summary>
    public bool ShouldRecord(Guid executionId, string key)
    {
        var changed = !_last.TryGetValue(executionId, out var previous) || !string.Equals(previous, key, StringComparison.Ordinal);
        if (changed)
        {
            _last[executionId] = key;
        }

        return changed;
    }
}
