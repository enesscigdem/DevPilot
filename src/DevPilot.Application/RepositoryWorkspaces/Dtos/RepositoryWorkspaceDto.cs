using DevPilot.Domain.Enums;

namespace DevPilot.Application.RepositoryWorkspaces.Dtos;

public sealed class RepositoryWorkspaceDto
{
    public Guid Id { get; set; }

    public string Provider { get; set; } = "GitHub";

    public string Host { get; set; } = "github.com";

    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public RepositoryWorkspaceStatus Status { get; set; }

    public string CommitSha { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
