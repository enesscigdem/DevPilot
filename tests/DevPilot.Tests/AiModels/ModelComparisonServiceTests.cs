using DevPilot.Application.Executions.Ports;
using DevPilot.Application.ModelComparisons;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ValueObjects;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Executions;
using DevPilot.Infrastructure.ImpactAnalysis;
using DevPilot.Infrastructure.ModelComparisons;
using DevPilot.Infrastructure.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.AiModels;

public class ModelComparisonServiceTests
{
    private sealed class Fixture
    {
        public DevPilotDbContext Db { get; } = new(new DbContextOptionsBuilder<DevPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

        public RecordingDispatcher Dispatcher { get; } = new();

        public ModelComparisonService Service { get; }

        public DevelopmentTask Task { get; }

        public AiModelConfig ModelA { get; } = NewModel("Model A");

        public AiModelConfig ModelB { get; } = NewModel("Model B");

        public AiModelConfig ModelC { get; } = NewModel("Model C");

        public Fixture(DevelopmentTaskStatus taskStatus = DevelopmentTaskStatus.Approved, bool withAnalysis = true)
        {
            var workspace = new RepositoryWorkspace { Id = Guid.NewGuid(), Owner = "o", Repository = "r", Branch = "main" };
            Task = new DevelopmentTask
            {
                Id = Guid.NewGuid(),
                RepositoryWorkspaceId = workspace.Id,
                RepositoryWorkspace = workspace,
                Title = "Add rate limiting",
                Status = taskStatus,
            };

            Db.RepositoryWorkspaces.Add(workspace);
            Db.DevelopmentTasks.Add(Task);
            Db.AiModelConfigs.AddRange(ModelA, ModelB, ModelC);
            if (withAnalysis)
            {
                Db.TaskImpactAnalyses.Add(new TaskImpactAnalysis
                {
                    Id = Guid.NewGuid(),
                    DevelopmentTaskId = Task.Id,
                    Status = ImpactAnalysisStatus.Completed,
                    CreatedAt = DateTime.UtcNow,
                    StructuredResult = new ImpactAnalysisResultData(),
                });
            }

            Db.SaveChanges();

            Service = new ModelComparisonService(
                Db,
                new EfTaskRepository(Db),
                new EfImpactAnalysisRepository(Db),
                new EfExecutionRepository(Db),
                Dispatcher,
                NullLogger<ModelComparisonService>.Instance);
        }

        public StartModelComparisonRequest Request(params AiModelConfig[] models) =>
            new() { TaskId = Task.Id, ModelIds = models.Select(m => m.Id).ToList() };

        /// <summary>Simulates the worker finishing the active execution.</summary>
        public async System.Threading.Tasks.Task FinishActiveExecutionAsync(TaskExecutionStatus status = TaskExecutionStatus.Completed)
        {
            var active = await Db.TaskExecutions.SingleAsync(e => e.Status == TaskExecutionStatus.Pending || e.Status == TaskExecutionStatus.Running);
            active.Status = status;
            active.CompletedAt = DateTime.UtcNow;

            // The real processor also moves the task out of Executing when it finishes.
            var task = await Db.DevelopmentTasks.SingleAsync(t => t.Id == active.DevelopmentTaskId);
            task.Status = status == TaskExecutionStatus.Completed ? DevelopmentTaskStatus.Completed : DevelopmentTaskStatus.Failed;
            await Db.SaveChangesAsync();
        }
    }

    private static AiModelConfig NewModel(string name, bool enabled = true) =>
        new() { Id = Guid.NewGuid(), Name = name, ModelName = name.ToLowerInvariant(), BaseUrl = "https://x.example", IsEnabled = enabled };

    private sealed class RecordingDispatcher : IExecutionDispatcher
    {
        public List<Guid> Enqueued { get; } = new();

        public void EnqueueProcessExecution(Guid executionId) => Enqueued.Add(executionId);
    }

    [Fact]
    public async Task Start_BeginsTheFirstRunRightAway_PinnedToItsModel()
    {
        var f = new Fixture();

        var comparison = await f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);

        comparison.Status.Should().Be(ModelComparisonStatus.Running);
        comparison.Runs.Select(r => r.State).Should().Equal(ModelComparisonRunState.Running, ModelComparisonRunState.Queued);

        var execution = await f.Db.TaskExecutions.SingleAsync();
        execution.PinnedAiModelId.Should().Be(f.ModelA.Id);
        execution.PinnedAiModelName.Should().Be("Model A");
        execution.Status.Should().Be(TaskExecutionStatus.Pending);
        f.Dispatcher.Enqueued.Should().ContainSingle().Which.Should().Be(execution.Id);
        (await f.Db.DevelopmentTasks.SingleAsync()).Status.Should().Be(DevelopmentTaskStatus.Executing);
    }

    [Fact]
    public async Task NextRun_WaitsUntilThePreviousExecutionHasEnded()
    {
        var f = new Fixture();
        await f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);

        await f.Service.AdvanceAllAsync(CancellationToken.None);

