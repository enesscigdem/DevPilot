using DevPilot.Domain.Enums;

namespace DevPilot.Application.RepositoryClone;

public sealed class CloneRequest
{
    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public GitProviderKind Provider { get; set; } = GitProviderKind.GitHub;

    public string Host { get; set; } = "github.com";

    /// <summary>Stored token to clone with. Only used for non-GitHub providers.</summary>
    public Guid? GitConnectionId { get; set; }
}
