using DevPilot.Domain.Entities;

namespace DevPilot.Application.Tasks.Ports;

public interface ITaskRepository
{
    Task AddAsync(DevelopmentTask task, CancellationToken cancellationToken = default);

    Task UpdateAsync(DevelopmentTask task, CancellationToken cancellationToken = default);

    Task DeleteAsync(DevelopmentTask task, CancellationToken cancellationToken = default);

    Task<DevelopmentTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The keys, among the given ones, that already exist as imported tasks in the workspace.</summary>
    Task<IReadOnlySet<string>> FindByExternalKeysAsync(
        Guid repositoryWorkspaceId,
        string externalSource,
        IReadOnlyCollection<string> externalKeys,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

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
