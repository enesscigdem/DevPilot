namespace DevPilot.Application.RepositoryClone;

public sealed class CloneRequest
{
    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;
}
