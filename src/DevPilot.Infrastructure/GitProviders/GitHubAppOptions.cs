namespace DevPilot.Infrastructure.GitProviders;

public sealed class GitHubAppOptions
{
    public const string SectionName = "GitProvider:GitHub";

    public string BaseUrl { get; set; } = "https://api.github.com";

    public string? AppId { get; set; }

    public string? AppSlug { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? PrivateKeyPem { get; set; }

    public string? PrivateKeyPath { get; set; }

    public bool EnableLegacyPatFallback { get; set; } = false;

    public string? FallbackToken { get; set; }

    public bool IsAppConfigured =>
        !string.IsNullOrWhiteSpace(AppId) &&
        (!string.IsNullOrWhiteSpace(PrivateKeyPem) ||
         (!string.IsNullOrWhiteSpace(PrivateKeyPath) && File.Exists(PrivateKeyPath)));

    public bool IsOAuthConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
