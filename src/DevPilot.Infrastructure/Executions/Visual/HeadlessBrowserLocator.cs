namespace DevPilot.Infrastructure.Executions.Visual;

/// <summary>Finds an installed Chrome, Chromium or Edge. Nothing is downloaded.</summary>
public static class HeadlessBrowserLocator
{
    public const string OverrideEnvironmentVariable = "DEVPILOT_BROWSER";

    public static string? Find()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        foreach (var candidate in KnownPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindOnPath(new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge" });
    }

    private static IEnumerable<string> KnownPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            yield return Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
        }
    }

    private static string? FindOnPath(IEnumerable<string> names)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in names)
        {
            foreach (var directory in directories)
            {
                var full = Path.Combine(directory, name);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }
}
