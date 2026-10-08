using DevPilot.Application.Trackers;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Trackers;

/// <summary>Encrypts tracker tokens at rest with a purpose of their own, so a leaked git token cannot open a tracker token.</summary>
public interface ITrackerSecretProtector
{
    string Protect(string plainText);

    /// <summary>Null when the value cannot be decrypted (for example the key ring was lost).</summary>
    string? Unprotect(string protectedText);
}

internal sealed class TrackerSecretProtector : ITrackerSecretProtector
{
    private readonly IDataProtector _protector;

    public TrackerSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("DevPilot.TrackerToken.v1");
    }

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string? Unprotect(string protectedText)
    {
        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}

public sealed class EfTrackerConnectionStore : ITrackerConnectionStore
{
    private readonly DevPilotDbContext _db;
    private readonly ITrackerSecretProtector _protector;

    public EfTrackerConnectionStore(DevPilotDbContext db, ITrackerSecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<IReadOnlyList<TrackerConnectionDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.TrackerConnections.AsNoTracking().OrderBy(c => c.DisplayName).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    public async Task<TrackerConnectionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.TrackerConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToDto(row);
    }

    public async Task<TrackerConnectionInfo?> GetInfoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.TrackerConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var token = _protector.Unprotect(row.EncryptedToken);
        return string.IsNullOrWhiteSpace(token)
            ? null
            : new TrackerConnectionInfo(row.Id, row.Provider, row.BaseUrl, row.Email, token);
    }

    public async Task<TrackerConnectionDto> CreateAsync(
        TrackerProviderKind provider,
        string baseUrl,
        string displayName,
        string? email,
        string token,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var entity = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            BaseUrl = baseUrl.Trim().TrimEnd('/'),
            DisplayName = displayName.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            EncryptedToken = _protector.Protect(token.Trim()),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.TrackerConnections.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.TrackerConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        // Imported tasks keep their key and link; they only stop reporting back.
        var linked = await _db.DevelopmentTasks.Where(t => t.ExternalConnectionId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var task in linked)
        {
            task.ExternalConnectionId = null;
        }

        _db.TrackerConnections.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static TrackerConnectionDto ToDto(TrackerConnection c) =>
        new(c.Id, c.Provider, c.BaseUrl, c.DisplayName, c.Email, c.CreatedAt);
}

/// <summary>Posts a comment on the issue a task came from. Best effort: delivery must never fail because Jira is down.</summary>
public sealed class TaskExternalNotifier : ITaskExternalNotifier
{
    private readonly DevPilotDbContext _db;
    private readonly ITrackerConnectionStore _connections;
    private readonly ITrackerClient _client;
    private readonly ILogger<TaskExternalNotifier> _logger;

    public TaskExternalNotifier(
        DevPilotDbContext db,
        ITrackerConnectionStore connections,
        ITrackerClient client,
        ILogger<TaskExternalNotifier> logger)
    {
        _db = db;
        _connections = connections;
        _client = client;
        _logger = logger;
    }

    public async Task NotifyAsync(Guid taskId, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await _db.DevelopmentTasks
                .AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => new { t.ExternalSource, t.ExternalKey, t.ExternalConnectionId })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (task?.ExternalKey is null || task.ExternalConnectionId is null)
            {
                return;
            }

            var connection = await _connections.GetInfoAsync(task.ExternalConnectionId.Value, cancellationToken).ConfigureAwait(false);
            if (connection is null)
            {
                return;
            }

            var result = await _client.AddCommentAsync(connection, task.ExternalKey, message, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Could not comment on {Key}: {Error}", task.ExternalKey, result.ErrorMessage);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not report progress of task {TaskId} to its issue.", taskId);
        }
    }
}
