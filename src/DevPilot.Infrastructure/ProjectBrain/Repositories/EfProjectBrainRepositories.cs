using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.ProjectBrain.Repositories;

public sealed class EfCodeChunkRepository : ICodeChunkRepository
{
    private readonly DevPilotDbContext _context;

    public EfCodeChunkRepository(DevPilotDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<string, CodeChunk>> GetExistingChunksAsync(
        Guid repositoryWorkspaceId,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        var paths = relativePaths.ToList();
        if (paths.Count == 0)
        {
            return new Dictionary<string, CodeChunk>();
        }

        var chunks = await _context.CodeChunks
            .AsNoTracking()
            .Where(c => c.RepositoryWorkspaceId == repositoryWorkspaceId && paths.Contains(c.RelativePath))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return chunks.ToDictionary(c => c.GetLookupKey(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, CodeChunk>> GetExistingChunksAsync(
        string workspacePath,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        var paths = relativePaths.ToList();
        if (paths.Count == 0)
        {
            return new Dictionary<string, CodeChunk>();
        }

        var chunks = await _context.CodeChunks
            .AsNoTracking()
            .Where(c => c.WorkspacePath == workspacePath && paths.Contains(c.RelativePath))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return chunks.ToDictionary(c => c.GetLookupKey(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<CodeChunk>> GetAllByWorkspaceAsync(
        Guid repositoryWorkspaceId,
        CancellationToken cancellationToken = default)
    {
        return await _context.CodeChunks
            .AsNoTracking()
            .Where(c => c.RepositoryWorkspaceId == repositoryWorkspaceId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CodeChunk>> GetAllByWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        return await _context.CodeChunks
            .AsNoTracking()
            .Where(c => c.WorkspacePath == workspacePath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        await _context.CodeChunks.AddRangeAsync(chunks, cancellationToken).ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            var tracked = _context.CodeChunks.Local.FirstOrDefault(c => c.Id == chunk.Id);
            if (tracked != null)
            {
                _context.Entry(tracked).CurrentValues.SetValues(chunk);
            }
            else
            {
                _context.CodeChunks.Update(chunk);
            }
        }
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        var ids = chunks.Select(c => c.Id).ToHashSet();
        foreach (var id in ids)
        {
            var tracked = _context.CodeChunks.Local.FirstOrDefault(c => c.Id == id);
            if (tracked != null)
            {
                _context.CodeChunks.Remove(tracked);
            }
            else
            {
                var stub = new CodeChunk { Id = id };
                _context.CodeChunks.Attach(stub);
                _context.CodeChunks.Remove(stub);
            }
        }
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountByWorkspaceAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default)
    {
        return await _context.CodeChunks
            .AsNoTracking()
            .CountAsync(c => c.RepositoryWorkspaceId == repositoryWorkspaceId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountByWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        return await _context.CodeChunks
            .AsNoTracking()
            .CountAsync(c => c.WorkspacePath == workspacePath, cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class EfIndexJobRepository : IIndexJobRepository
{
    private readonly DevPilotDbContext _context;

    public EfIndexJobRepository(DevPilotDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(IndexJob job, CancellationToken cancellationToken = default)
    {
        await _context.IndexJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(IndexJob job, CancellationToken cancellationToken = default)
    {
        var tracked = _context.IndexJobs.Local.FirstOrDefault(j => j.Id == job.Id);
        if (tracked != null && !ReferenceEquals(tracked, job))
        {
            _context.Entry(tracked).CurrentValues.SetValues(job);
        }
        else if (tracked == null)
        {
            _context.IndexJobs.Update(job);
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _context.ChangeTracker.Clear();
            _context.IndexJobs.Update(job);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IndexJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.IndexJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexJob>> GetByWorkspaceAsync(
        Guid repositoryWorkspaceId,
        CancellationToken cancellationToken = default)
    {
        return await _context.IndexJobs
            .AsNoTracking()
            .Where(j => j.RepositoryWorkspaceId == repositoryWorkspaceId)
            .OrderByDescending(j => j.StartedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexJob>> GetByWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        return await _context.IndexJobs
            .AsNoTracking()
            .Where(j => j.WorkspacePath == workspacePath)
            .OrderByDescending(j => j.StartedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IndexJob?> GetLatestByWorkspaceAsync(
        Guid repositoryWorkspaceId,
        CancellationToken cancellationToken = default)
    {
        return await _context.IndexJobs
            .AsNoTracking()
            .Where(j => j.RepositoryWorkspaceId == repositoryWorkspaceId)
            .OrderByDescending(j => j.StartedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class EfProjectBrainConversationRepository : IProjectBrainConversationRepository
{
    private readonly DevPilotDbContext _context;

    public EfProjectBrainConversationRepository(DevPilotDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ProjectBrainConversation>> GetByWorkspaceIdAsync(
        Guid repositoryWorkspaceId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ProjectBrainConversations
            .AsNoTracking()
            .Include(c => c.Messages)
            .Where(c => c.RepositoryWorkspaceId == repositoryWorkspaceId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProjectBrainConversation?> GetByIdWithMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ProjectBrainConversations
            .AsNoTracking()
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProjectBrainConversation?> GetByIdAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ProjectBrainConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(
        ProjectBrainConversation conversation,
        CancellationToken cancellationToken = default)
    {
        await _context.ProjectBrainConversations
            .AddAsync(conversation, cancellationToken)
            .ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(
        ProjectBrainConversation conversation,
        CancellationToken cancellationToken = default)
    {
        var tracked = _context.ProjectBrainConversations.Local.FirstOrDefault(c => c.Id == conversation.Id);
        if (tracked != null && !ReferenceEquals(tracked, conversation))
        {
            _context.Entry(tracked).CurrentValues.SetValues(conversation);
        }
        else if (tracked == null)
        {
            _context.ProjectBrainConversations.Update(conversation);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddMessageAsync(
        ProjectBrainMessage message,
        CancellationToken cancellationToken = default)
    {
        await _context.ProjectBrainMessages
            .AddAsync(message, cancellationToken)
            .ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        ProjectBrainConversation conversation,
        CancellationToken cancellationToken = default)
    {
        var tracked = _context.ProjectBrainConversations.Local.FirstOrDefault(c => c.Id == conversation.Id);
        if (tracked != null)
        {
            _context.ProjectBrainConversations.Remove(tracked);
        }
        else
        {
            _context.ProjectBrainConversations.Attach(conversation);
            _context.ProjectBrainConversations.Remove(conversation);
        }
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
