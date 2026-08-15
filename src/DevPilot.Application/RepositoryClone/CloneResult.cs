namespace DevPilot.Application.RepositoryClone;

public sealed class CloneResult
{
    public string LocalPath { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? Error { get; set; }
}
