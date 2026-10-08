using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

/// <summary>A stored access token for a non-GitHub-App git host (GitLab or any HTTPS remote).</summary>
public class GitConnection
{
    public Guid Id { get; set; }

    public GitProviderKind Provider { get; set; }

    /// <summary>Host name only, lower-cased, for example gitlab.com or git.company.local.</summary>
    public string Host { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>User name sent with the token over HTTP basic auth. Empty means the provider default.</summary>
    public string? Username { get; set; }

    /// <summary>Token encrypted with the data protection key ring. Never returned by the API.</summary>
    public string EncryptedToken { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<RepositoryWorkspace> Workspaces { get; set; } = new List<RepositoryWorkspace>();
}
