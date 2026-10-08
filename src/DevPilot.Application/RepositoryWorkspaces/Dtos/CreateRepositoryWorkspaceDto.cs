namespace DevPilot.Application.RepositoryWorkspaces.Dtos;

public sealed class CreateRepositoryWorkspaceDto
{
    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    /// <summary>
    /// Any https or ssh git remote. When set, owner and repository are read from it and the stored
    /// connection decides the provider. GitHub repositories keep using the GitHub App picker.
    /// </summary>
    public string? RemoteUrl { get; set; }

    public Guid? GitConnectionId { get; set; }
}
