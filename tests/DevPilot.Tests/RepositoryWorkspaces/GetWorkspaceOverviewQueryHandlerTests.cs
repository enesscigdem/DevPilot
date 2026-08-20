using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Queries.GetWorkspaceOverview;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ProjectBrain.Entities;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.RepositoryWorkspaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.RepositoryWorkspaces;

public class GetWorkspaceOverviewQueryHandlerTests : IDisposable
{
    private readonly DevPilotDbContext _db;
    private readonly EfWorkspaceOverviewReader _reader;
    private readonly GetWorkspaceOverviewQueryHandler _handler;

    public GetWorkspaceOverviewQueryHandlerTests()
    {
        var options = new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase("WorkspaceOverviewTests_" + Guid.NewGuid().ToString("N"))
            .Options;

        _db = new DevPilotDbContext(options);
        _reader = new EfWorkspaceOverviewReader(_db, NullLogger<EfWorkspaceOverviewReader>.Instance);
        _handler = new GetWorkspaceOverviewQueryHandler(_reader, NullLogger<GetWorkspaceOverviewQueryHandler>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task HandleAsync_NonExistentWorkspace_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(Guid.NewGuid()));

        result.Success.Should().BeFalse();
        result.NotFound.Should().BeTrue();
        result.Overview.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_EmptyWorkspace_ReturnsCleanEmptyOverviewWithoutExceptions()
    {
        var workspace = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "enesscigdem",
            Repository = "DevPilot",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(workspace);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(workspace.Id));

        result.Success.Should().BeTrue();
        result.NotFound.Should().BeFalse();
        result.Overview.Should().NotBeNull();

        var o = result.Overview!;
        o.Header.WorkspaceId.Should().Be(workspace.Id);
        o.Header.RepositoryFullName.Should().Be("enesscigdem/DevPilot");
        o.Header.Branch.Should().Be("main");
        o.Header.FileCount.Should().Be(0);
        o.Header.LastIndexedAt.Should().BeNull();
        o.Header.IsIndexed.Should().BeFalse();

        o.ActiveExecution.Should().BeNull();
        o.NeedsAttention.Should().BeEmpty();
        o.AwaitingApproval.Should().BeEmpty();
        o.FailedOrBlocked.Should().BeEmpty();
        o.RecentActivity.Should().BeEmpty();
        o.ShippedRecently.Should().BeEmpty();

        o.RecentlyAnalyzed.RepositoryFullName.Should().Be("enesscigdem/DevPilot");
        o.RecentlyAnalyzed.SymbolsCount.Should().Be(0);
        o.RecentlyAnalyzed.TypesCount.Should().Be(0);
        o.RecentlyAnalyzed.ReferencesCount.Should().BeNull("References resolution is not persisted cheaply");
        o.RecentlyAnalyzed.Loc.Should().BeNull("LOC is not persisted cheaply");
    }

    [Fact]
    public async Task HandleAsync_CompleteWorkspaceScoping_NeverLeaksWorkspaceADataToWorkspaceB()
    {
        var wsA = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "orgA",
            Repository = "repoA",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var wsB = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "orgB",
            Repository = "repoB",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.AddRange(wsA, wsB);

        // Task & execution for Workspace A
        var taskA = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = wsA.Id,
            Title = "Task in Workspace A",
            Status = DevelopmentTaskStatus.Executing,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var execA = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskA.Id,
            Status = TaskExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        _db.DevelopmentTasks.Add(taskA);
        _db.TaskExecutions.Add(execA);

        // Task & execution for Workspace B
        var taskB = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = wsB.Id,
            Title = "Task in Workspace B",
            Status = DevelopmentTaskStatus.Failed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var execB = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = taskB.Id,
            Status = TaskExecutionStatus.Failed,
            ErrorMessage = "Failure in Workspace B",
            CreatedAt = DateTime.UtcNow,
        };
        _db.DevelopmentTasks.Add(taskB);
        _db.TaskExecutions.Add(execB);

