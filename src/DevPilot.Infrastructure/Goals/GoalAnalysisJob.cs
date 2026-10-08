using DevPilot.Application.Goals;
using DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Goals;

/// <summary>Hangfire-backed dispatcher: the analyses of a goal run beside executions, never inside the goal loop.</summary>
public sealed class HangfireGoalAnalysisDispatcher : IGoalAnalysisDispatcher
{
    private readonly IBackgroundJobClient _jobs;

    public HangfireGoalAnalysisDispatcher(IBackgroundJobClient jobs)
    {
        _jobs = jobs;
    }

    public void EnqueueAnalysis(Guid taskId) => _jobs.Enqueue<GoalAnalysisJob>(job => job.AnalyzeAsync(taskId));
}

/// <summary>
/// Runs one task analysis. Retries are disabled on purpose: the analysis records its own failure on the
/// task and the goal loop decides whether to try again, so a hidden Hangfire retry would only repeat a paid call.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class GoalAnalysisJob
{
    private readonly IAnalyzeTaskImpactCommandHandler _analyze;
    private readonly ILogger<GoalAnalysisJob> _logger;

    public GoalAnalysisJob(IAnalyzeTaskImpactCommandHandler analyze, ILogger<GoalAnalysisJob> logger)
    {
        _analyze = analyze;
        _logger = logger;
    }

    public async Task AnalyzeAsync(Guid taskId)
    {
        var result = await _analyze.HandleAsync(new AnalyzeTaskImpactCommand(taskId)).ConfigureAwait(false);
        if (!result.Success)
        {
            _logger.LogWarning("Goal analysis of task {TaskId} did not complete: {Reason}", taskId, result.ErrorMessage);
        }
    }
}
