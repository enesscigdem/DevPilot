namespace DevPilot.Application.Trackers;

/// <summary>
/// Which tracker instance an issue key belongs to. Two Jira sites can both have an issue called APP-42, and a connection can be
/// removed and added again, so neither the key nor the connection id says which issue is meant; the address of the site does.
/// </summary>
public static class TrackerOrigin
{
    public const int MaxLength = 300;

    /// <summary>The stable form of a base URL. Must stay in step with the SQL that backfilled existing tasks.</summary>
    public static string Normalize(string? baseUrl) =>
        (baseUrl ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
}
