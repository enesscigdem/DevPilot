using DevPilot.Application.AiProviders;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Goals;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.ProjectBrain.Queries.SemanticSearch;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.Goals;

public class GoalWavePlannerTests
{
    private static GoalTaskPlan Task(string key, string[]? areas = null, string[]? dependsOn = null) =>
        new(key, key, "d", areas ?? Array.Empty<string>(), dependsOn ?? Array.Empty<string>(), "medium");

    // One string per wave, e.g. "1,2,3" for t1, t2 and t3 running together.
    private static string[] Waves(params GoalTaskPlan[] tasks) =>
        GoalWavePlanner.Plan(tasks).Waves.Select(w => string.Join(",", w.TaskKeys.Select(k => k[1..]))).ToArray();

    [Fact]
    public void Tasks_in_different_places_run_together()
    {
        Waves(Task("t1", new[] { "src/A/a.cs" }), Task("t2", new[] { "src/B/b.cs" }), Task("t3", new[] { "src/C/c.cs" }))
            .Should().Equal("1,2,3");
    }

    [Fact]
    public void Tasks_touching_the_same_file_are_never_in_the_same_wave()
    {
        var plan = GoalWavePlanner.Plan(new[]
        {
            Task("t1", new[] { "src/Notes/Editor.tsx" }),
            Task("t2", new[] { "src/Notes/Editor.tsx" }),
            Task("t3", new[] { "src/Search/Box.tsx" })
        });

        plan.Waves.Select(w => string.Join(",", w.TaskKeys)).Should().Equal("t1,t3", "t2");
        plan.Conflicts.Should().ContainSingle(c => c.FirstKey == "t1" && c.SecondKey == "t2");
    }

    [Fact]
    public void Only_the_same_file_is_a_clash_never_a_folder()
    {
        GoalWavePlanner.SharedAreas(new[] { "src/Notes/Editor.tsx" }, new[] { "src/Notes/Editor.tsx" }).Should().ContainSingle();
        GoalWavePlanner.SharedAreas(new[] { "src/Notes" }, new[] { "src/Notes/Editor.tsx" }).Should().BeEmpty();
        GoalWavePlanner.SharedAreas(new[] { "src/Notes/" }, new[] { "src/Notes/" }).Should().BeEmpty();
        GoalWavePlanner.SharedAreas(new[] { "src/Notes/Editor.tsx" }, new[] { "src/Notes/Sidebar.tsx" }).Should().BeEmpty();
        GoalWavePlanner.SharedAreas(new[] { "src/Editor.tsx" }, new[] { "src/Editor.tsx.map" }).Should().BeEmpty();
    }

    [Fact]
    public void A_shared_file_is_reported_as_it_was_written()
    {
        GoalWavePlanner.SharedAreas(new[] { "src/Notes/NoteEditor.tsx" }, new[] { "SRC/notes/noteeditor.tsx" })
            .Should().ContainSingle().Which.Should().Be("src/Notes/NoteEditor.tsx");
    }

    [Fact]
    public void Guessed_areas_never_hold_a_task_back()
    {
        var plan = GoalWavePlanner.Plan(new[]
        {
            new GoalTaskPlan("t1", "A", "d", new[] { "src/Editor.tsx" }, Array.Empty<string>(), "medium", AreasGuessed: true),
            new GoalTaskPlan("t2", "B", "d", new[] { "src/Editor.tsx" }, Array.Empty<string>(), "medium", AreasGuessed: true),
            new GoalTaskPlan("t3", "C", "d", new[] { "src/Editor.tsx" }, Array.Empty<string>(), "medium")
        });

        plan.Waves.Should().ContainSingle();
        plan.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void Paths_are_compared_without_regard_to_separators_case_or_leading_dot()
    {
        GoalWavePlanner.SharedAreas(new[] { @".\Src\Notes\Editor.tsx" }, new[] { "src/notes/editor.tsx" }).Should().ContainSingle();
    }

    [Fact]
    public void Unknown_areas_never_create_a_conflict()
    {
        Waves(Task("t1"), Task("t2")).Should().Equal("1,2");
    }

    [Fact]
    public void An_explicit_dependency_waits_for_its_wave()
    {
        Waves(Task("t1", new[] { "a" }), Task("t2", new[] { "b" }, new[] { "t1" }), Task("t3", new[] { "c" }, new[] { "t2" }))
            .Should().Equal("1", "2", "3");
    }

    [Fact]
    public void A_dependency_on_a_later_or_unknown_task_is_ignored()
    {
        Waves(Task("t1", dependsOn: new[] { "t2" }), Task("t2", dependsOn: new[] { "t9" }))
            .Should().Equal("1,2");
    }

    [Fact]
    public void Tasks_sharing_the_editor_are_pushed_to_a_later_wave_while_the_rest_stay_parallel()
    {
        var plan = GoalWavePlanner.Plan(new[]
        {
            Task("t1", new[] { "Notes/Editor.tsx" }),
            Task("t2", new[] { "Notes/Sidebar.tsx" }),
            Task("t3", new[] { "Notes/Editor.tsx", "Notes/Toolbar.tsx" }),
            Task("t4", new[] { "Notes/Toolbar.tsx" }),
            Task("t5", new[] { "Notes/Search.tsx" })
        });

        // t4 shares the toolbar with t3, but t3 is in wave 2, so t4 can already run in wave 1.
        plan.Waves.Select(w => string.Join(",", w.TaskKeys)).Should().Equal("t1,t2,t4,t5", "t3");
    }
}

public class GoalCostEstimatorTests
{
    private static GoalTaskPlan Task(string size) => new("t", "t", "d", Array.Empty<string>(), Array.Empty<string>(), size);

