using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

public class GitHubInstallationConnection
{
    public Guid Id { get; set; }

    public long ExternalInstallationId { get; set; }

    public string AccountLogin { get; set; } = string.Empty;

    public string AccountType { get; set; } = string.Empty;

    public long TargetId { get; set; }

    public string? TargetAvatarUrl { get; set; }

    public GitHubInstallationStatus Status { get; set; } = GitHubInstallationStatus.Active;

    public DateTime ConnectedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? LastVerifiedAt { get; set; }

    public ICollection<RepositoryWorkspace> Workspaces { get; set; } = new List<RepositoryWorkspace>();
}
