using DevPilot.Domain.Enums;

namespace DevPilot.Domain.Entities;

/// <summary>Access to an issue tracker (Jira) that tasks are imported from and progress is reported back to.</summary>
public class TrackerConnection
{
    public Guid Id { get; set; }

    public TrackerProviderKind Provider { get; set; }

    /// <summary>Site address without a trailing slash, for example https://acme.atlassian.net.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Account e-mail for Jira Cloud (basic auth with an API token). Empty means a bearer personal access token.</summary>
    public string? Email { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Token encrypted with the data protection key ring. Never returned by the API.</summary>
    public string EncryptedToken { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