    [Fact]
    public void Without_history_a_rough_default_is_used_and_says_so()
    {
        var estimate = GoalCostEstimator.Estimate(new[] { Task("medium"), Task("medium") }, null, null, null);

        estimate.Basis.Should().Be("default");
        estimate.Samples.Should().Be(0);
        estimate.InputTokens.Should().Be(240_000);
        estimate.Usd.Should().BeNull();
    }

    [Fact]
    public void With_enough_history_the_average_of_earlier_runs_is_used()
    {
        var estimate = GoalCostEstimator.Estimate(
            new[] { Task("medium") }, new GoalUsageHistory(200_000, 40_000, 8), null, null);

        estimate.Basis.Should().Be("history");
        estimate.Samples.Should().Be(8);
        estimate.InputTokens.Should().Be(200_000);
        estimate.OutputTokens.Should().Be(40_000);
    }

    [Fact]
    public void Too_little_history_is_not_trusted()
    {
        GoalCostEstimator.Estimate(new[] { Task("medium") }, new GoalUsageHistory(1, 1, 2), null, null)
            .Basis.Should().Be("default");
    }

    [Fact]
    public void Size_scales_the_estimate_and_a_price_gives_dollars()
    {
        var estimate = GoalCostEstimator.Estimate(
            new[] { Task("small"), Task("large") }, new GoalUsageHistory(100_000, 20_000, 5), 3m, 15m);

        estimate.InputTokens.Should().Be(240_000);   // 100k * (0.6 + 1.8)
        estimate.OutputTokens.Should().Be(48_000);
        estimate.Usd.Should().Be(1.44m);             // 0.24M*3 + 0.048M*15 = 0.72 + 0.72
    }

    [Theory]
    [InlineData("S", "small")]
    [InlineData("Large", "large")]
    [InlineData("whatever", "medium")]
    [InlineData(null, "medium")]
    public void Sizes_are_normalised(string? raw, string expected) =>
        GoalCostEstimator.NormalizeSize(raw).Should().Be(expected);
}

public class GoalPlannerTests
{
    private const string Backlog = "Task: Arama\nDescription: Başlık ve içerikte ara\n\nTask: Toolbar\nDescription: Sticky olsun";

    private static readonly Guid WorkspaceId = Guid.NewGuid();

    [Fact]
    public void The_models_json_is_read_even_inside_code_fences_and_prose()
    {
        var tasks = GoalPlanner.ParseTasks(
            "Here is the plan:\n```json\n{\"tasks\":[{\"title\":\"A\",\"description\":\"a\",\"areas\":[\"src/a.ts\"],\"dependsOn\":[],\"size\":\"S\"}," +
            "{\"title\":\"B\",\"description\":\"b\",\"areas\":[],\"dependsOn\":[1,2,7],\"size\":\"large\"}]}\n```");

        tasks.Should().NotBeNull();
        tasks!.Select(t => t.Key).Should().Equal("t1", "t2");
        tasks[0].Size.Should().Be("small");
        tasks[0].Areas.Should().Equal("src/a.ts");
        tasks[1].DependsOn.Should().Equal("t1");   // 2 is itself, 7 does not exist
    }

