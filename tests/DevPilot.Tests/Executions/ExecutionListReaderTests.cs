using DevPilot.Application.Executions.Dtos;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Executions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevPilot.Tests.Executions;

public sealed class ExecutionListReaderTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly EfExecutionListReader _reader;

    public ExecutionListReaderTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("ExecutionListReaderTests_" + Guid.NewGuid().ToString("N"))
            .Options;

        _db = new DevPilotDbContext(options);
        _reader = new EfExecutionListReader(_db);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task ReadExecutionsListAsync_WhenWorkspaceSpecified_OnlyReturnsWorkspaceExecutions()
    {
        var wsA = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "ownerA",
            Repository = "repoA",
            LocalPath = "/ws/a",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var wsB = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "ownerB",
            Repository = "repoB",
            LocalPath = "/ws/b",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var taskA = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = wsA.Id,
            RepositoryWorkspace = wsA,
            Title = "Task A",
            Description = "Desc A",
            Status = DevelopmentTaskStatus.Completed,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var taskB = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = wsB.Id,
            RepositoryWorkspace = wsB,
            Title = "Task B",
            Description = "Desc B",
            Status = DevelopmentTaskStatus.Draft,
            Priority = DevelopmentTaskPriority.Low,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var execA = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskA.Id,
            DevelopmentTask = taskA,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };

        var execB = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskB.Id,
            DevelopmentTask = taskB,
            Status = TaskExecutionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        _db.RepositoryWorkspaces.AddRange(wsA, wsB);
        _db.DevelopmentTasks.AddRange(taskA, taskB);
        _db.TaskExecutions.AddRange(execA, execB);
        await _db.SaveChangesAsync();

        var resultA = await _reader.ReadExecutionsListAsync(wsA.Id);

        resultA.Should().HaveCount(1);
        resultA[0].Id.Should().Be(execA.Id);
        resultA[0].TaskTitle.Should().Be("Task A");
        resultA[0].RepositoryName.Should().Be("repoA");

        var resultB = await _reader.ReadExecutionsListAsync(wsB.Id);
        resultB.Should().HaveCount(1);
        resultB[0].Id.Should().Be(execB.Id);

        var resultAll = await _reader.ReadExecutionsListAsync(null);
        resultAll.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadExecutionsListAsync_DerivesTruthfulStagesAndDeterministicProgress()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "testorg",
            Repository = "testrepo",
            LocalPath = "/ws/test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            RepositoryWorkspace = ws,
            Title = "Task with full evidence",
            Description = "Desc",
            Status = DevelopmentTaskStatus.Executing,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var analysis = new DevPilot.Domain.Entities.TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            StructuredResult = new ImpactAnalysisResultData { Summary = "Plan" },
            CreatedAt = DateTime.UtcNow,
        };

        var exec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Running,
            ReviewStatus = ExecutionReviewStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        var act1 = new ExecutionActivity
        {
            Id = Guid.NewGuid(),
            ExecutionId = exec.Id,
            Stage = ExecutionStage.DeveloperAgent,
            Status = ExecutionActivityStatus.Started,
            CreatedAt = DateTime.UtcNow,
            Message = "DevAgent started"
        };

        _db.RepositoryWorkspaces.Add(ws);
        _db.DevelopmentTasks.Add(task);
        _db.TaskImpactAnalyses.Add(analysis);
        _db.TaskExecutions.Add(exec);
        _db.ExecutionActivities.Add(act1);
        await _db.SaveChangesAsync();

        var list = await _reader.ReadExecutionsListAsync(ws.Id);

        list.Should().HaveCount(1);
        var item = list[0];
        item.Stages.Should().HaveCount(7);
        item.Stages[0].State.Should().Be(ExecutionStageStepState.Done);   // Analyze
        item.Stages[1].State.Should().Be(ExecutionStageStepState.Done);   // Plan
        item.Stages[2].State.Should().Be(ExecutionStageStepState.Done);   // Approved
        item.Stages[3].State.Should().Be(ExecutionStageStepState.Active); // Implement
        item.Stages[4].State.Should().Be(ExecutionStageStepState.Todo);   // Build
        item.Stages[5].State.Should().Be(ExecutionStageStepState.Todo);   // Review
        item.Stages[6].State.Should().Be(ExecutionStageStepState.Todo);   // PR

        item.ProgressPercentage.Should().Be(50); // (3.5 / 7) * 100 = 50%
    }

    [Fact]
    public async Task ReadExecutionsListAsync_WhenLegacyIncompleteExecution_DoesNotAssumeGreenStages()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "testorg",
            Repository = "testrepo",
            LocalPath = "/ws/test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            RepositoryWorkspace = ws,
            Title = "Legacy incomplete task",
            Description = "Desc",
            Status = DevelopmentTaskStatus.Draft,
            Priority = DevelopmentTaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var exec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            DevelopmentTask = task,
            Status = TaskExecutionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        _db.RepositoryWorkspaces.Add(ws);
        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(exec);
        await _db.SaveChangesAsync();

        var list = await _reader.ReadExecutionsListAsync(ws.Id);

        list.Should().HaveCount(1);
        var item = list[0];
        item.Stages.Should().HaveCount(7);
        item.Stages.Should().OnlyContain(s => s.State == ExecutionStageStepState.Todo);
        item.ProgressPercentage.Should().Be(0);
    }
}
