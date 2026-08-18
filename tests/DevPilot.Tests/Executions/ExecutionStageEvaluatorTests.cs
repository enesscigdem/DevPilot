using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class ExecutionStageEvaluatorTests
{
    private static DevelopmentTask CreateTask(DevelopmentTaskStatus status)
    {
        return new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = Guid.NewGuid(),
            Title = "Implement feature X",
            Description = "Feature description",
            Status = status,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static TaskExecution CreateExecution(
        Guid taskId,
        TaskExecutionStatus status = TaskExecutionStatus.Pending,
        ExecutionReviewStatus reviewStatus = ExecutionReviewStatus.Pending,
        ExecutionPushStatus pushStatus = ExecutionPushStatus.None,
        ExecutionPullRequestStatus prStatus = ExecutionPullRequestStatus.None,
        ExecutionMergeStatus mergeStatus = ExecutionMergeStatus.None)
    {
        return new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskId,
            Status = status,
            ReviewStatus = reviewStatus,
            PushStatus = pushStatus,
            PullRequestStatus = prStatus,
            MergeStatus = mergeStatus,
            CreatedAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public void EvaluateStages_WhenTaskHasNoAnalysisAndNotStarted_AllStagesTodo()
    {
        var task = CreateTask(DevelopmentTaskStatus.Draft);
        var execution = CreateExecution(task.Id);
        var activities = Array.Empty<ExecutionActivity>();

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, null, activities);
        var progress = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        Assert.Equal(7, stages.Count);
        Assert.All(stages, s => Assert.Equal(ExecutionStageStepState.Todo, s.State));
        Assert.Equal(0, progress);
    }

    [Fact]
    public void EvaluateStages_WhenTaskAnalyzing_AnalyzeAndPlanAreActive()
    {
        var task = CreateTask(DevelopmentTaskStatus.Analyzing);
        var execution = CreateExecution(task.Id);
        var activities = Array.Empty<ExecutionActivity>();

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, null, activities);
        var progress = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        Assert.Equal(ExecutionStageStepState.Active, stages[0].State); // Analyze
        Assert.Equal(ExecutionStageStepState.Active, stages[1].State); // Plan
        Assert.Equal(ExecutionStageStepState.Todo, stages[2].State);   // Approved
        Assert.True(progress > 0 && progress < 30);
    }

    [Fact]
    public void EvaluateStages_WhenTaskAnalysisCompletedWithPlan_AnalyzeAndPlanDone()
    {
        var task = CreateTask(DevelopmentTaskStatus.AwaitingApproval);
        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            Summary = "Plan summary",
            StructuredResult = new ImpactAnalysisResultData
            {
                Summary = "Summary",
                Confidence = 90,
                ProposedPlan = new List<ProposedPlanStep> { new() { Title = "Step 1", Description = "Step 1" } }
            },
            CreatedAt = DateTime.UtcNow,
        };
        var execution = CreateExecution(task.Id);
        var activities = Array.Empty<ExecutionActivity>();

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, analysis, activities);

        Assert.Equal(ExecutionStageStepState.Done, stages[0].State);   // Analyze
        Assert.Equal(ExecutionStageStepState.Done, stages[1].State);   // Plan
        Assert.Equal(ExecutionStageStepState.Active, stages[2].State); // Approved (AwaitingApproval)
    }

    [Fact]
    public void EvaluateStages_WhenTaskExplicitlyApproved_ApprovedStageDone()
    {
        var task = CreateTask(DevelopmentTaskStatus.Approved);
        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow,
        };
        var execution = CreateExecution(task.Id, TaskExecutionStatus.Running);
        var activities = new List<ExecutionActivity>
        {
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Started, CreatedAt = DateTime.UtcNow }
        };

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, analysis, activities);
        var progress = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        Assert.Equal(ExecutionStageStepState.Done, stages[0].State);   // Analyze
        Assert.Equal(ExecutionStageStepState.Done, stages[1].State);   // Plan
        Assert.Equal(ExecutionStageStepState.Done, stages[2].State);   // Approved
        Assert.Equal(ExecutionStageStepState.Active, stages[3].State); // Implement
        Assert.Equal(50, progress); // Reached stage 4 (Active) => (3.5 / 7) * 100 = 50%
    }

    [Fact]
    public void EvaluateStages_WhenDevAgentFails_ImplementStageFailedAndNever100Percent()
    {
        var task = CreateTask(DevelopmentTaskStatus.Executing);
        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow,
        };
        var execution = CreateExecution(task.Id, TaskExecutionStatus.Failed);
        var activities = new List<ExecutionActivity>
        {
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Failed, Message = "Agent error", CreatedAt = DateTime.UtcNow }
        };

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, analysis, activities);
        var progress = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        Assert.Equal(ExecutionStageStepState.Done, stages[0].State);   // Analyze
        Assert.Equal(ExecutionStageStepState.Done, stages[1].State);   // Plan
        Assert.Equal(ExecutionStageStepState.Done, stages[2].State);   // Approved
        Assert.Equal(ExecutionStageStepState.Failed, stages[3].State); // Implement
        Assert.Equal(ExecutionStageStepState.Todo, stages[4].State);   // Build
        Assert.Equal(ExecutionStageStepState.Todo, stages[5].State);   // Review
        Assert.Equal(ExecutionStageStepState.Todo, stages[6].State);   // PR

        Assert.Equal(57, progress); // 4 / 7 * 100 = 57%
        Assert.NotEqual(100, progress);
    }

    [Fact]
    public void EvaluateStages_WhenBuildFails_BuildTestStageFailed()
    {
        var task = CreateTask(DevelopmentTaskStatus.Executing);
        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow,
        };
        var execution = CreateExecution(task.Id, TaskExecutionStatus.Failed);
        var activities = new List<ExecutionActivity>
        {
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Failed, Message = "Compiler error", CreatedAt = DateTime.UtcNow.AddSeconds(1) }
        };

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, analysis, activities);
        var progress = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        Assert.Equal(ExecutionStageStepState.Done, stages[0].State);   // Analyze
        Assert.Equal(ExecutionStageStepState.Done, stages[1].State);   // Plan
        Assert.Equal(ExecutionStageStepState.Done, stages[2].State);   // Approved
        Assert.Equal(ExecutionStageStepState.Done, stages[3].State);   // Implement
        Assert.Equal(ExecutionStageStepState.Failed, stages[4].State); // Build & Test
        Assert.Equal(71, progress); // 5 / 7 * 100 = 71%
        Assert.NotEqual(100, progress);
    }

    [Fact]
    public void EvaluateStages_WhenExecutionCompletedAndReviewPending_ReviewStageActive()
    {
        var task = CreateTask(DevelopmentTaskStatus.Completed);
        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow,
        };
        var execution = CreateExecution(task.Id, TaskExecutionStatus.Completed, ExecutionReviewStatus.Pending);
        var activities = new List<ExecutionActivity>
        {
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow.AddSeconds(1) },
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow.AddSeconds(2) }
        };

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, analysis, activities);
        var progress = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        Assert.Equal(ExecutionStageStepState.Done, stages[0].State);   // Analyze
        Assert.Equal(ExecutionStageStepState.Done, stages[1].State);   // Plan
        Assert.Equal(ExecutionStageStepState.Done, stages[2].State);   // Approved
        Assert.Equal(ExecutionStageStepState.Done, stages[3].State);   // Implement
        Assert.Equal(ExecutionStageStepState.Done, stages[4].State);   // Build & Test
        Assert.Equal(ExecutionStageStepState.Active, stages[5].State); // Review (Pending after completion)
        Assert.Equal(ExecutionStageStepState.Todo, stages[6].State);   // PR

        Assert.Equal(79, progress); // (5.5 / 7) * 100 = 79%
    }

    [Fact]
    public void EvaluateStages_WhenPullRequestIsOpen_ProgressIs100PercentEvenBeforeMerge()
    {
        var task = CreateTask(DevelopmentTaskStatus.Completed);
        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow,
        };
        var execution = CreateExecution(
            task.Id,
            TaskExecutionStatus.Completed,
            ExecutionReviewStatus.Approved,
            ExecutionPushStatus.Pushed,
            ExecutionPullRequestStatus.Open,
            ExecutionMergeStatus.None);

        var activities = new List<ExecutionActivity>
        {
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow.AddSeconds(1) },
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow.AddSeconds(2) }
        };

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, analysis, activities);
        var progress = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        Assert.All(stages, s => Assert.Equal(ExecutionStageStepState.Done, s.State));
        Assert.Equal(100, progress); // Visible 7-stage rail is 100% complete
    }

    [Fact]
    public void EvaluateStages_WhenTaskStatusIsFailedDueToExecutionFailure_ApprovedStageIsDoneBasedOnExecutionInvariants()
    {
        // Real-world scenario: Execution started, DeveloperAgent finished, Build failed.
        // EfExecutionRepository.FailAsync set both execution.Status = Failed and task.Status = Failed.
        var task = CreateTask(DevelopmentTaskStatus.Failed);
        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow,
        };
        var execution = CreateExecution(task.Id, TaskExecutionStatus.Failed);
        var activities = new List<ExecutionActivity>
        {
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Failed, Message = "Build failed", CreatedAt = DateTime.UtcNow.AddSeconds(1) }
        };

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, analysis, activities);
        var progress = ExecutionStageEvaluator.CalculateProgressPercentage(stages);

        Assert.Equal(ExecutionStageStepState.Done, stages[0].State);   // Analyze: Done
        Assert.Equal(ExecutionStageStepState.Done, stages[1].State);   // Plan: Done
        Assert.Equal(ExecutionStageStepState.Done, stages[2].State);   // Approved: Done (guaranteed by execution start invariant)
        Assert.Equal(ExecutionStageStepState.Done, stages[3].State);   // Implement: Done
        Assert.Equal(ExecutionStageStepState.Failed, stages[4].State); // Build & Test: Failed
        Assert.Equal(ExecutionStageStepState.Todo, stages[5].State);   // Review: Todo
        Assert.Equal(ExecutionStageStepState.Todo, stages[6].State);   // PR: Todo

        Assert.Equal(71, progress); // 5 / 7 * 100 = 71%
    }

    [Fact]
    public void EvaluateStages_WhenExecutionStartedAndLaterReviewRejected_ApprovedRemainsDoneAndReviewIsFailed()
    {
        // Scenario: Task was approved, execution ran and completed, then human review was rejected.
        // Even if task status is later updated to Rejected, the historical Approved stage remains Done.
        var task = CreateTask(DevelopmentTaskStatus.Rejected);
        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow,
        };
        var execution = CreateExecution(task.Id, TaskExecutionStatus.Completed, reviewStatus: ExecutionReviewStatus.Rejected);
        var activities = new List<ExecutionActivity>
        {
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow.AddSeconds(1) },
            new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow.AddSeconds(2) }
        };

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, analysis, activities);

        Assert.Equal(ExecutionStageStepState.Done, stages[0].State);   // Analyze: Done
        Assert.Equal(ExecutionStageStepState.Done, stages[1].State);   // Plan: Done
        Assert.Equal(ExecutionStageStepState.Done, stages[2].State);   // Approved: Done (historical approval invariant preserved)
        Assert.Equal(ExecutionStageStepState.Done, stages[3].State);   // Implement: Done
        Assert.Equal(ExecutionStageStepState.Done, stages[4].State);   // Build & Test: Done
        Assert.Equal(ExecutionStageStepState.Failed, stages[5].State); // Review: Failed
        Assert.Equal(ExecutionStageStepState.Todo, stages[6].State);   // PR: Todo
    }

    [Fact]
    public void EvaluateStages_WhenTaskRejectedBeforeExecution_ApprovedIsFailed()
    {
        // Scenario: Plan was generated, but user rejected task at approval gate before execution started.
        var task = CreateTask(DevelopmentTaskStatus.Rejected);
        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow,
        };
        var execution = CreateExecution(task.Id, TaskExecutionStatus.Pending);
        var activities = Array.Empty<ExecutionActivity>();

        var stages = ExecutionStageEvaluator.EvaluateStages(execution, task, analysis, activities);

        Assert.Equal(ExecutionStageStepState.Done, stages[0].State);   // Analyze: Done
        Assert.Equal(ExecutionStageStepState.Done, stages[1].State);   // Plan: Done
        Assert.Equal(ExecutionStageStepState.Failed, stages[2].State); // Approved: Failed (pre-execution rejection)
        Assert.Equal(ExecutionStageStepState.Todo, stages[3].State);   // Implement: Todo
    }
}
