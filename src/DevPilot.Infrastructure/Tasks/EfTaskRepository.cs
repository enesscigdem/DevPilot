using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure.Tasks;

public sealed class EfTaskRepository : ITaskRepository
{
    private readonly DevPilotDbContext _dbContext;

    public EfTaskRepository(DevPilotDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
    {
        _dbContext.DevelopmentTasks.Add(task);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
    {
        _dbContext.DevelopmentTasks.Update(task);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default)
    {
        _dbContext.DevelopmentTasks.Remove(task);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DevelopmentTasks
            .Include(t => t.RepositoryWorkspace)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(
        DevelopmentTaskQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        IQueryable<DevelopmentTask> query = _dbContext.DevelopmentTasks
            .Include(t => t.RepositoryWorkspace);

        if (filter.Status.HasValue)
        {
            query = query.Where(t => t.Status == filter.Status.Value);
        }

        if (filter.Priority.HasValue)
        {
            query = query.Where(t => t.Priority == filter.Priority.Value);
        }

        if (filter.RepositoryWorkspaceId.HasValue)
        {
            query = query.Where(t => t.RepositoryWorkspaceId == filter.RepositoryWorkspaceId.Value);
        }

        return await query
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class RepositoryWorkspaceQuery : IRepositoryWorkspaceQuery
{
    private readonly DevPilotDbContext _dbContext;

    public RepositoryWorkspaceQuery(DevPilotDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<RepositoryWorkspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RepositoryWorkspaces
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }
}
