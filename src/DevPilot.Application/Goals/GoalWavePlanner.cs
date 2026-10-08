using DevPilot.Domain.Enums;

namespace DevPilot.Application.Goals;

/// <summary>
/// Orders tasks so that tasks expected to change the same code never run together, while everything else runs in
/// parallel. The order is for avoiding merge conflicts, not for correctness: a task that fails does not hold
/// back the ones after it. Only an explicit "depends on" keeps a task behind another.
/// </summary>
public static class GoalWavePlanner
{
    public static (IReadOnlyList<GoalWave> Waves, IReadOnlyList<GoalConflict> Conflicts) Plan(
        IReadOnlyList<GoalTaskPlan> tasks,
        ConflictMode mode = ConflictMode.Careful)
    {
        var index = tasks.Select((task, i) => (task.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        var wave = new int[tasks.Count];
        var conflicts = new List<GoalConflict>();

        for (var i = 0; i < tasks.Count; i++)
        {
            var earliest = 0;
            foreach (var dependency in tasks[i].DependsOn)
            {
                // Only earlier tasks count, so a cycle can never form.
                if (index.TryGetValue(dependency, out var d) && d < i)
                {
                    earliest = Math.Max(earliest, wave[d] + 1);
                }
            }

            var chosen = earliest;
            var clashes = new List<(int Other, IReadOnlyList<string> Shared)>();
            for (var j = 0; j < i; j++)
            {
                // A guess from a code search is too vague to keep a task waiting: in a small repository every search
                // finds the same few files. Real clashes are caught later, from the real plan, before a task starts.
                var shared = tasks[i].AreasGuessed || tasks[j].AreasGuessed
                    ? Array.Empty<string>()
                    : GoalConflictRules.Clashes(mode, tasks[i].Areas, tasks[j].Areas);
                if (shared.Count > 0)
                {
                    clashes.Add((j, shared));
                    conflicts.Add(new GoalConflict(tasks[j].Key, tasks[i].Key, shared));
                }
            }

            while (clashes.Any(c => wave[c.Other] == chosen))
            {
                chosen++;
            }

            wave[i] = chosen;
        }

        var waves = tasks
            .Select((task, i) => (task.Key, Wave: wave[i]))
            .GroupBy(x => x.Wave)
            .OrderBy(g => g.Key)
            .Select((g, n) => new GoalWave(n + 1, g.Select(x => x.Key).ToList()))
            .ToList();
        return (waves, conflicts);
    }

    /// <summary>The same files named by both lists, at most three (enough to show a person why two tasks wait).</summary>
    public static IReadOnlyList<string> SharedAreas(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
        SharedFiles(first, second).Take(3).ToList();

    /// <summary>Every file named by both lists, as it was written.</summary>
    public static IReadOnlyList<string> SharedFiles(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        var shared = new List<string>();
        foreach (var a in first.Select(Clean).Where(x => x.Length > 0))
        {
            foreach (var b in second.Select(Clean).Where(x => x.Length > 0))
            {
                var (x, y) = (a.ToLowerInvariant(), b.ToLowerInvariant());
                // Only the same file is a clash. A folder is too coarse: "src/components" would hold back every task
                // that touches anything in it, and git merges changes to different parts of a file by itself.
                if (x == y && IsFile(x))
                {
                    // The more specific path, as it was written, because a person reads this.
                    shared.Add(a.Length >= b.Length ? a : b);
                }
            }
        }

        return shared.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Comparison form of a path: separators, leading dot and case do not matter.</summary>
    public static string Key(string path) => Clean(path).ToLowerInvariant();

    private static string Clean(string path)
    {
        var cleaned = path.Replace('\\', '/').Trim();
        // Only a leading "./" is noise; a leading dot is part of a name (".github").
        while (cleaned.StartsWith("./", StringComparison.Ordinal))
        {
            cleaned = cleaned[2..];
        }

        return cleaned.TrimStart('/');
    }

    /// <summary>A path with an extension, or one of the well-known files that have none (Dockerfile, Makefile, ...).</summary>
    private static bool IsFile(string path)
    {
        if (path.EndsWith('/'))
        {
            return false;
        }

        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.Contains('.') || GoalConflictRules.IsExtensionlessFile(name);
    }
}
