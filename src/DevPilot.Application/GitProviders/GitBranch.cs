namespace DevPilot.Application.GitProviders;

public sealed class GitBranch
{
    public string Name { get; set; } = string.Empty;

    public string? CommitSha { get; set; }

    public bool IsProtected { get; set; }
}
