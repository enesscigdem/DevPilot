using System.Collections.Concurrent;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

/// <summary>Loads the fix history of an execution and the diff of each finished fix, then builds their views.</summary>
public static class ExecutionRevisionProjector
{
    private const int MaxCachedDiffs = 64;

    // A pair of snapshot commits never changes, so its diff is read once. Polling a running fix re-reads the older ones.
    private static readonly ConcurrentDictionary<string, ExecutionGitDiffResult> DiffCache = new();

    public static async Task<IReadOnlyList<ExecutionRevisionDto>> BuildAsync(
        TaskExecution execution,
        IExecutionRevisionStore? store,
        IReadOnlyList<ExecutionActivity> activities,
        ExecutionVerificationOutcome overallOutcome,
        IExecutionGitDiffReader? diffReader,
        CancellationToken cancellationToken)
    {
        if (!ExecutionRevisionScope.HasRevision(execution))
        {
            return Array.Empty<ExecutionRevisionDto>();
        }

        var rows = store is null
            ? Array.Empty<ExecutionRevision>()
            : await store.ListRevisionsAsync(execution.Id, cancellationToken).ConfigureAwait(false);

        var pairs = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.BaseSnapshotSha) && !string.IsNullOrWhiteSpace(r.ResultSnapshotSha))
            .Select(r => (r.BaseSnapshotSha!, r.ResultSnapshotSha!))
            .ToList();
        if (!string.IsNullOrWhiteSpace(execution.RevisionBaseSnapshotSha) && !string.IsNullOrWhiteSpace(execution.RevisionResultSnapshotSha))
        {
            pairs.Add((execution.RevisionBaseSnapshotSha!, execution.RevisionResultSnapshotSha!));
        }

        var diffs = new Dictionary<string, ExecutionGitDiffResult>();
        if (diffReader != null && !string.IsNullOrWhiteSpace(execution.WorkspacePath))
        {
            foreach (var (baseSha, resultSha) in pairs.Distinct())
            {
                var key = $"{execution.WorkspacePath}|{baseSha}|{resultSha}";
                if (!DiffCache.TryGetValue(key, out var diff))
                {
                    diff = await diffReader
                        .ReadCommittedDiffAsync(execution.WorkspacePath, baseSha, resultSha, cancellationToken)
                        .ConfigureAwait(false);
                    if (diff.Success)
                    {
                        if (DiffCache.Count >= MaxCachedDiffs)
                        {
                            DiffCache.Clear();
                        }

                        DiffCache[key] = diff;
                    }
                }

                diffs[$"{baseSha}|{resultSha}"] = diff;
            }
        }

        return ExecutionRevisionBuilder.BuildAll(
            execution,
            rows,
            activities,
            overallOutcome,
            record => !string.IsNullOrWhiteSpace(record.BaseSnapshotSha) && !string.IsNullOrWhiteSpace(record.ResultSnapshotSha)
                ? diffs.GetValueOrDefault($"{record.BaseSnapshotSha}|{record.ResultSnapshotSha}")
                : null);
    }
}
