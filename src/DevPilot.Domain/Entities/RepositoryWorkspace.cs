using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

public class RepositoryWorkspace
{
    public Guid Id { get; set; }

    public Guid? GitHubInstallationConnectionId { get; set; }

    public GitHubInstallationConnection? GitHubInstallationConnection { get; set; }

    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    public bool IsPrivate { get; set; }

    public string? RemoteUrl { get; set; }

    public RepositoryWorkspaceStatus Status { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
