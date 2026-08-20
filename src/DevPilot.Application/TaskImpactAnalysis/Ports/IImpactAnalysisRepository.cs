using DevPilot.Domain.Entities;
using TaskImpactAnalysisEntity = DevPilot.Domain.Entities.TaskImpactAnalysis;

namespace DevPilot.Application.TaskImpactAnalysis.Ports;

public interface IImpactAnalysisRepository
{
    Task<TaskImpactAnalysisEntity?> GetLatestByTaskIdAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        TaskImpactAnalysisEntity analysis,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        TaskImpactAnalysisEntity analysis,
        CancellationToken cancellationToken = default);

    Task<bool> StartAnalysisAtomicAsync(
        TaskImpactAnalysisEntity analysis,
        DevelopmentTask task,
        CancellationToken cancellationToken = default);

    Task<bool> HasActiveAnalysisForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<int> ReconcileStaleAnalysesAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default);
}
