using System.Collections.Concurrent;

namespace DevPilot.Application.Executions.Services;

/// <summary>
/// Remembers, between queuing a resumed revision and the worker picking it up, that the reviewer's feedback is already in
/// the worktree and only build/test/repair must run again. If the process restarts in between, the marker is gone and the
/// job behaves like an ordinary requested fix.
/// </summary>
public static class RevisionResumeMarkers
{
    private static readonly ConcurrentDictionary<Guid, bool> Marked = new();

    public static void Mark(Guid executionId) => Marked[executionId] = true;

    public static void Clear(Guid executionId) => Marked.TryRemove(executionId, out _);

    public static bool Consume(Guid executionId) => Marked.TryRemove(executionId, out _);
}
