using DevPilot.Domain.Enums;

namespace DevPilot.Application.GitProviders;

public sealed record GitConnectionDto(
    Guid Id,
    GitProviderKind Provider,
    string Host,
    string DisplayName,
    string? Username,
    DateTime CreatedAt);

/// <summary>Stored access tokens for GitLab and generic git hosts. Tokens never leave the store.</summary>
public interface IGitConnectionStore
{
    Task<IReadOnlyList<GitConnectionDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<GitConnectionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<GitConnectionDto> CreateAsync(
        GitProviderKind provider,
        string host,
        string displayName,
        string? username,
        string token,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