        (await f.Db.TaskExecutions.CountAsync()).Should().Be(1, "only one execution may be active per task");
    }

    [Fact]
    public async Task RunsFollowEachOther_InOrder_UntilTheComparisonIsComplete()
    {
        var f = new Fixture();
        var started = await f.Service.StartAsync(f.Request(f.ModelA, f.ModelB, f.ModelC), CancellationToken.None);

        await f.FinishActiveExecutionAsync();
        await f.Service.AdvanceAllAsync(CancellationToken.None);
        (await f.Db.TaskExecutions.OrderBy(e => e.CreatedAt).Select(e => e.PinnedAiModelName).ToListAsync())
            .Should().Equal("Model A", "Model B");

        await f.FinishActiveExecutionAsync();
        await f.Service.AdvanceAllAsync(CancellationToken.None);
        (await f.Db.TaskExecutions.CountAsync()).Should().Be(3);

        await f.FinishActiveExecutionAsync();
        await f.Service.AdvanceAllAsync(CancellationToken.None);
        (await f.Db.TaskExecutions.CountAsync()).Should().Be(3, "nothing is left to start");

        var done = await f.Service.GetAsync(started.Id, CancellationToken.None);
        done.Status.Should().Be(ModelComparisonStatus.Completed);
        done.Runs.Should().OnlyContain(r => r.State == ModelComparisonRunState.Finished && r.ExecutionId != null);
        f.Dispatcher.Enqueued.Should().HaveCount(3);
    }

    [Fact]
    public async Task AFailedRun_DoesNotStopTheRemainingModels()
    {
        var f = new Fixture();
        await f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);

        await f.FinishActiveExecutionAsync(TaskExecutionStatus.Failed);
        await f.Service.AdvanceAllAsync(CancellationToken.None);

        (await f.Db.TaskExecutions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Cancel_SkipsRunsThatHaveNotStarted()
    {
        var f = new Fixture();
        var started = await f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);

        var cancelled = await f.Service.CancelAsync(started.Id, CancellationToken.None);
        await f.FinishActiveExecutionAsync();
        await f.Service.AdvanceAllAsync(CancellationToken.None);

        cancelled.Runs[1].State.Should().Be(ModelComparisonRunState.Skipped);
        (await f.Db.TaskExecutions.CountAsync()).Should().Be(1, "the cancelled comparison must not start more runs");
        (await f.Service.GetAsync(started.Id, CancellationToken.None)).Status.Should().Be(ModelComparisonStatus.Cancelled);
    }

    [Fact]
    public async Task OnlyOneComparisonCanBeOpenPerTask()
    {
        var f = new Fixture();
        await f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);

        var act = () => f.Service.StartAsync(f.Request(f.ModelB, f.ModelC), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ANewComparisonIsAllowed_OnceThePreviousOneIsDone()
    {
        var f = new Fixture();
        await f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);
        await f.FinishActiveExecutionAsync();
        await f.Service.AdvanceAllAsync(CancellationToken.None);
        await f.FinishActiveExecutionAsync();

        var second = await f.Service.StartAsync(f.Request(f.ModelB, f.ModelC), CancellationToken.None);

        second.Runs[0].State.Should().Be(ModelComparisonRunState.Running);
        (await f.Service.ListForTaskAsync(f.Task.Id, CancellationToken.None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Start_RejectsBadModelSelections()
    {
        var f = new Fixture();
        var disabled = NewModel("Off", enabled: false);
        f.Db.AiModelConfigs.Add(disabled);
        await f.Db.SaveChangesAsync();

        var tooFew = () => f.Service.StartAsync(f.Request(f.ModelA), CancellationToken.None);
        var tooMany = () => f.Service.StartAsync(
            f.Request(f.ModelA, f.ModelB, f.ModelC, NewModel("D")), CancellationToken.None);
        var duplicate = () => f.Service.StartAsync(
            new StartModelComparisonRequest { TaskId = f.Task.Id, ModelIds = { f.ModelA.Id, f.ModelA.Id } }, CancellationToken.None);
        var off = () => f.Service.StartAsync(f.Request(f.ModelA, disabled), CancellationToken.None);
        var unknown = () => f.Service.StartAsync(
            new StartModelComparisonRequest { TaskId = f.Task.Id, ModelIds = { f.ModelA.Id, Guid.NewGuid() } }, CancellationToken.None);

        foreach (var attempt in new[] { tooFew, tooMany, duplicate, off, unknown })
        {
            await attempt.Should().ThrowAsync<ArgumentException>();
        }

        (await f.Db.TaskExecutions.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(DevelopmentTaskStatus.Draft)]
    [InlineData(DevelopmentTaskStatus.AwaitingApproval)]
    [InlineData(DevelopmentTaskStatus.Rejected)]
    [InlineData(DevelopmentTaskStatus.Executing)]
    public async Task Start_RequiresAnApprovedPlan(DevelopmentTaskStatus status)
    {
        var f = new Fixture(status);

        var act = () => f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(DevelopmentTaskStatus.Approved)]
    [InlineData(DevelopmentTaskStatus.Failed)]
    [InlineData(DevelopmentTaskStatus.Completed)]
    public async Task Start_IsAllowed_ForApprovedFailedAndCompletedTasks(DevelopmentTaskStatus status)
    {
        var f = new Fixture(status);

        var comparison = await f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);

        comparison.Runs[0].State.Should().Be(ModelComparisonRunState.Running);
    }

    [Fact]
    public async Task Start_RequiresACompletedImpactAnalysis()
    {
        var f = new Fixture(withAnalysis: false);

        var act = () => f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*impact analysis*");
    }

    [Fact]
    public async Task Start_IsRefused_WhileAnExecutionIsAlreadyRunning()
    {
        var f = new Fixture();
        f.Db.TaskExecutions.Add(new TaskExecution
        {
            Id = Guid.NewGuid(), DevelopmentTaskId = f.Task.Id, Status = TaskExecutionStatus.Running, CreatedAt = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();

        var act = () => f.Service.StartAsync(f.Request(f.ModelA, f.ModelB), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already running*");
    }

    [Fact]
    public async Task Get_UnknownComparison_ThrowsNotFound()
    {
        var f = new Fixture();

        var act = () => f.Service.GetAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
