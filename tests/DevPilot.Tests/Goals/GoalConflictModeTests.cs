using DevPilot.Application.Automation;
using DevPilot.Application.Goals;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure;
using DevPilot.Infrastructure.Automation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DevPilot.Tests.Goals;

public class GoalConflictRulesTests
{
    private static readonly string[] Editor = { "src/Notes/Editor.tsx" };

    [Fact]
    public void Careful_makes_tasks_wait_for_any_shared_file()
    {
        GoalConflictRules.Clashes(ConflictMode.Careful, Editor, Editor).Should().ContainSingle();
    }

    [Fact]
    public void Fast_never_makes_tasks_wait_for_a_shared_file()
    {
        GoalConflictRules.Clashes(ConflictMode.Fast, Editor, Editor, Editor, Editor).Should().BeEmpty();
        GoalConflictRules.Clashes(ConflictMode.Fast, new[] { "package.json" }, new[] { "package.json" }).Should().BeEmpty();
    }

    [Fact]
    public void Balanced_lets_tasks_share_an_ordinary_file()
    {
        GoalConflictRules.Clashes(ConflictMode.Balanced, Editor, Editor).Should().BeEmpty();
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("web/package-lock.json")]
    [InlineData("src/App/App.csproj")]
    [InlineData("DevPilot.sln")]
    [InlineData("src/Infra/Migrations/20260101_Add.cs")]
    [InlineData("src/Infra/Migrations/Snapshot.cs")]
    [InlineData("tsconfig.app.json")]
    [InlineData("src/Api/appsettings.Development.json")]
    [InlineData("vite.config.ts")]
    [InlineData("src/__snapshots__/a.snap")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("yarn.lock")]
    [InlineData("src\\Api\\Dockerfile")]
    public void Balanced_still_makes_tasks_wait_for_files_that_cannot_be_merged(string path)
    {
        GoalConflictRules.IsHot(path).Should().BeTrue();
        GoalConflictRules.Clashes(ConflictMode.Balanced, new[] { path }, new[] { path }).Should().ContainSingle();
    }

    [Theory]
    [InlineData("src/Notes/Editor.tsx")]
    [InlineData("src/Services/MigrationHelper.cs")]
    [InlineData("docs/package-notes.md")]
    [InlineData("src/Api/appsettings.cs")]
    public void Ordinary_files_are_not_hot(string path) => GoalConflictRules.IsHot(path).Should().BeFalse();

    [Fact]
    public void Balanced_waits_when_either_task_creates_deletes_or_restructures_the_shared_file()
    {
        GoalConflictRules.Clashes(ConflictMode.Balanced, Editor, Editor, Editor, null).Should().ContainSingle();
        GoalConflictRules.Clashes(ConflictMode.Balanced, Editor, Editor, null, new[] { "SRC/notes/editor.tsx" }).Should().ContainSingle();
        GoalConflictRules.Clashes(ConflictMode.Balanced, Editor, Editor, new[] { "src/Other.tsx" }, null).Should().BeEmpty();
    }

    [Fact]
    public void The_hot_file_is_found_even_when_many_ordinary_files_are_shared()
    {
        var first = Enumerable.Range(1, 6).Select(i => $"src/File{i}.ts").Append("package.json").ToList();

        GoalConflictRules.Clashes(ConflictMode.Balanced, first, first).Should().Equal("package.json");
    }

    [Fact]
    public void Plans_in_balanced_mode_run_ordinary_shared_files_together_but_chain_hot_ones()
    {
        var tasks = new[]
        {
            new GoalTaskPlan("t1", "A", "d", new[] { "src/Editor.tsx", "package.json" }, Array.Empty<string>(), "medium"),
            new GoalTaskPlan("t2", "B", "d", new[] { "src/Editor.tsx" }, Array.Empty<string>(), "medium"),
            new GoalTaskPlan("t3", "C", "d", new[] { "package.json" }, Array.Empty<string>(), "medium")
        };

        GoalWavePlanner.Plan(tasks, ConflictMode.Careful).Waves.Select(w => string.Join(",", w.TaskKeys)).Should().Equal("t1", "t2,t3");
        GoalWavePlanner.Plan(tasks, ConflictMode.Balanced).Waves.Select(w => string.Join(",", w.TaskKeys)).Should().Equal("t1,t2", "t3");
        GoalWavePlanner.Plan(tasks, ConflictMode.Fast).Waves.Should().ContainSingle();
    }
}

