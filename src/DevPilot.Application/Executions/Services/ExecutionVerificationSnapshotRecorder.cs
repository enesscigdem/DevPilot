using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Services;

public sealed class ExecutionVerificationSnapshotRecorder : IExecutionVerificationSnapshotRecorder
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IExecutionActivityRepository _activityRepository;
    private readonly IExecutionVerificationSnapshotStore _store;
    private readonly AiPricingOptions? _pricing;
    private readonly ILogger<ExecutionVerificationSnapshotRecorder> _logger;

    public ExecutionVerificationSnapshotRecorder(
        IExecutionRepository executionRepository,
        IExecutionActivityRepository activityRepository,
        IExecutionVerificationSnapshotStore store,
        ILogger<ExecutionVerificationSnapshotRecorder> logger,
        AiPricingOptions? pricing = null)
    {
        _executionRepository = executionRepository;
        _activityRepository = activityRepository;
        _store = store;
        _logger = logger;
        _pricing = pricing;
    }

    public async Task RecordAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var execution = await _executionRepository.GetByIdAsync(executionId, cancellationToken).ConfigureAwait(false);
            if (execution is null)
            {
                return;
            }

            var activities = await _activityRepository.GetByExecutionIdAsync(executionId, cancellationToken).ConfigureAwait(false);
            var outcome = ExecutionVerificationEvaluator.DetermineOutcome(execution, activities);
            var snapshot = ExecutionVerdictBuilder.BuildSnapshot(execution, activities, outcome, _pricing);

            await _store
                .SaveAsync(executionId, outcome.ToString(), ExecutionVerdictBuilder.Serialize(snapshot), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // History only: a snapshot failure must never change the execution result.
            _logger.LogWarning(ex, "Failed to record verification snapshot for execution {ExecutionId}.", executionId);
        }
    }
}