    [Theory]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{ \"tasks\": [ {\"title\": ")]
    [InlineData("{\"other\":1}")]
    public void Unreadable_answers_yield_null(string content) =>
        GoalPlanner.ParseTasks(content).Should().BeNull();

    [Fact]
    public async Task A_good_answer_becomes_a_plan_with_waves_and_an_estimate()
    {
        var ai = AiAnswering(new AiResponse
        {
            IsSuccess = true, Model = "m", InputTokens = 1000, OutputTokens = 500,
            Content = "{\"tasks\":[" +
                      "{\"title\":\"Arama\",\"description\":\"d\",\"areas\":[\"src/Search.tsx\"],\"size\":\"small\"}," +
                      "{\"title\":\"Toolbar\",\"description\":\"d\",\"areas\":[\"src/Search.tsx\"],\"size\":\"medium\"}," +
                      "{\"title\":\"Silme\",\"description\":\"d\",\"areas\":[\"src/Delete.tsx\"]}]}"
        });

        var result = await NewPlanner(ai).PlanAsync(WorkspaceId, "arama, toolbar ve silme geri alma");

        result.Success.Should().BeTrue();
        var plan = result.Plan!;
        plan.Source.Should().Be("ai");
        plan.PlanningTokens.Should().Be(1500);
        plan.Tasks.Should().HaveCount(3);
        plan.Waves.Select(w => string.Join(",", w.TaskKeys)).Should().Equal("t1,t3", "t2");
        plan.Conflicts.Should().ContainSingle();
        plan.Estimate.Basis.Should().Be("default");
        plan.Estimate.InputTokens.Should().BeGreaterThan(0);
        ai.ReceivedRequests.Single().UserPrompt.Should().Contain("arama, toolbar ve silme geri alma");
    }

    [Fact]
    public async Task When_the_model_fails_the_structured_text_still_becomes_a_plan()
    {
        var ai = new FakeAiProvider { IsSuccessToReturn = false, ErrorMessageToReturn = "down" };

        var result = await NewPlanner(ai).PlanAsync(WorkspaceId, Backlog);

        result.Success.Should().BeTrue();
        result.Plan!.Source.Should().Be("parser");
        result.Plan.Tasks.Select(t => t.Title).Should().Equal("Arama", "Toolbar");
        result.Plan.Warnings.Should().Contain(w => w.Code == "AiUnavailable");
    }

