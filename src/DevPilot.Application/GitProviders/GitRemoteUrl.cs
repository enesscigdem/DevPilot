using DevPilot.Domain.Enums;

namespace DevPilot.Application.GitProviders;

public sealed record ParsedGitRemote(string Host, string Owner, string Repository)
{
    public string FullPath => $"{Owner}/{Repository}";
}

/// <summary>
/// Parses http(s) and ssh remotes of any host. The owner keeps nested namespaces (GitLab subgroups). Azure DevOps
/// remotes are read into organization/project plus repository, whatever the address style, so the same repository
/// always produces the same identity.
/// </summary>
public static class GitRemoteUrl
{
    public const string AzureHost = "dev.azure.com";
    public const string BitbucketHost = "bitbucket.org";

    public static ParsedGitRemote? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var trimmed = remoteUrl.Trim();
        string host;
        string path;

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                return null;
            }

            host = uri.Host;
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            // scp-style: git@host:group/repo.git
            var at = trimmed.IndexOf('@');
            var colon = trimmed.IndexOf(':');
            if (at < 0 || colon < at)
            {
                return null;
            }

            host = trimmed[(at + 1)..colon];
            path = trimmed[(colon + 1)..];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments.Any(s => s is "." or ".."))
        {
            return null;
        }

        host = host.ToLowerInvariant();
        return IsAzureHost(host) ? ParseAzure(host, segments) : ParseGeneric(host, segments);
    }

    private static ParsedGitRemote? ParseGeneric(string host, string[] segments)
    {
        var repo = StripGitSuffix(segments[^1]);
        if (repo.Length == 0)
        {
            return null;
        }

        return new ParsedGitRemote(host, string.Join('/', segments[..^1]), repo);
    }

    /// <summary>
    /// https://dev.azure.com/org/project/_git/repo, https://org.visualstudio.com/[DefaultCollection/]project/_git/repo,
    /// https://dev.azure.com/org/_git/repo (the project is named like the repository) and
    /// git@ssh.dev.azure.com:v3/org/project/repo.
    /// </summary>
    private static ParsedGitRemote? ParseAzure(string host, string[] segments)
    {
        string organization;
        string project;
        string repo;

        var gitIndex = Array.IndexOf(segments, "_git");
        if (gitIndex >= 0)
        {
            if (gitIndex + 1 >= segments.Length)
            {
                return null;
            }

            repo = segments[gitIndex + 1];
            var before = segments[..gitIndex].ToList();
            if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
            {
                organization = host[..^".visualstudio.com".Length];
                if (before.Count > 0 && string.Equals(before[0], "DefaultCollection", StringComparison.OrdinalIgnoreCase))
                {
                    before.RemoveAt(0);
                }
            }
            else
            {
                if (before.Count == 0)
                {
                    return null;
                }

                organization = before[0];
                before.RemoveAt(0);
            }

            project = before.Count > 0 ? before[0] : repo;
        }
        else if (segments.Length >= 4 && string.Equals(segments[0], "v3", StringComparison.OrdinalIgnoreCase))
        {
            organization = segments[1];
            project = segments[2];
            repo = segments[3];
        }
        else
        {
            return null;
        }

        repo = StripGitSuffix(repo);
        if (organization.Length == 0 || project.Length == 0 || repo.Length == 0)
        {
            return null;
        }

        return new ParsedGitRemote(AzureHost, $"{organization}/{project}", repo);
    }

    public static bool IsAzureHost(string host) =>
        string.Equals(host, AzureHost, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "ssh." + AzureHost, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    public static GitProviderKind InferProvider(string host)
    {
        if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return GitProviderKind.GitHub;
        }

        if (IsAzureHost(host))
        {
            return GitProviderKind.AzureDevOps;
        }

        if (string.Equals(host, BitbucketHost, StringComparison.OrdinalIgnoreCase))
        {
            return GitProviderKind.Bitbucket;
        }

        return host.Contains("gitlab", StringComparison.OrdinalIgnoreCase) ? GitProviderKind.GitLab : GitProviderKind.Generic;
    }

    public static string BuildCloneUrl(string host, string owner, string repository) =>
        BuildCloneUrl(GitProviderKind.Generic, host, owner, repository);

    public static string BuildCloneUrl(GitProviderKind provider, string host, string owner, string repository)
    {
        var ownerPath = string.Join('/', owner.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var repo = Uri.EscapeDataString(repository);

        return provider switch
        {
            // Owner is organization/project; the address names the repository under _git and takes no .git suffix.
            GitProviderKind.AzureDevOps => $"https://{AzureHost}/{ownerPath}/_git/{repo}",
            _ => $"https://{host}/{ownerPath}/{repo}.git",
        };
    }

    /// <summary>Splits an Azure DevOps owner (organization/project) into its two parts.</summary>
    public static (string Organization, string Project)? SplitAzureOwner(string owner)
    {
        var parts = owner.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    private static string StripGitSuffix(string repo) =>
        repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;
}
