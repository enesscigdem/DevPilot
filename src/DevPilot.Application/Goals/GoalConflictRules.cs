using DevPilot.Domain.Enums;

namespace DevPilot.Application.Goals;

/// <summary>
/// Decides which shared files really make two tasks wait for each other. The analysis of a task names files and
/// whether each is created, changed or deleted, but not which lines, so "same part of the file" cannot be known.
/// The balanced mode therefore waits only where a merge is likely to fail whatever the lines: files that are
/// rewritten wholesale (manifests, lock files, project files, migrations, generated code) and files a task
/// creates, deletes or restructures.
/// </summary>
public static class GoalConflictRules
{
    private static readonly HashSet<string> HotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "npm-shrinkwrap.json",
        "composer.json", "composer.lock", "gemfile", "gemfile.lock", "go.mod", "go.sum", "cargo.toml", "cargo.lock",
        "pyproject.toml", "poetry.lock", "requirements.txt", "pipfile", "pipfile.lock", "pom.xml",
        "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts",
        "directory.build.props", "directory.build.targets", "directory.packages.props", "global.json", "nuget.config",
        "dockerfile", "docker-compose.yml", "docker-compose.yaml", ".gitignore", ".gitattributes"
    };

    private static readonly string[] HotExtensions =
    {
        ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".lock", ".snap", ".designer.cs", ".g.cs", ".generated.cs"
    };

    private static readonly string[] HotFolders =
    {
        "/migrations/", "/__snapshots__/", "/generated/", "/.github/workflows/"
    };

    /// <summary>The shared files that make the two tasks wait, in the written form, at most three.</summary>
    public static IReadOnlyList<string> Clashes(
        ConflictMode mode,
        IReadOnlyList<string> first,
        IReadOnlyList<string> second,
        IReadOnlyCollection<string>? firstStructural = null,
        IReadOnlyCollection<string>? secondStructural = null)
    {
        if (mode == ConflictMode.Fast)
        {
            return Array.Empty<string>();
        }

        var shared = GoalWavePlanner.SharedFiles(first, second);
        if (mode == ConflictMode.Careful)
        {
            return shared.Take(3).ToList();
        }

        var structural = new HashSet<string>(
            (firstStructural ?? Array.Empty<string>()).Concat(secondStructural ?? Array.Empty<string>()).Select(GoalWavePlanner.Key),
            StringComparer.Ordinal);
        return shared.Where(file => IsHot(file) || structural.Contains(GoalWavePlanner.Key(file))).Take(3).ToList();
    }

    private static readonly HashSet<string> ExtensionlessFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "dockerfile", "makefile", "gemfile", "pipfile", "procfile", "jenkinsfile", "rakefile", "vagrantfile", "license"
    };

    /// <summary>Well-known files without an extension; without this a folder and a file called "Dockerfile" look alike.</summary>
    public static bool IsExtensionlessFile(string name) => ExtensionlessFiles.Contains(name);

    public static bool IsHot(string path)
    {
        var normalized = "/" + path.Replace('\\', '/').Trim().TrimStart('/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (HotNames.Contains(name))
        {
            return true;
        }

        var lower = name.ToLowerInvariant();
        if (HotExtensions.Any(lower.EndsWith)
            || (lower.StartsWith("tsconfig") && lower.EndsWith(".json"))
            || (lower.StartsWith("appsettings") && lower.EndsWith(".json"))
            || lower.StartsWith("vite.config.") || lower.StartsWith("webpack.config.") || lower.StartsWith("next.config."))
        {
            return true;
        }

        var folderPath = normalized.ToLowerInvariant();
        return HotFolders.Any(folderPath.Contains);
    }
}
