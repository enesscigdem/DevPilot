namespace DevPilot.Application.GitProviders;

public sealed class GitRepository
{
    public string Name { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string Owner { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsPrivate { get; set; }

    public string DefaultBranch { get; set; } = string.Empty;

    public string? Url { get; set; }
}
