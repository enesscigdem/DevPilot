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
}
