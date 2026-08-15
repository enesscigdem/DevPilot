namespace DevPilot.Infrastructure.RepositoryClone;

public sealed class RepositoryCloneOptions
{
    public const string SectionName = "RepositoryClone";

    public string WorkspaceRoot { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    public string? Token { get; set; }
}
