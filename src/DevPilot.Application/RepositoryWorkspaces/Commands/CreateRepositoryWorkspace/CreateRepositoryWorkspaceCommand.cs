using DevPilot.Application.RepositoryWorkspaces.Dtos;

namespace DevPilot.Application.RepositoryWorkspaces.Commands.CreateRepositoryWorkspace;

public sealed record CreateRepositoryWorkspaceCommand(CreateRepositoryWorkspaceDto Dto);

public sealed class CreateRepositoryWorkspaceResult
{
    public bool Success { get; set; }

    public bool IsValidationError { get; set; }

    public bool IsConflict { get; set; }

    public string? ErrorMessage { get; set; }

    public RepositoryWorkspaceDto? Workspace { get; set; }
}
