using System.Diagnostics;
using System.Text;

namespace DevPilot.Infrastructure.Executions;

public static class GitAuthenticationHelper
{
    public static string CreateTransientHomeDirectory(string token)
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"devpilot-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        var configContent = $"[http]{Environment.NewLine}    extraHeader = \"Authorization: Basic {credentials}\"{Environment.NewLine}";

        File.WriteAllText(Path.Combine(tempHome, ".gitconfig"), configContent);

        return tempHome;
    }

    public static void ApplyEnvironment(ProcessStartInfo psi, string? tempHomeDirectory)
    {
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GIT_CONFIG_NOSYSTEM"] = "1";

        if (!string.IsNullOrWhiteSpace(tempHomeDirectory))
        {
            psi.EnvironmentVariables["HOME"] = tempHomeDirectory;
            psi.EnvironmentVariables["USERPROFILE"] = tempHomeDirectory;
        }
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