    [Fact]
    public async Task When_the_model_fails_and_the_text_has_no_structure_the_person_is_told()
    {
        var ai = new FakeAiProvider { IsSuccessToReturn = false, ErrorMessageToReturn = "down" };

        var result = await NewPlanner(ai).PlanAsync(WorkspaceId, "make the app nicer somehow");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Tasks_without_areas_get_them_from_a_code_search_and_unknown_ones_are_flagged()
    {
        var ai = AiAnswering(new AiResponse
        {
            IsSuccess = true, Model = "m",
            Content = "{\"tasks\":[{\"title\":\"Arama\",\"description\":\"d\",\"areas\":[]},{\"title\":\"Başka\",\"description\":\"d\",\"areas\":[]}]}"
        });
        var search = new FakeSearch(query => query.QueryText.StartsWith("Arama")
            ? new[] { "src/Search.tsx", "src/Search.tsx", "src/Box.tsx" }
            : Array.Empty<string>());

        var result = await NewPlanner(ai, search).PlanAsync(WorkspaceId, "arama ve başka");

        result.Plan!.Tasks[0].Areas.Should().Equal("src/Search.tsx", "src/Box.tsx");
        result.Plan.Tasks[0].AreasGuessed.Should().BeTrue();   // found by search, so it never delays the task
        result.Plan.Tasks[1].Areas.Should().BeEmpty();
        result.Plan.Tasks[1].AreasGuessed.Should().BeFalse();
        result.Plan.Warnings.Should().Contain(w => w.Code == "AreasUnknown" && w.TaskKey == "t2");
    }

    [Fact]
    public async Task Empty_oversized_and_unknown_workspace_requests_fail_cleanly()
    {
        var planner = NewPlanner(new FakeAiProvider());

        (await planner.PlanAsync(WorkspaceId, "  ")).Success.Should().BeFalse();
        (await planner.PlanAsync(WorkspaceId, new string('x', GoalPlanner.MaxGoalLength + 1))).Success.Should().BeFalse();
        (await NewPlanner(new FakeAiProvider(), workspaceExists: false).PlanAsync(WorkspaceId, "x")).NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Arranging_after_a_removal_renumbers_the_tasks_and_regroups_the_waves()
    {
        // t1 (removed) used to hold t2 back; without it t2 and t3 can run together.
        var planner = NewPlanner(new FakeAiProvider());
        var tasks = new[]
        {
            new GoalTaskPlan("t2", "B", "d", new[] { "src/Editor.tsx" }, Array.Empty<string>(), "small"),
            new GoalTaskPlan("t3", "C", "d", new[] { "src/Search.tsx" }, new[] { "t1", "t2" }, "large")
        };

        var result = await planner.ArrangeAsync(WorkspaceId, tasks, 3m, 15m);

        result.Success.Should().BeTrue();
        var plan = result.Plan!;
        plan.Tasks.Select(t => t.Key).Should().Equal("t1", "t2");
        plan.Tasks[1].DependsOn.Should().Equal("t1");     // the dependency on the removed task is gone, the one on B was renamed
        plan.Waves.Select(w => string.Join(",", w.TaskKeys)).Should().Equal("t1", "t2");
        plan.Estimate.InputPerMillionUsd.Should().Be(3m);
        plan.Estimate.Usd.Should().NotBeNull();
        plan.PlanningTokens.Should().Be(0);
    }

    [Fact]
    public async Task Arranging_independent_tasks_puts_them_in_one_wave_and_flags_missing_areas()
    {
        var tasks = new[]
        {
            new GoalTaskPlan("t1", "A", "d", new[] { "a.ts" }, Array.Empty<string>(), "medium"),
            new GoalTaskPlan("t2", "B", "", Array.Empty<string>(), Array.Empty<string>(), "medium")
        };

        var plan = (await NewPlanner(new FakeAiProvider()).ArrangeAsync(WorkspaceId, tasks, null, null)).Plan!;

        plan.Waves.Should().ContainSingle();
        plan.Warnings.Should().Contain(w => w.Code == "AreasUnknown" && w.TaskKey == "t2");
        plan.Warnings.Should().Contain(w => w.Code == "MissingDescription" && w.TaskKey == "t2");
    }

    [Fact]
    public async Task Arranging_rejects_an_empty_or_unknown_request()
    {
        var planner = NewPlanner(new FakeAiProvider());

        (await planner.ArrangeAsync(WorkspaceId, Array.Empty<GoalTaskPlan>(), null, null)).Success.Should().BeFalse();
        (await NewPlanner(new FakeAiProvider(), workspaceExists: false)
            .ArrangeAsync(WorkspaceId, new[] { new GoalTaskPlan("t1", "A", "d", Array.Empty<string>(), Array.Empty<string>(), "small") }, null, null))
            .NotFound.Should().BeTrue();
    }

    private static FakeAiProvider AiAnswering(AiResponse response)
    {
        var ai = new FakeAiProvider();
        ai.StructuredResponsesToReturn.Enqueue(response);
        return ai;
    }

    private static GoalPlanner NewPlanner(FakeAiProvider ai, FakeSearch? search = null, bool workspaceExists = true, ConflictMode mode = ConflictMode.Careful) =>
        new(new FakeWorkspaceQuery(workspaceExists), search ?? new FakeSearch(_ => Array.Empty<string>()), ai,
            new FakeHistory(), new AiPricingOptions(), new StubPolicyStore(mode), NullLogger<GoalPlanner>.Instance);

    private sealed class FakeWorkspaceQuery : IRepositoryWorkspaceQuery
    {
        private readonly bool _exists;

        public FakeWorkspaceQuery(bool exists) => _exists = exists;

        public Task<RepositoryWorkspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<RepositoryWorkspace?>(_exists ? new RepositoryWorkspace { Id = id, LocalPath = "x" } : null);
    }

    private sealed class FakeHistory : IGoalHistoryReader
    {
        public Task<GoalUsageHistory?> GetRecentUsageAsync(Guid repositoryWorkspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GoalUsageHistory?>(null);
    }

    private sealed class FakeSearch : ISemanticSearchQueryHandler
    {
        private readonly Func<SemanticSearchQuery, string[]> _paths;

        public FakeSearch(Func<SemanticSearchQuery, string[]> paths) => _paths = paths;

        public Task<SemanticSearchResult> HandleAsync(SemanticSearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SemanticSearchResult
            {
                Success = true,
                Hits = _paths(query).Select(p => new SemanticSearchHit { Chunk = new CodeChunk { RelativePath = p, Content = "code" }, Score = 1 }).ToList()
            });
    }
}
