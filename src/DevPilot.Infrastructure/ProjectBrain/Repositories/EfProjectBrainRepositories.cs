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
        _context.CodeChunks.UpdateRange(chunks);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteRangeAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        _context.CodeChunks.RemoveRange(chunks);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
        _context.IndexJobs.Update(job);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IndexJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.IndexJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken)
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
}
