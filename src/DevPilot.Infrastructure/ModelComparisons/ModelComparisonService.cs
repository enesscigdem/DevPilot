using DevPilot.Application.Executions.Ports;
using DevPilot.Application.ModelComparisons;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Constants;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.ModelComparisons;

internal sealed class ModelComparisonService : IModelComparisonService
{
    private const int MinModels = 2;
    private const int MaxModels = 3;

    private readonly DevPilotDbContext _db;
    private readonly ITaskRepository _taskRepository;
    private readonly IImpactAnalysisRepository _analysisRepository;
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionDispatcher _dispatcher;
    private readonly ILogger<ModelComparisonService> _logger;

    public ModelComparisonService(
        DevPilotDbContext db,
        ITaskRepository taskRepository,
        IImpactAnalysisRepository analysisRepository,
        IExecutionRepository executionRepository,
        IExecutionDispatcher dispatcher,
        ILogger<ModelComparisonService> logger)
    {
        _db = db;
        _taskRepository = taskRepository;
        _analysisRepository = analysisRepository;
        _executionRepository = executionRepository;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<ModelComparisonDto> StartAsync(StartModelComparisonRequest request, CancellationToken cancellationToken)
    {
        var modelIds = request.ModelIds.Distinct().ToList();
        if (modelIds.Count != request.ModelIds.Count)
        {
            throw new ArgumentException("Pick each model only once.");
        }

        if (modelIds.Count is < MinModels or > MaxModels)
        {
            throw new ArgumentException($"Pick between {MinModels} and {MaxModels} models to compare.");
        }

        var models = await _db.AiModelConfigs.AsNoTracking()
            .Where(m => modelIds.Contains(m.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var id in modelIds)
        {
            var model = models.FirstOrDefault(m => m.Id == id)
                ?? throw new ArgumentException("One of the selected models no longer exists.");
            if (!model.IsEnabled)
            {
                throw new ArgumentException($"Model {model.Name} is disabled. Enable it or pick another one.");
            }
        }

        var task = await _taskRepository.GetByIdAsync(request.TaskId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Task not found.");

        // The plan must already be approved (it has been executed or is ready to be): comparing models
        // means running the same approved plan, so a draft or still-pending plan cannot be compared.
        if (task.Status is not (DevelopmentTaskStatus.Approved or DevelopmentTaskStatus.Failed or DevelopmentTaskStatus.Completed))
        {
            throw new InvalidOperationException(
                $"A task in '{task.Status}' status cannot be compared. Approve its plan first.");
        }

        var analysis = await _analysisRepository.GetLatestByTaskIdAsync(task.Id, cancellationToken).ConfigureAwait(false);
        if (analysis is null || analysis.Status != ImpactAnalysisStatus.Completed)
        {
            throw new InvalidOperationException("A completed impact analysis is required before models can be compared.");
        }

        if (analysis.StructuredResult?.ImpactedFiles is { } files && files.Count > ExecutionCapacityPolicy.MaxImpactedFiles)
        {
            throw new InvalidOperationException(
                $"The approved plan contains {files.Count} files, above the executable maximum of {ExecutionCapacityPolicy.MaxImpactedFiles}.");
        }

        if (await _executionRepository.HasActiveExecutionForTaskAsync(task.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("An execution is already running for this task.");
        }

        if (await HasOpenComparisonAsync(task.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A model comparison is already in progress for this task.");
        }

        var comparison = new ModelComparison
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            CreatedAt = DateTime.UtcNow,
        };
        for (var i = 0; i < modelIds.Count; i++)
        {
            var model = models.First(m => m.Id == modelIds[i]);
            comparison.Runs.Add(new ModelComparisonRun
            {
                Id = Guid.NewGuid(),
                Position = i,
                AiModelConfigId = model.Id,
                ModelName = model.Name,
            });
        }

        _db.ModelComparisons.Add(comparison);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // First run starts right away; the coordinator starts the rest as each one finishes.
        await AdvanceAsync(comparison.Id, cancellationToken).ConfigureAwait(false);

        return await GetAsync(comparison.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelComparisonDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var comparison = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return (await MapAsync(new[] { comparison }, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<IReadOnlyList<ModelComparisonDto>> ListForTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var comparisons = await _db.ModelComparisons.AsNoTracking()
            .Include(c => c.Runs)
            .Include(c => c.DevelopmentTask)
            .Where(c => c.DevelopmentTaskId == taskId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return await MapAsync(comparisons, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelComparisonDto> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var comparison = await _db.ModelComparisons
            .Include(c => c.Runs)
            .Include(c => c.DevelopmentTask)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Model comparison not found.");

        if (comparison.CancelledAt is null)
        {
            comparison.CancelledAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return (await MapAsync(new[] { comparison }, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task AdvanceAllAsync(CancellationToken cancellationToken)
    {
        var openIds = await _db.ModelComparisons.AsNoTracking()
            .Where(c => c.CancelledAt == null)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var id in openIds)
        {
            try
            {
                await AdvanceAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Advancing model comparison {ComparisonId} failed.", id);
            }
        }
    }

    /// <summary>Starts the lowest-position run that has no execution yet, if the task is idle.</summary>
    private async Task AdvanceAsync(Guid comparisonId, CancellationToken cancellationToken)
    {
        // Tracked entities from an earlier call in this scope must not hide state written by a worker.
        _db.ChangeTracker.Clear();

        var comparison = await _db.ModelComparisons.AsNoTracking()
            .Include(c => c.Runs)
            .FirstOrDefaultAsync(c => c.Id == comparisonId, cancellationToken)
            .ConfigureAwait(false);
        if (comparison is null || comparison.CancelledAt is not null)
        {
            return;
        }

        var runIds = comparison.Runs.Select(r => r.Id).ToList();
        var startedRunIds = await _db.TaskExecutions.AsNoTracking()
            .Where(e => e.ModelComparisonRunId != null && runIds.Contains(e.ModelComparisonRunId.Value))
            .Select(e => e.ModelComparisonRunId!.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var next = comparison.Runs
            .Where(r => !startedRunIds.Contains(r.Id))
            .OrderBy(r => r.Position)
            .FirstOrDefault();
        if (next is null)
        {
            return;
        }

        // The previous run (or any other execution of this task) must be over first.
        if (await _executionRepository.HasActiveExecutionForTaskAsync(comparison.DevelopmentTaskId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var task = await _taskRepository.GetByIdAsync(comparison.DevelopmentTaskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return;
        }

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            PinnedAiModelId = next.AiModelConfigId,
            PinnedAiModelName = next.ModelName,
            ModelComparisonRunId = next.Id,
        };

        task.Status = DevelopmentTaskStatus.Executing;
        task.UpdatedAt = DateTime.UtcNow;

        // The unique "one active execution per task" index arbitrates if two callers race here.
        if (!await _executionRepository.StartExecutionAtomicAsync(execution, task, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogInformation(
            "Model comparison {ComparisonId}: started run {Position} ({Model}) as execution {ExecutionId}.",
            comparisonId, next.Position, next.ModelName, execution.Id);

        try
        {
            _dispatcher.EnqueueProcessExecution(execution.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model comparison {ComparisonId}: dispatch failed for execution {ExecutionId}.", comparisonId, execution.Id);
            await _executionRepository
                .FailAsync(execution.Id, "Failed to enqueue background processing job.", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task<bool> HasOpenComparisonAsync(Guid taskId, CancellationToken cancellationToken) =>
        _db.ModelComparisons.AsNoTracking()
            .Where(c => c.DevelopmentTaskId == taskId && c.CancelledAt == null)
            .AnyAsync(
                c => c.Runs.Any(r => !_db.TaskExecutions.Any(e => e.ModelComparisonRunId == r.Id)
                                     || _db.TaskExecutions.Any(e => e.ModelComparisonRunId == r.Id
                                                                    && (e.Status == TaskExecutionStatus.Pending || e.Status == TaskExecutionStatus.Running))),
                cancellationToken);

    private async Task<ModelComparison> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.ModelComparisons.AsNoTracking()
            .Include(c => c.Runs)
            .Include(c => c.DevelopmentTask)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new KeyNotFoundException("Model comparison not found.");

    private async Task<List<ModelComparisonDto>> MapAsync(IReadOnlyList<ModelComparison> comparisons, CancellationToken cancellationToken)
    {
        var runIds = comparisons.SelectMany(c => c.Runs.Select(r => r.Id)).ToList();
        var executions = await _db.TaskExecutions.AsNoTracking()
            .Where(e => e.ModelComparisonRunId != null && runIds.Contains(e.ModelComparisonRunId.Value))
            .Select(e => new { RunId = e.ModelComparisonRunId!.Value, e.Id, e.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byRun = executions.ToDictionary(e => e.RunId);

        var workspaceIds = comparisons.ToDictionary(c => c.Id, c => c.DevelopmentTask?.RepositoryWorkspaceId ?? Guid.Empty);

        return comparisons.Select(c =>
        {
            var cancelled = c.CancelledAt is not null;
            var runs = c.Runs.OrderBy(r => r.Position).Select(r =>
            {
                byRun.TryGetValue(r.Id, out var execution);
                var state = execution is null
                    ? (cancelled ? ModelComparisonRunState.Skipped : ModelComparisonRunState.Queued)
                    : execution.Status is TaskExecutionStatus.Pending or TaskExecutionStatus.Running
                        ? ModelComparisonRunState.Running
                        : ModelComparisonRunState.Finished;

                return new ModelComparisonRunDto
                {
                    Id = r.Id,
                    Position = r.Position,
                    ModelId = r.AiModelConfigId,
                    ModelName = r.ModelName,
                    State = state,
                    ExecutionId = execution?.Id,
                    ExecutionStatus = execution?.Status,
                };
            }).ToList();

            var status = runs.Any(r => r.State is ModelComparisonRunState.Queued or ModelComparisonRunState.Running)
                ? ModelComparisonStatus.Running
                : cancelled && runs.Any(r => r.State == ModelComparisonRunState.Skipped)
                    ? ModelComparisonStatus.Cancelled
                    : ModelComparisonStatus.Completed;

            return new ModelComparisonDto
            {
                Id = c.Id,
                TaskId = c.DevelopmentTaskId,
                TaskTitle = c.DevelopmentTask?.Title ?? string.Empty,
                RepositoryWorkspaceId = workspaceIds[c.Id],
                CreatedAt = c.CreatedAt,
                Status = status,
                Runs = runs,
            };
        }).ToList();
    }
}
