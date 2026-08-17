namespace DevPilot.Application.RepositoryWorkspaces.Dtos;

public sealed class CreateRepositoryWorkspaceDto
{
    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;
}
