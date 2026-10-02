namespace DevPilot.Application.AiProviders;

/// <summary>
/// Manages the models a user registered in the panel and the stage-to-model assignments.
/// Validation problems throw <see cref="ArgumentException"/>; unknown ids throw <see cref="KeyNotFoundException"/>.
/// </summary>
public interface IAiModelService
{
    Task<IReadOnlyList<AiModelDto>> ListAsync(CancellationToken cancellationToken);

    Task<AiModelDto> CreateAsync(SaveAiModelRequest request, CancellationToken cancellationToken);

    Task<AiModelDto> UpdateAsync(Guid id, SaveAiModelRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Sends a tiny prompt to the stored model and records the outcome on it.</summary>
    Task<AiModelTestResultDto> TestAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One entry per stage, including stages that have no assignment yet.</summary>
    Task<IReadOnlyList<AiStageAssignmentDto>> GetStageAssignmentsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AiStageAssignmentDto>> SetStageAssignmentsAsync(
        IReadOnlyList<AiStageAssignmentDto> assignments,
        CancellationToken cancellationToken);
}
