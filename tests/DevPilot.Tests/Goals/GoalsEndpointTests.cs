using System.Reflection;
using DevPilot.Api.Controllers;
using DevPilot.Application.Automation;
using DevPilot.Application.Goals;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DevPilot.Tests.Goals;

/// <summary>Guards the HTTP surface of goals: the routes exist and the actions answer as the screen expects.</summary>
public class GoalsEndpointTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private static string? Template(string action) =>
        typeof(GoalsController).GetMethod(action)!.GetCustomAttributes().OfType<HttpMethodAttribute>().Single().Template;

    [Fact]
    public void Routes_are_registered()
    {
        typeof(GoalsController).GetCustomAttribute<RouteAttribute>()!.Template
            .Should().Be("api/repositoryworkspaces/{workspaceId:guid}/goals");
        Template(nameof(GoalsController.PlanGoal)).Should().Be("plan");
        Template(nameof(GoalsController.ArrangeGoal)).Should().Be("arrange");
        Template(nameof(GoalsController.StartGoal)).Should().BeNull();
        Template(nameof(GoalsController.GetGoals)).Should().BeNull();
        Template(nameof(GoalsController.GetGoal)).Should().Be("{goalId:guid}");
        Template(nameof(GoalsController.CancelGoal)).Should().Be("{goalId:guid}/cancel");
    }

    [Fact]
    public async Task Planning_returns_the_plan_and_unknown_workspaces_are_404()
    {
        var plan = new GoalPlan(Array.Empty<GoalTaskPlan>(), Array.Empty<GoalWave>(), Array.Empty<GoalConflict>(),
            new GoalCostEstimate(0, 0, null, "default", 0), Array.Empty<GoalPlanWarning>(), "ai", 0);

        var ok = await Controller(new StubPlanner { Result = new GoalPlanResult { Success = true, Plan = plan } })
            .PlanGoal(Workspace, new PlanGoalRequest("x"), CancellationToken.None);
        var missing = await Controller(new StubPlanner { Result = new GoalPlanResult { NotFound = true, ErrorMessage = "no" } })
            .PlanGoal(Workspace, new PlanGoalRequest("x"), CancellationToken.None);
        var bad = await Controller(new StubPlanner { Result = new GoalPlanResult { ErrorMessage = "bad" } })
            .PlanGoal(Workspace, new PlanGoalRequest("x"), CancellationToken.None);

        ok.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(plan);
        missing.Should().BeOfType<NotFoundObjectResult>();
        bad.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Arranging_rejects_duplicate_keys_before_calling_the_planner()
    {
        var planner = new StubPlanner { Result = new GoalPlanResult { Success = true } };

        var result = await Controller(planner).ArrangeGoal(
            Workspace,
            new ArrangeGoalRequest { Tasks = new List<GoalTaskInput> { new() { Key = "t1", Title = "a" }, new() { Key = "t1", Title = "b" } } },
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        planner.ArrangeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Starting_returns_the_created_goal_and_a_missing_workspace_is_404()
    {
        var goal = new GoalState(Guid.NewGuid(), Workspace, "g", "t", GoalStatus.Active, "ai", 0, 0, null, DateTime.UtcNow, null, Array.Empty<GoalTaskState>());
        var store = new StubStore { Goal = goal };

        var created = await Controller(new StubPlanner(), new StubStart { Result = new StartGoalResult { Success = true, GoalId = goal.Id } }, store)
            .StartGoal(Workspace, new StartGoalRequest { Text = "t", Tasks = new List<GoalTaskInput>() }, CancellationToken.None);
        var missing = await Controller(new StubPlanner(), new StubStart { Result = new StartGoalResult { NotFound = true, ErrorMessage = "no" } }, store)
            .StartGoal(Workspace, new StartGoalRequest(), CancellationToken.None);

        created.Should().BeOfType<CreatedAtActionResult>().Which.Value.Should().BeOfType<GoalDetailDto>();
        missing.Should().BeOfType<NotFoundObjectResult>();
    }

    [Theory]
    [InlineData(AutomationLevel.Manual, false, false)]
    [InlineData(AutomationLevel.SemiAuto, false, true)]
    [InlineData(AutomationLevel.FullAuto, false, true)]
    [InlineData(AutomationLevel.FullAuto, true, false)]
    public async Task The_board_says_whether_ready_plans_are_approved_by_DevPilot_or_wait_for_a_person(AutomationLevel level, bool paused, bool expected)
    {
        var goal = new GoalState(Guid.NewGuid(), Workspace, "g", "t", GoalStatus.Active, "ai", 0, 0, null, DateTime.UtcNow, null, Array.Empty<GoalTaskState>());
        var policy = new AutomationPolicy { Level = level, Paused = paused };

        var result = await Controller(new StubPlanner(), new StubStart(), new StubStore { Goal = goal }, policy).GetGoal(Workspace, goal.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<GoalDetailDto>().Which.AutomationActive.Should().Be(expected);
    }

    [Fact]
    public async Task Without_any_policy_the_board_reports_automation_as_off()
    {
        var goal = new GoalState(Guid.NewGuid(), Workspace, "g", "t", GoalStatus.Active, "ai", 0, 0, null, DateTime.UtcNow, null, Array.Empty<GoalTaskState>());

        var result = await Controller(new StubPlanner(), new StubStart(), new StubStore { Goal = goal }, null).GetGoal(Workspace, goal.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<GoalDetailDto>().Which.AutomationActive.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_an_active_goal_marks_it_cancelled_and_a_missing_one_is_404()
    {
        var goal = new GoalState(Guid.NewGuid(), Workspace, "g", "t", GoalStatus.Active, "ai", 0, 0, null, DateTime.UtcNow, null, Array.Empty<GoalTaskState>());
        var store = new StubStore { Goal = goal };

        var result = await Controller(new StubPlanner(), new StubStart(), store).CancelGoal(Workspace, goal.Id, CancellationToken.None);
        var missing = await Controller(new StubPlanner(), new StubStart(), new StubStore()).CancelGoal(Workspace, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        store.Statuses.Should().Equal(GoalStatus.Cancelled);
        missing.Should().BeOfType<NotFoundObjectResult>();
    }

    private static GoalsController Controller(StubPlanner planner, StubStart? start = null, StubStore? store = null, AutomationPolicy? policy = null) =>
        new(planner, start ?? new StubStart(), store ?? new StubStore(), new StubPolicies(policy));

    private sealed class StubPolicies : IAutomationPolicyStore
    {
        private readonly AutomationPolicy? _policy;

        public StubPolicies(AutomationPolicy? policy) => _policy = policy;

        public Task<AutomationPolicy?> GetAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) => Task.FromResult(_policy);

        public Task<AutomationPolicy?> SaveAsync(AutomationPolicy policy, CancellationToken cancellationToken = default) => Task.FromResult<AutomationPolicy?>(policy);

        public Task<IReadOnlyList<AutomationPolicy>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AutomationPolicy>>(Array.Empty<AutomationPolicy>());
    }

    private sealed class StubPlanner : IGoalPlanner
    {
        public GoalPlanResult Result { get; set; } = new();

        public int ArrangeCalls { get; private set; }

        public Task<GoalPlanResult> PlanAsync(Guid repositoryWorkspaceId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<GoalPlanResult> ArrangeAsync(Guid repositoryWorkspaceId, IReadOnlyList<GoalTaskPlan> tasks, decimal? inputPerMillionUsd, decimal? outputPerMillionUsd, CancellationToken cancellationToken = default)
        {
            ArrangeCalls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubStart : IStartGoalCommandHandler
    {
        public StartGoalResult Result { get; set; } = new();

        public Task<StartGoalResult> HandleAsync(StartGoalCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    private sealed class StubStore : IGoalStore
    {
        public GoalState? Goal { get; set; }

        public List<GoalStatus> Statuses { get; } = new();

        public Task AddAsync(Goal goal, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GoalState?> GetAsync(Guid goalId, Guid? repositoryWorkspaceId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Goal is not null && Goal.Id == goalId ? Goal with { Status = Statuses.Count > 0 ? Statuses[^1] : Goal.Status } : null);

        public Task<IReadOnlyList<GoalState>> ListAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalState>>(Array.Empty<GoalState>());

        public Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task MarkAnalysisRequestedAsync(Guid goalTaskId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetNoteAsync(Guid goalTaskId, string? note, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetStatusAsync(Guid goalId, GoalStatus status, CancellationToken cancellationToken = default)
        {
            Statuses.Add(status);
            return Task.CompletedTask;
        }
    }
}
