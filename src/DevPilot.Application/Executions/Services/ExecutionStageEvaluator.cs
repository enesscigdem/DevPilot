using DevPilot.Application.Executions.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Executions.Services;

public static class ExecutionStageEvaluator
{
    public static IReadOnlyList<ExecutionStageStepDto> EvaluateStages(
        TaskExecution execution,
        DevelopmentTask task,
        DevPilot.Domain.Entities.TaskImpactAnalysis? analysis,
        IReadOnlyList<ExecutionActivity> activities)
    {
        var hasCompletedAnalysis = analysis is not null && analysis.Status == ImpactAnalysisStatus.Completed;
        var hasFailedAnalysis = analysis is not null && analysis.Status == ImpactAnalysisStatus.Failed;

        // Stage 1: Analyze
        var analyzeState = hasCompletedAnalysis
            ? ExecutionStageStepState.Done
            : (hasFailedAnalysis
                ? ExecutionStageStepState.Failed
                : (task.Status == DevelopmentTaskStatus.Analyzing ? ExecutionStageStepState.Active : ExecutionStageStepState.Todo));

        // Stage 2: Plan
        var planState = (hasCompletedAnalysis && analysis?.StructuredResult != null)
            ? ExecutionStageStepState.Done
            : (hasFailedAnalysis
                ? ExecutionStageStepState.Failed
                : (task.Status == DevelopmentTaskStatus.Analyzing ? ExecutionStageStepState.Active : ExecutionStageStepState.Todo));

        // Stage 3: Approved (explicit semantic states and execution invariants)
        var hasExecutionStarted = execution.Status is TaskExecutionStatus.Running
                                                   or TaskExecutionStatus.Completed
                                                   or TaskExecutionStatus.Failed
                                                   or TaskExecutionStatus.Cancelled
                                  || activities.Count > 0;

        var isExplicitlyApproved = task.Status is DevelopmentTaskStatus.Approved
                                               or DevelopmentTaskStatus.Executing
                                               or DevelopmentTaskStatus.Completed
                                  || hasExecutionStarted;

        var approvedState = isExplicitlyApproved
            ? ExecutionStageStepState.Done
            : (task.Status == DevelopmentTaskStatus.Rejected
                ? ExecutionStageStepState.Failed
                : (task.Status == DevelopmentTaskStatus.AwaitingApproval ? ExecutionStageStepState.Active : ExecutionStageStepState.Todo));

        // Stage 4: Implement (DeveloperAgent)
        var hasDevAgentCompleted = activities.Any(a => a.Stage == ExecutionStage.DeveloperAgent && a.Status == ExecutionActivityStatus.Completed);
        var hasDevAgentFailed = activities.Any(a => a.Stage == ExecutionStage.DeveloperAgent && a.Status == ExecutionActivityStatus.Failed);
        var hasDevAgentStarted = activities.Any(a => a.Stage == ExecutionStage.DeveloperAgent && a.Status == ExecutionActivityStatus.Started);

        ExecutionStageStepState implementState;
        if (hasDevAgentCompleted)
        {
            implementState = ExecutionStageStepState.Done;
        }
        else if (hasDevAgentFailed)
        {
            implementState = ExecutionStageStepState.Failed;
        }
        else if (execution.Status == TaskExecutionStatus.Running && (hasDevAgentStarted || activities.All(a => a.Stage <= ExecutionStage.Workspace)))
        {
            implementState = ExecutionStageStepState.Active;
        }
        else if (execution.Status == TaskExecutionStatus.Cancelled)
        {
            implementState = ExecutionStageStepState.Blocked;
        }
        else if (execution.Status == TaskExecutionStatus.Failed && !activities.Any(a => a.Stage > ExecutionStage.DeveloperAgent))
        {
            implementState = ExecutionStageStepState.Failed;
        }
        else
        {
            implementState = ExecutionStageStepState.Todo;
        }

        // Stage 5: Build & Test
        var buildPassed = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Completed);
        var buildFailed = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Failed);
        var buildStarted = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Started);

        var testPassed = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Completed);
        var testFailed = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Failed);
        var testStarted = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Started);
        var repositoryVerificationReady = activities.Any(a =>
            a.Status == ExecutionActivityStatus.Completed &&
            (a.Message.StartsWith("Repository checks passed", StringComparison.OrdinalIgnoreCase) ||
             a.Message.StartsWith("Tests passed", StringComparison.OrdinalIgnoreCase) ||
             a.Message.StartsWith("No new regressions", StringComparison.OrdinalIgnoreCase) ||
             (a.MetadataJson != null && (a.MetadataJson.Contains("\"VerificationOutcome\":\"NoNewRegressions\"", StringComparison.OrdinalIgnoreCase) || a.MetadataJson.Contains("\"BaselineClassification\":\"PreExisting\"", StringComparison.OrdinalIgnoreCase)))));

        ExecutionStageStepState buildTestState;
        if (repositoryVerificationReady || (buildPassed && testPassed))
        {
            buildTestState = ExecutionStageStepState.Done;
        }
        else if (buildFailed || testFailed)
        {
            buildTestState = ExecutionStageStepState.Failed;
        }
        else if (execution.Status == TaskExecutionStatus.Running && (buildStarted || testStarted || (hasDevAgentCompleted && !buildPassed)))
        {
            buildTestState = ExecutionStageStepState.Active;
        }
        else if (execution.Status == TaskExecutionStatus.Failed && hasDevAgentCompleted && (!buildPassed || !testPassed))
        {
            buildTestState = ExecutionStageStepState.Failed;
        }
        else
        {
            buildTestState = ExecutionStageStepState.Todo;
        }

        // Stage 6: Review
        ExecutionStageStepState reviewState;
        if (execution.ReviewStatus == ExecutionReviewStatus.Approved)
        {
            reviewState = ExecutionStageStepState.Done;
        }
        else if (execution.ReviewStatus == ExecutionReviewStatus.Rejected)
        {
            reviewState = ExecutionStageStepState.Failed;
        }
        else if (execution.Status == TaskExecutionStatus.Completed && execution.ReviewStatus == ExecutionReviewStatus.Pending)
        {
            reviewState = ExecutionStageStepState.Active;
        }
        else
        {
            reviewState = ExecutionStageStepState.Todo;
        }

        // Stage 7: Pull Request
        ExecutionStageStepState prState;
        if (execution.PullRequestStatus == ExecutionPullRequestStatus.Open || execution.MergeStatus == ExecutionMergeStatus.Merged)
        {
            prState = ExecutionStageStepState.Done;
        }
        else if (execution.PullRequestStatus == ExecutionPullRequestStatus.Failed)
        {
            prState = ExecutionStageStepState.Failed;
        }
        else if (execution.PullRequestStatus == ExecutionPullRequestStatus.InProgress || (execution.ReviewStatus == ExecutionReviewStatus.Approved && execution.PushStatus == ExecutionPushStatus.Pushed))
        {
            prState = ExecutionStageStepState.Active;
        }
        else
        {
            prState = ExecutionStageStepState.Todo;
        }

        return new List<ExecutionStageStepDto>
        {
            new() { StageKey = "analyze", Label = "Analyze", State = analyzeState },
            new() { StageKey = "plan", Label = "Plan", State = planState },
            new() { StageKey = "approved", Label = "Approved", State = approvedState },
            new() { StageKey = "implement", Label = "Implement", State = implementState },
            new() { StageKey = "build", Label = "Build & Test", State = buildTestState },
            new() { StageKey = "review", Label = "Review", State = reviewState },
            new() { StageKey = "pr", Label = "Pull Request", State = prState },
        };
    }

    /// <summary>
    /// Calculates the deterministic progress percentage (0 to 100) based on how far the execution
    /// workflow has reached across the 7 visible pipeline stages.
    /// </summary>
    public static int CalculateProgressPercentage(IReadOnlyList<ExecutionStageStepDto> stages)
    {
        if (stages == null || stages.Count == 0)
        {
            return 0;
        }

        var allDone = stages.All(s => s.State == ExecutionStageStepState.Done);
        if (allDone)
        {
            return 100;
        }

        // Find the furthest reached stage (1-indexed, 1 to 7)
        var furthestIndex = 0;
        ExecutionStageStepState furthestState = ExecutionStageStepState.Todo;

        for (var i = 0; i < stages.Count; i++)
        {
            var state = stages[i].State;
            if (state is ExecutionStageStepState.Done or ExecutionStageStepState.Active or ExecutionStageStepState.Failed or ExecutionStageStepState.Blocked)
            {
                furthestIndex = i + 1;
                furthestState = state;
            }
        }

        if (furthestIndex == 0)
        {
            return 0;
        }

        // If the furthest reached stage is Active, it represents partial completion of that stage
        double effectiveReached = furthestState == ExecutionStageStepState.Active
            ? furthestIndex - 0.5
            : furthestIndex;

        var percentage = (int)Math.Round((effectiveReached / stages.Count) * 100.0);
        return Math.Clamp(percentage, 0, 100);
    }
}
