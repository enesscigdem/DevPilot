namespace DevPilot.Application.Executions.Ports;

/// <summary>
/// Tells the git host layer which repository workspace the current call is for. The pull request and credential calls only
/// carry an owner and a repository name, and the same pair can be connected on more than one host; the workspace is what
/// says which host is meant. Callers set it right before they talk to the host.
/// </summary>
public interface IRepositoryTargetContext
{
    Guid? WorkspaceId { get; }

    void Use(Guid workspaceId);
}

/// <summary>One value per scope (a request or a background job).</summary>
public sealed class RepositoryTargetContext : IRepositoryTargetContext
{
    public Guid? WorkspaceId { get; private set; }

    public void Use(Guid workspaceId) => WorkspaceId = workspaceId;
}
