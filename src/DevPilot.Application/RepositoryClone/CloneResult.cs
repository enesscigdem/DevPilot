using DevPilot.Domain.Enums;

namespace DevPilot.Application.RepositoryClone;

public sealed class CloneResult
{
    public Guid WorkspaceId { get; set; }

    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    public RepositoryWorkspaceStatus Status { get; set; }

    public bool Success { get; set; }

    public bool IsConflict { get; set; }

    public bool IsValidationError { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
