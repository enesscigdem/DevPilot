namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Paths that are never part of an execution's change set: dependency installs and build output that
/// verification checks create inside the worktree. A repository without a .gitignore would otherwise
/// report tens of thousands of untracked files after one <c>npm install</c>.
/// </summary>
internal static class WorkspaceChangeScope
{
    public static readonly IReadOnlyList<string> ExcludedPathspecs = new[]
    {
        ":(exclude,glob)**/node_modules/**",
        ":(exclude,glob)**/.pnpm-store/**",
        ":(exclude,glob)**/.dotnet_home/**",
        ":(exclude,glob)**/*.tsbuildinfo",
    };

    /// <summary>Pathspec arguments (after <c>--</c>) selecting the whole worktree minus the excluded paths.</summary>
    public static string[] WorktreePathspecs() => new[] { "." }.Concat(ExcludedPathspecs).ToArray();
}
