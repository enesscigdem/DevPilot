namespace DevPilot.Application.ModelComparisons;

/// <summary>
/// Runs one approved task with several models, one after another, and exposes the runs for comparison.
/// Invalid input throws <see cref="ArgumentException"/>, unknown ids <see cref="KeyNotFoundException"/>,
/// and a task that cannot be executed right now <see cref="InvalidOperationException"/>.
/// </summary>
public interface IModelComparisonService
{
    Task<ModelComparisonDto> StartAsync(StartModelComparisonRequest request, CancellationToken cancellationToken);

    Task<ModelComparisonDto> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Comparisons of one task, newest first.</summary>
    Task<IReadOnlyList<ModelComparisonDto>> ListForTaskAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>Skips the runs that have not started. A run already in progress finishes normally.</summary>
    Task<ModelComparisonDto> CancelAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Starts the next queued run of every open comparison whose task is idle. Called by the coordinator.</summary>
    Task AdvanceAllAsync(CancellationToken cancellationToken);
}
