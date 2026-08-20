namespace DevPilot.Application.GitProviders;

public sealed record GitHubConnectionStatusDto(
    bool IsConfigured,
    bool IsConnected,
    IReadOnlyList<GitHubInstallationSummaryDto> Installations);

public sealed record GitHubInstallationSummaryDto(
    Guid Id,
    long ExternalInstallationId,
    string AccountLogin,
    string AccountType,
    string? TargetAvatarUrl,
    string Status,
    DateTime ConnectedAt,
    string ManageUrl);

public sealed record GitHubConnectUrlResponseDto(string Url);

public sealed record GitHubDiscoveredRepositoryDto(
    long Id,
    string FullName,
    string Name,
    string Owner,
    bool IsPrivate,
    string DefaultBranch,
    string Url,
    string? Description,
    long ExternalInstallationId,
    bool IsConnectedToDevPilot,
    Guid? DevPilotWorkspaceId);