        // Chunks for A
        _db.CodeChunks.Add(new CodeChunk
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = wsA.Id,
            WorkspacePath = "/tmp/a",
            WorkspaceName = "repoA",
            RelativePath = "FileA.cs",
            Language = "C#",
            TypeName = "ServiceA",
            SymbolName = "MethodA",
            DeclaredSymbols = "MethodA",
            ContentHash = "hashA",
            ChunkOrder = 0,
        });

        await _db.SaveChangesAsync();

        // Query Workspace A
        var resA = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(wsA.Id));
        resA.Success.Should().BeTrue();
        var ovA = resA.Overview!;
        ovA.Header.RepositoryFullName.Should().Be("orgA/repoA");
        ovA.ActiveExecution.Should().NotBeNull();
        ovA.ActiveExecution!.TaskTitle.Should().Be("Task in Workspace A");
        ovA.FailedOrBlocked.Should().BeEmpty("Task B belongs to Workspace B, not A");
        ovA.RecentlyAnalyzed.SymbolsCount.Should().Be(1);

        // Query Workspace B
        var resB = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(wsB.Id));
        resB.Success.Should().BeTrue();
        var ovB = resB.Overview!;
        ovB.Header.RepositoryFullName.Should().Be("orgB/repoB");
        ovB.ActiveExecution.Should().BeNull("No running execution in B");
        ovB.FailedOrBlocked.Should().HaveCount(1);
        ovB.FailedOrBlocked[0].Title.Should().Be("Task in Workspace B");
        ovB.RecentlyAnalyzed.SymbolsCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_ActiveExecutionSelection_PrefersRunningOverPending()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task1 = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Pending Task 1",
            Status = DevelopmentTaskStatus.Approved,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        var execPending = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task1.Id,
            Status = TaskExecutionStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        };

        var task2 = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Running Task 2",
            Status = DevelopmentTaskStatus.Executing,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var execRunning = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task2.Id,
            Status = TaskExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-4),
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        };

        _db.DevelopmentTasks.AddRange(task1, task2);
        _db.TaskExecutions.AddRange(execPending, execRunning);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        result.Overview!.ActiveExecution.Should().NotBeNull();
        result.Overview.ActiveExecution!.TaskId.Should().Be(task2.Id);
        result.Overview.ActiveExecution.TaskTitle.Should().Be("Running Task 2");
        result.Overview.ActiveExecution.ElapsedSeconds.Should().BeGreaterThan(0);
        result.Overview.ActiveExecution.TokensUsed.Should().BeNull();
        result.Overview.ActiveExecution.EstimatedCost.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ActiveExecutionStages_MapsRealEvidenceCorrectly()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Feature Task",
            Status = DevelopmentTaskStatus.Executing,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow,
        };
        _db.DevelopmentTasks.Add(task);

        // Completed Impact Analysis with 4 impacted files
        var analysis = new TaskImpactAnalysis
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Completed,
            Summary = "Plan summary",
            Confidence = 90,
            StructuredResult = new ImpactAnalysisResultData
            {
                Summary = "Plan summary",
                ImpactedFiles = new List<ImpactedFile>
                {
                    new() { FilePath = "A.cs", ChangeType = ImpactFileChangeType.Modify },
                    new() { FilePath = "B.cs", ChangeType = ImpactFileChangeType.Add },
                },
                ProposedPlan = new List<ProposedPlanStep>
                {
                    new() { Order = 1, Title = "Step 1" }
                }
            },
            CreatedAt = DateTime.UtcNow.AddMinutes(-50),
            CompletedAt = DateTime.UtcNow.AddMinutes(-48),
        };
        _db.TaskImpactAnalyses.Add(analysis);

        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
        };
        _db.TaskExecutions.Add(execution);

        // Activities: Developer agent completed, Build passed, Test started
        _db.ExecutionActivities.AddRange(
            new ExecutionActivity
            {
                Id = Guid.NewGuid(),
                ExecutionId = execution.Id,
                Stage = ExecutionStage.DeveloperAgent,
                Status = ExecutionActivityStatus.Completed,
                Message = "Developer agent completed",
                MetadataJson = "{\"ModifiedFileCount\": 2}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            },
            new ExecutionActivity
            {
                Id = Guid.NewGuid(),
                ExecutionId = execution.Id,
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Completed,
                Message = "dotnet build succeeded",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            },
            new ExecutionActivity
            {
                Id = Guid.NewGuid(),
                ExecutionId = execution.Id,
                Stage = ExecutionStage.Test,
                Status = ExecutionActivityStatus.Started,
                Message = "dotnet test started",
                CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            }
        );
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        var execDto = result.Overview!.ActiveExecution!;

        execDto.ModifiedFileCount.Should().Be(2);

        // Stages check:
        // 0: analyze -> Done
        // 1: plan -> Done
        // 2: approved -> Done
        // 3: implement -> Done
        // 4: build -> Active (Build passed, Test started)
        // 5: review -> Todo
        // 6: pr -> Todo
        execDto.Stages[0].State.Should().Be(WorkspaceStageState.Done);
        execDto.Stages[1].State.Should().Be(WorkspaceStageState.Done);
        execDto.Stages[2].State.Should().Be(WorkspaceStageState.Done);
        execDto.Stages[3].State.Should().Be(WorkspaceStageState.Done);
        execDto.Stages[4].State.Should().Be(WorkspaceStageState.Active);
        execDto.Stages[5].State.Should().Be(WorkspaceStageState.Todo);
        execDto.Stages[6].State.Should().Be(WorkspaceStageState.Todo);
        execDto.CurrentStageKey.Should().Be("build");
    }

    [Fact]
    public async Task HandleAsync_AttentionItems_PrioritizesAndCapsAt3()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        // 1. Failed execution
        var t1 = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Failed Task", Status = DevelopmentTaskStatus.Failed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var e1 = new TaskExecution { Id = Guid.NewGuid(), DevelopmentTaskId = t1.Id, Status = TaskExecutionStatus.Failed, ErrorMessage = "Compilation error in C:\\secret\\path\\File.cs", CreatedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow.AddMinutes(-4) };

        // 2. Review pending
        var t2 = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Review Task", Status = DevelopmentTaskStatus.Executing, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var e2 = new TaskExecution { Id = Guid.NewGuid(), DevelopmentTaskId = t2.Id, Status = TaskExecutionStatus.Completed, ReviewStatus = ExecutionReviewStatus.Pending, CreatedAt = DateTime.UtcNow.AddMinutes(-3), CompletedAt = DateTime.UtcNow.AddMinutes(-2) };

        // 3. Plan approval required
        var t3 = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Approval Task", Status = DevelopmentTaskStatus.AwaitingApproval, CreatedAt = DateTime.UtcNow.AddMinutes(-1), UpdatedAt = DateTime.UtcNow.AddMinutes(-1) };

        // 4. Rejected task (4th item)
        var t4 = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Rejected Task", Status = DevelopmentTaskStatus.Rejected, CreatedAt = DateTime.UtcNow.AddMinutes(-20), UpdatedAt = DateTime.UtcNow.AddMinutes(-15) };

        _db.DevelopmentTasks.AddRange(t1, t2, t3, t4);
        _db.TaskExecutions.AddRange(e1, e2);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        var attention = result.Overview!.NeedsAttention;

        attention.Should().HaveCount(3, "Needs attention caps at 3 items max");
        attention.Select(a => a.Kind).Should().Contain(new[]
        {
            WorkspaceAttentionKind.ExecutionFailed,
            WorkspaceAttentionKind.ReviewPending,
            WorkspaceAttentionKind.PlanApprovalRequired
        });

        // Verify error sanitization
        var failedItem = attention.First(a => a.Kind == WorkspaceAttentionKind.ExecutionFailed);
        failedItem.Reason.Should().NotContain("C:\\secret\\path");
    }

    [Fact]
    public async Task HandleAsync_AttentionItems_PresentsSpecificFailureKindAndTitle()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Compiler Error Task",
            Status = DevelopmentTaskStatus.Failed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Failed,
            ErrorMessage = "C:\\secret\\path\\build.log error",
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        _db.ExecutionActivities.Add(new ExecutionActivity
        {
            Id = Guid.NewGuid(),
            ExecutionId = execution.Id,
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Failed,
            Message = "3 compilation errors in OrderService.cs",
            CreatedAt = DateTime.UtcNow,
        });

        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(execution);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        var item = result.Overview!.NeedsAttention.Should().ContainSingle().Subject;

        item.Kind.Should().Be(WorkspaceAttentionKind.BuildFailed);
        item.Title.Should().Be("Build failed");
        item.MetaDetail.Should().Be("3 compilation errors in OrderService.cs");

        // Verify meaningful numeric content in activity actions is preserved
        var act = result.Overview.RecentActivity.Should().ContainSingle().Subject;
        act.Action.Should().Be("3 compilation errors in OrderService.cs");
    }

    [Fact]
    public async Task HandleAsync_FailedOrBlocked_DeduplicatesPerTask()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Broken Task",
            Status = DevelopmentTaskStatus.Failed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Failed,
            ErrorMessage = "dotnet build failed at /home/user/src/Proj.cs",
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        _db.ExecutionActivities.Add(new ExecutionActivity
        {
            Id = Guid.NewGuid(),
            ExecutionId = execution.Id,
            Stage = ExecutionStage.Build,
            Status = ExecutionActivityStatus.Failed,
            Message = "Build failed on missing reference",
            CreatedAt = DateTime.UtcNow,
        });

        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(execution);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        var trouble = result.Overview!.FailedOrBlocked;

        trouble.Should().HaveCount(1, "Should deduplicate to exactly one entry per task/execution problem");
        trouble[0].Kind.Should().Be(WorkspaceFailureKind.BuildFailed);
        trouble[0].Summary.Should().Be("Build failed on missing reference");
    }

    [Fact]
    public async Task HandleAsync_ShippedRecently_OnlyIncludesGenuinelyMergedWork()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        // 1. Merged execution (Shipped)
        var t1 = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Shipped Feature", Status = DevelopmentTaskStatus.Completed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var e1 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = t1.Id,
            Status = TaskExecutionStatus.Completed,
            MergeStatus = ExecutionMergeStatus.Merged,
            MergeCommitSha = "abcdef123456",
            MergedAt = DateTime.UtcNow.AddHours(-2),
            PullRequestNumber = 412,
            CreatedAt = DateTime.UtcNow.AddHours(-3),
        };

        // 2. Open PR (Not shipped yet)
        var t2 = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Open PR Feature", Status = DevelopmentTaskStatus.Executing, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var e2 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = t2.Id,
            Status = TaskExecutionStatus.Completed,
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            PullRequestNumber = 413,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };

        // 3. Approved Review (Not shipped yet)
        var t3 = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Approved Review Feature", Status = DevelopmentTaskStatus.Approved, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var e3 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = t3.Id,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };

        _db.DevelopmentTasks.AddRange(t1, t2, t3);
        _db.TaskExecutions.AddRange(e1, e2, e3);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        var shipped = result.Overview!.ShippedRecently;

        shipped.Should().HaveCount(1, "Only genuinely merged executions count as shipped");
        shipped[0].Title.Should().Be("Shipped Feature");
        shipped[0].PullRequestNumber.Should().Be(412);
        shipped[0].MergeCommitSha.Should().Be("abcdef123456");
        shipped[0].MergedAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(-2), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HandleAsync_RecentActivity_UsesOnlyPersistedEvents()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Order Filter", Status = DevelopmentTaskStatus.Executing, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var exec = new TaskExecution { Id = Guid.NewGuid(), DevelopmentTaskId = task.Id, Status = TaskExecutionStatus.Running, CreatedAt = DateTime.UtcNow };

        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(exec);

        _db.ExecutionActivities.AddRange(
            new ExecutionActivity
            {
                Id = Guid.NewGuid(),
                ExecutionId = exec.Id,
                Stage = ExecutionStage.DeveloperAgent,
                Status = ExecutionActivityStatus.Completed,
                Message = "Developer edited OrderRepository.cs",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            },
            new ExecutionActivity
            {
                Id = Guid.NewGuid(),
                ExecutionId = exec.Id,
                Stage = ExecutionStage.Build,
                Status = ExecutionActivityStatus.Completed,
                Message = "dotnet build succeeded",
                CreatedAt = DateTime.UtcNow.AddMinutes(-3),
            }
        );
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        var activity = result.Overview!.RecentActivity;

        activity.Should().HaveCount(2);
        activity[0].OccurredAt.Should().BeAfter(activity[1].OccurredAt, "Sorted latest first");
        activity[0].Actor.Should().Be(WorkspaceActivityActor.System);
        activity[1].Actor.Should().Be(WorkspaceActivityActor.Developer);
    }

    [Fact]
    public async Task HandleAsync_ActiveExecutionSelection_IncludesNonTerminalPostExecutionStates()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        // Execution completed, but Review is Pending (non-terminal active workflow)
        var task = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "Review Pending Task", Status = DevelopmentTaskStatus.Executing, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var exec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        _db.ExecutionActivities.AddRange(
            new ExecutionActivity { Id = Guid.NewGuid(), ExecutionId = exec.Id, Stage = ExecutionStage.DeveloperAgent, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow.AddMinutes(-8) },
            new ExecutionActivity { Id = Guid.NewGuid(), ExecutionId = exec.Id, Stage = ExecutionStage.Build, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow.AddMinutes(-7) },
            new ExecutionActivity { Id = Guid.NewGuid(), ExecutionId = exec.Id, Stage = ExecutionStage.Test, Status = ExecutionActivityStatus.Completed, CreatedAt = DateTime.UtcNow.AddMinutes(-6) }
        );
        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(exec);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        var active = result.Overview!.ActiveExecution;

        active.Should().NotBeNull("Non-terminal completed execution with review pending must remain active");
        active!.TaskTitle.Should().Be("Review Pending Task");
        active.Stages[5].State.Should().Be(WorkspaceStageState.Active, "Review stage is active");
        active.CurrentStageKey.Should().Be("review");
    }

    [Fact]
    public async Task HandleAsync_ActiveExecutionSelection_ApprovedReviewWithOpenPr_IsActiveUntilMerged()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task = new DevelopmentTask { Id = Guid.NewGuid(), RepositoryWorkspaceId = ws.Id, Title = "PR Open Task", Status = DevelopmentTaskStatus.Approved, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var exec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            PullRequestNumber = 501,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            CompletedAt = DateTime.UtcNow.AddMinutes(-15),
            ReviewDecidedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(exec);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        var active = result.Overview!.ActiveExecution;

        active.Should().NotBeNull("PR open workflow must remain active until merged");
        active!.Stages[5].State.Should().Be(WorkspaceStageState.Done, "Review is done");
        active.Stages[6].State.Should().Be(WorkspaceStageState.Done, "Pull Request creation is done");
        active.CurrentStageKey.Should().Be("pr");
    }

    [Fact]
    public async Task HandleAsync_ActiveAgentExecution_RunningExecution_ReturnsActiveAgentExecution()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Active Developer Task",
            Status = DevelopmentTaskStatus.Executing,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var exec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        };
        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(exec);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();

        result.Overview!.ActiveAgentExecution.Should().NotBeNull();
        result.Overview.ActiveAgentExecution!.TaskId.Should().Be(task.Id);
        result.Overview.ActiveAgentExecution.TaskTitle.Should().Be("Active Developer Task");
        result.Overview.ActiveExecution.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_ActiveAgentExecution_FailedExecution_BecomesNullAgentIdle()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Failed Task",
            Status = DevelopmentTaskStatus.Failed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var exec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Failed,
            ErrorMessage = "Kimi API returned status code 503.",
            StartedAt = DateTime.UtcNow.AddMinutes(-6),
            CompletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow.AddMinutes(-6),
        };
        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(exec);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();

        result.Overview!.ActiveAgentExecution.Should().BeNull("Failed executions must not appear as ActiveAgentExecution");
        result.Overview.ActiveExecution.Should().BeNull();
        result.Overview.FailedOrBlocked.Should().HaveCount(1);
    }

    [Fact]
    public async Task HandleAsync_ActiveAgentExecution_OldPrOpenWithNoPendingRunning_ReturnsNullAgentIdleWhileWorkflowIsActive()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var oldTask = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Old Delivery Feature",
            Status = DevelopmentTaskStatus.Approved,
            CreatedAt = DateTime.UtcNow.AddHours(-22),
            UpdatedAt = DateTime.UtcNow.AddHours(-22),
        };
        var oldExec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = oldTask.Id,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            PullRequestNumber = 99,
            CreatedAt = DateTime.UtcNow.AddHours(-22),
            CompletedAt = DateTime.UtcNow.AddHours(-21),
            ReviewDecidedAt = DateTime.UtcNow.AddHours(-20),
        };
        _db.DevelopmentTasks.Add(oldTask);
        _db.TaskExecutions.Add(oldExec);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();

        result.Overview!.ActiveAgentExecution.Should().BeNull("Old PR Open has no running agent, so ActiveAgentExecution must be null (Agent idle)");
        result.Overview.ActiveExecution.Should().NotBeNull("Workspace overview dashboard still retains active workflow until PR is merged");
        result.Overview.ActiveExecution!.TaskTitle.Should().Be("Old Delivery Feature");
        result.Overview.ActiveExecution.CurrentStageKey.Should().Be("pr");
    }

    [Fact]
    public async Task HandleAsync_ActiveAgentExecution_OldPrOpenPlusNewerRunning_PrefersNewerRunningExecution()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var oldTask = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Old PR Task",
            Status = DevelopmentTaskStatus.Approved,
            CreatedAt = DateTime.UtcNow.AddHours(-22),
            UpdatedAt = DateTime.UtcNow.AddHours(-22),
        };
        var oldExec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = oldTask.Id,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            PullRequestNumber = 99,
            CreatedAt = DateTime.UtcNow.AddHours(-22),
            CompletedAt = DateTime.UtcNow.AddHours(-21),
        };

        var newTask = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "New Running Task",
            Status = DevelopmentTaskStatus.Executing,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
        };
        var newExec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = newTask.Id,
            Status = TaskExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        };

        _db.DevelopmentTasks.AddRange(oldTask, newTask);
        _db.TaskExecutions.AddRange(oldExec, newExec);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();

        result.Overview!.ActiveAgentExecution.Should().NotBeNull();
        result.Overview.ActiveAgentExecution!.TaskId.Should().Be(newTask.Id);
        result.Overview.ActiveAgentExecution.TaskTitle.Should().Be("New Running Task");
        result.Overview.ActiveExecution!.TaskId.Should().Be(newTask.Id);
    }

    [Fact]
    public async Task HandleAsync_ActiveAgentExecution_NeverSelectsCompletedOrFailedOrCancelled()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task1 = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Completed Task",
            Status = DevelopmentTaskStatus.Completed,
            CreatedAt = DateTime.UtcNow.AddHours(-10),
            UpdatedAt = DateTime.UtcNow.AddHours(-9),
        };
        var exec1 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task1.Id,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Approved,
            PullRequestStatus = ExecutionPullRequestStatus.Open,
            CreatedAt = DateTime.UtcNow.AddHours(-10),
            StartedAt = DateTime.UtcNow.AddHours(-10),
            CompletedAt = DateTime.UtcNow.AddHours(-9),
        };

        var task2 = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Failed Task",
            Status = DevelopmentTaskStatus.Failed,
            CreatedAt = DateTime.UtcNow.AddHours(-5),
            UpdatedAt = DateTime.UtcNow.AddHours(-4),
        };
        var exec2 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task2.Id,
            Status = TaskExecutionStatus.Failed,
            CreatedAt = DateTime.UtcNow.AddHours(-5),
            StartedAt = DateTime.UtcNow.AddHours(-5),
            CompletedAt = DateTime.UtcNow.AddHours(-4),
        };

        var task3 = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Cancelled Task",
            Status = DevelopmentTaskStatus.Approved,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
        };
        var exec3 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task3.Id,
            Status = TaskExecutionStatus.Cancelled,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            StartedAt = DateTime.UtcNow.AddHours(-2),
            CompletedAt = DateTime.UtcNow.AddHours(-1),
        };

        _db.DevelopmentTasks.AddRange(task1, task2, task3);
        _db.TaskExecutions.AddRange(exec1, exec2, exec3);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();
        result.Overview!.ActiveAgentExecution.Should().BeNull("All executions are terminal (completed, failed, cancelled)");
    }

    [Fact]
    public async Task HandleAsync_CompletedExecutionAwaitingReview_AppearsInAwaitingApproval_AndActiveAgentExecutionIsNull()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Awaiting Code Review Task",
            Status = DevelopmentTaskStatus.Executing,
            CreatedAt = DateTime.UtcNow.AddHours(-67),
            UpdatedAt = DateTime.UtcNow.AddHours(-66),
        };
        var exec = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = TaskExecutionStatus.Completed,
            ReviewStatus = ExecutionReviewStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddHours(-67),
            StartedAt = DateTime.UtcNow.AddHours(-67),
            CompletedAt = DateTime.UtcNow.AddHours(-66),
        };

        _db.DevelopmentTasks.Add(task);
        _db.TaskExecutions.Add(exec);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();

        // ActiveAgentExecution must be null because no agent is currently running
        result.Overview!.ActiveAgentExecution.Should().BeNull();

        // ActiveExecution (workflow) contains the completed execution
        result.Overview.ActiveExecution.Should().NotBeNull();
        result.Overview.ActiveExecution!.ElapsedSeconds.Should().Be((int)TimeSpan.FromHours(1).TotalSeconds);

        // AwaitingApproval must contain the code review item
        result.Overview.AwaitingApproval.Should().ContainSingle(a => a.ExecutionId == exec.Id && a.Kind == WorkspaceApprovalKind.CodeReviewApproval);
    }

    [Fact]
    public async Task HandleAsync_ActiveAgentExecution_OrdersMultipleRunningTasksDeterministically()
    {
        var ws = new RepositoryWorkspace
        {
            Id = Guid.NewGuid(),
            Owner = "owner",
            Repository = "repo",
            Branch = "main",
            Status = RepositoryWorkspaceStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RepositoryWorkspaces.Add(ws);

        var task1 = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Running Task 1 (Started Earlier)",
            Status = DevelopmentTaskStatus.Executing,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-20),
        };
        var exec1 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task1.Id,
            Status = TaskExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-18),
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
        };

        var task2 = new DevelopmentTask
        {
            Id = Guid.NewGuid(),
            RepositoryWorkspaceId = ws.Id,
            Title = "Running Task 2 (Started Later)",
            Status = DevelopmentTaskStatus.Executing,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        var exec2 = new TaskExecution
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task2.Id,
            Status = TaskExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-8),
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        };

        _db.DevelopmentTasks.AddRange(task1, task2);
        _db.TaskExecutions.AddRange(exec1, exec2);
        await _db.SaveChangesAsync();

        var result = await _handler.HandleAsync(new GetWorkspaceOverviewQuery(ws.Id));
        result.Success.Should().BeTrue();

        result.Overview!.ActiveAgentExecution.Should().NotBeNull();
        result.Overview.ActiveAgentExecution!.TaskId.Should().Be(task2.Id, "Latest StartedAt takes precedence deterministically");
    }
}