public class GoalConflictModeFlowTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private static GoalOrchestrator Orchestrator(
        ConflictMode mode,
        GoalState goal,
        List<Guid> started,
        List<Guid> approved,
        Dictionary<Guid, string?> notes)
    {
        var policy = new AutomationPolicy { RepositoryWorkspaceId = Workspace, Level = AutomationLevel.SemiAuto, ConflictMode = mode, MaxParallelExecutions = 5 };
        return new GoalOrchestrator(
            new RecordingStore(goal, notes),
            new StubPolicyStore { Policy = policy },
            new NoWork(),
            new NoDispatch(),
            new Approve(approved),
            new Start(started),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GoalOrchestrator>.Instance);
    }

    private static GoalState Goal(string[] runningFiles, string[]? runningStructural, string[] readyFiles, string[]? readyStructural = null)
    {
        var running = GoalFixtures.Task("t1", run: GoalFixtures.Run(), impacted: runningFiles) with { StructuralFiles = runningStructural };
        var ready = GoalFixtures.Task("t2", DevelopmentTaskStatus.AwaitingApproval, impacted: readyFiles) with { StructuralFiles = readyStructural };
        return GoalFixtures.Goal(Workspace, running, ready);
    }

    [Theory]
    [InlineData(ConflictMode.Careful, false)]
    [InlineData(ConflictMode.Balanced, true)]
    [InlineData(ConflictMode.Fast, true)]
    public async Task A_shared_ordinary_file_holds_a_ready_plan_only_in_careful_mode(ConflictMode mode, bool starts)
    {
        var started = new List<Guid>();
        var goal = Goal(new[] { "src/Editor.tsx" }, null, new[] { "src/Editor.tsx" });

        await Orchestrator(mode, goal, started, new List<Guid>(), new Dictionary<Guid, string?>()).AdvanceAsync(goal.Id);

        (started.Count == 1).Should().Be(starts);
    }

    [Theory]
    [InlineData(ConflictMode.Careful, false)]
    [InlineData(ConflictMode.Balanced, false)]
    [InlineData(ConflictMode.Fast, true)]
    public async Task A_shared_hot_file_holds_a_ready_plan_unless_the_mode_is_fast(ConflictMode mode, bool starts)
    {
        var started = new List<Guid>();
        var goal = Goal(new[] { "package.json" }, null, new[] { "package.json" });

        await Orchestrator(mode, goal, started, new List<Guid>(), new Dictionary<Guid, string?>()).AdvanceAsync(goal.Id);

        (started.Count == 1).Should().Be(starts);
    }

    [Fact]
    public async Task In_balanced_mode_a_file_the_running_task_creates_still_holds_the_ready_plan()
    {
        var started = new List<Guid>();
        var notes = new Dictionary<Guid, string?>();
        var goal = Goal(new[] { "src/New.tsx" }, new[] { "src/New.tsx" }, new[] { "src/New.tsx" });

        await Orchestrator(ConflictMode.Balanced, goal, started, new List<Guid>(), notes).AdvanceAsync(goal.Id);

        started.Should().BeEmpty();
        notes.Values.Single().Should().Contain("New.tsx");
    }

    [Theory]
    [InlineData(ConflictMode.Careful, true)]
    [InlineData(ConflictMode.Balanced, false)]
    [InlineData(ConflictMode.Fast, false)]
    public async Task The_mode_decides_which_tasks_a_new_goal_makes_wait(ConflictMode mode, bool waits)
    {
        var store = new CapturingStore();
        var handler = new StartGoalCommandHandler(new Create(), store, new StubPolicyStore(mode), Microsoft.Extensions.Logging.Abstractions.NullLogger<StartGoalCommandHandler>.Instance);

        await handler.HandleAsync(new StartGoalCommand(
            Workspace, "text", "ai",
            new[]
            {
                new GoalTaskInput { Key = "t1", Title = "A", Areas = new List<string> { "src/Editor.tsx" } },
                new GoalTaskInput { Key = "t2", Title = "B", Areas = new List<string> { "src/Editor.tsx" } }
            },
            0, 0, null));

        store.Added.Single().Tasks.Single(t => t.Key == "t2").BlockedBy.Should().Be(waits ? "t1" : string.Empty);
    }

    [Fact]
    public async Task A_repository_without_a_policy_gets_the_balanced_default()
    {
        var store = new CapturingStore();
        var handler = new StartGoalCommandHandler(new Create(), store, new StubPolicyStore { Policy = null }, Microsoft.Extensions.Logging.Abstractions.NullLogger<StartGoalCommandHandler>.Instance);

        await handler.HandleAsync(new StartGoalCommand(
            Workspace, "text", "ai",
            new[]
            {
                new GoalTaskInput { Key = "t1", Title = "A", Areas = new List<string> { "src/Editor.tsx" } },
                new GoalTaskInput { Key = "t2", Title = "B", Areas = new List<string> { "src/Editor.tsx" } }
            },
            0, 0, null));

        store.Added.Single().Tasks.Single(t => t.Key == "t2").BlockedBy.Should().BeEmpty();
    }

    [Fact]
    public void A_new_policy_defaults_to_the_balanced_mode()
    {
        new AutomationPolicy().ConflictMode.Should().Be(ConflictMode.Balanced);
    }

    [Theory]
    [InlineData(ConflictMode.Careful)]
    [InlineData(ConflictMode.Balanced)]
    [InlineData(ConflictMode.Fast)]
    public async Task The_chosen_mode_is_stored_even_when_it_is_the_enum_default(ConflictMode mode)
    {
        using var db = new DevPilotDbContext(new DbContextOptionsBuilder<DevPilotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var workspace = new RepositoryWorkspace { Id = Guid.NewGuid(), Owner = "o", Repository = "r", Branch = "main" };
        db.RepositoryWorkspaces.Add(workspace);
        await db.SaveChangesAsync();
        var store = new EfAutomationPolicyStore(db);

        await store.SaveAsync(new AutomationPolicy { RepositoryWorkspaceId = workspace.Id, Level = AutomationLevel.SemiAuto, ConflictMode = mode });

        (await store.GetAsync(workspace.Id))!.ConflictMode.Should().Be(mode);
    }

    private sealed class RecordingStore : IGoalStore
    {
        private readonly GoalState _goal;
        private readonly Dictionary<Guid, string?> _notes;

        public RecordingStore(GoalState goal, Dictionary<Guid, string?> notes)
        {
            _goal = goal;
            _notes = notes;
        }

        public Task AddAsync(Goal goal, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GoalState?> GetAsync(Guid goalId, Guid? repositoryWorkspaceId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<GoalState?>(_goal);

        public Task<IReadOnlyList<GoalState>> ListAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalState>>(Array.Empty<GoalState>());

        public Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task MarkAnalysisRequestedAsync(Guid goalTaskId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetNoteAsync(Guid goalTaskId, string? note, CancellationToken cancellationToken = default)
        {
            _notes[goalTaskId] = note;
            return Task.CompletedTask;
        }

        public Task SetStatusAsync(Guid goalId, GoalStatus status, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CapturingStore : IGoalStore
    {
        public List<Goal> Added { get; } = new();

        public Task AddAsync(Goal goal, CancellationToken cancellationToken = default)
        {
            Added.Add(goal);
            return Task.CompletedTask;
        }

        public Task<GoalState?> GetAsync(Guid goalId, Guid? repositoryWorkspaceId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<GoalState?>(null);

        public Task<IReadOnlyList<GoalState>> ListAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalState>>(Array.Empty<GoalState>());

        public Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task MarkAnalysisRequestedAsync(Guid goalTaskId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetNoteAsync(Guid goalTaskId, string? note, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetStatusAsync(Guid goalId, GoalStatus status, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoWork : IAutomationWorkReader
    {
        public Task<IReadOnlyList<DevelopmentTask>> GetTasksReadyToStartAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentTask>>(Array.Empty<DevelopmentTask>());

        public Task<int> CountActiveExecutionsAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) => Task.FromResult(0);
public Task<int> CountExecutionsForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<Guid>> GetDeliverableExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        public Task<IReadOnlyList<Guid>> GetRetryableFailedExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());


        public Task<IReadOnlyList<Guid>> GetOpenPullRequestExecutionIdsAsync(Guid repositoryWorkspaceId, DateTime since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
    }

    private sealed class NoDispatch : IGoalAnalysisDispatcher
    {
        public void EnqueueAnalysis(Guid taskId)
        {
        }
    }

    private sealed class Approve : DevPilot.Application.Tasks.Commands.ApproveTask.IApproveTaskCommandHandler
    {
        private readonly List<Guid> _approved;

        public Approve(List<Guid> approved) => _approved = approved;

        public Task<DevPilot.Application.Tasks.Commands.ApproveTask.ApproveTaskResult> HandleAsync(
            DevPilot.Application.Tasks.Commands.ApproveTask.ApproveTaskCommand command, CancellationToken cancellationToken = default)
        {
            _approved.Add(command.TaskId);
            return Task.FromResult(new DevPilot.Application.Tasks.Commands.ApproveTask.ApproveTaskResult { Success = true });
        }
    }

    private sealed class Start : DevPilot.Application.Executions.Commands.StartExecution.IStartExecutionCommandHandler
    {
        private readonly List<Guid> _started;

        public Start(List<Guid> started) => _started = started;

        public Task<DevPilot.Application.Executions.Commands.StartExecution.StartExecutionResult> HandleAsync(
            DevPilot.Application.Executions.Commands.StartExecution.StartExecutionCommand command, CancellationToken cancellationToken = default)
        {
            _started.Add(command.TaskId);
            return Task.FromResult(new DevPilot.Application.Executions.Commands.StartExecution.StartExecutionResult { Success = true });
        }
    }

    private sealed class Create : DevPilot.Application.Tasks.Commands.CreateTask.ICreateTaskCommandHandler
    {
        public Task<DevPilot.Application.Tasks.Commands.CreateTask.CreateTaskResult> HandleAsync(
            DevPilot.Application.Tasks.Commands.CreateTask.CreateTaskCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevPilot.Application.Tasks.Commands.CreateTask.CreateTaskResult
            {
                Success = true,
                Task = new DevPilot.Application.Tasks.Dtos.TaskDto { Id = Guid.NewGuid(), Title = command.Dto.Title }
            });
    }
}
