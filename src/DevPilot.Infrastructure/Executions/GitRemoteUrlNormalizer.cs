using System.Text.RegularExpressions;

namespace DevPilot.Infrastructure.Executions;

public static class GitRemoteUrlNormalizer
{
    private static readonly Regex UrlCredentialSanitizer = new(
        @"https?://([^@/]+)@github\.com",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string Owner, string Repo)? ParseGitHubUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var trimmed = remoteUrl.Trim();

        // 1. Handle HTTP / HTTPS URLs
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length == 2)
                    {
                        var owner = segments[0];
                        var repo = segments[1];
                        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                        {
                            repo = repo[..^4];
                        }
                        return (owner, repo);
                    }
                }
            }
            return null;
        }

        // 2. Handle SSH URLs (git@github.com:owner/repo.git or ssh://git@github.com/owner/repo.git)
        var sshUrl = trimmed;
        if (sshUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            sshUrl = sshUrl[6..];
        }

        if (sshUrl.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase) ||
            sshUrl.StartsWith("git@github.com/", StringComparison.OrdinalIgnoreCase))
        {
            var prefixLen = sshUrl.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase) ? 15 : 15;
            var path = sshUrl[prefixLen..];
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 2)
            {
                var owner = segments[0];
                var repo = segments[1];
                if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    repo = repo[..^4];
                }
                return (owner, repo);
            }
        }

        return null;
    }

    public static bool MatchesRepository(string? remoteUrl, string expectedOwner, string expectedRepo)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || string.IsNullOrWhiteSpace(expectedOwner) || string.IsNullOrWhiteSpace(expectedRepo))
        {
            return false;
        }

        var trimmed = remoteUrl.Trim();

        // Support local bare repository paths for automated tests
        if (Path.IsPathRooted(trimmed) || Directory.Exists(trimmed) || trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parsed = ParseGitHubUrl(trimmed);
        if (parsed is null)
        {
            return false;
        }

        return string.Equals(parsed.Value.Owner, expectedOwner, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(parsed.Value.Repo, expectedRepo, StringComparison.OrdinalIgnoreCase);
    }

    public static string SanitizeOutput(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return UrlCredentialSanitizer.Replace(input, "https://***@github.com");
    }
}
