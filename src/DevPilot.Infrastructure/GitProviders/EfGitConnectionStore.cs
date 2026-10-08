using DevPilot.Application.GitProviders;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.GitProviders;

public sealed class EfGitConnectionStore : IGitConnectionStore
{
    private readonly DevPilotDbContext _dbContext;
    private readonly IGitSecretProtector _protector;

    public EfGitConnectionStore(DevPilotDbContext dbContext, IGitSecretProtector protector)
    {
        _dbContext = dbContext;
        _protector = protector;
    }

    public async Task<IReadOnlyList<GitConnectionDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.GitConnections
            .AsNoTracking()
            .OrderBy(c => c.DisplayName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDto).ToList();
    }

    public async Task<GitConnectionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.GitConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToDto(row);
    }

    public async Task<GitConnectionDto> CreateAsync(
        GitProviderKind provider,
        string host,
        string displayName,
        string? username,
        string token,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var entity = new GitConnection
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            Host = host.Trim().ToLowerInvariant(),
            DisplayName = displayName.Trim(),
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            EncryptedToken = _protector.Protect(token.Trim()),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _dbContext.GitConnections.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.GitConnections
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        _dbContext.GitConnections.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static GitConnectionDto ToDto(GitConnection c) =>
        new(c.Id, c.Provider, c.Host, c.DisplayName, c.Username, c.CreatedAt);
}
