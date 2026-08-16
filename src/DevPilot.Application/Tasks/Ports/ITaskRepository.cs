using DevPilot.Domain.Entities;

namespace DevPilot.Application.Tasks.Ports;

public interface ITaskRepository
{
    Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default);

    Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default);

    Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default);

    Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevelopmentTask>> GetAllAsync(
        DevelopmentTaskQueryFilter filter,
        CancellationToken cancellationToken = default);
}

public sealed class DevelopmentTaskQueryFilter
{
    public DevPilot.Domain.Enums.DevelopmentTaskStatus? Status { get; set; }

    public DevPilot.Domain.Enums.DevelopmentTaskPriority? Priority { get; set; }

    public Guid? RepositoryWorkspaceId { get; set; }
}
