using System.Text;
using System.Text.Json;
using DevPilot.Application.AiProviders;
using DevPilot.Application.Automation;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.ProjectBrain.Queries.SemanticSearch;
using DevPilot.Application.Tasks.Batch;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Constants;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Goals;

public interface IGoalPlanner
{
    /// <summary>Turns whatever the person wrote (a paragraph, a list, rough notes) into a plan. Creates nothing.</summary>
    Task<GoalPlanResult> PlanAsync(Guid repositoryWorkspaceId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-arranges tasks the person has edited or removed: waves, conflicts and estimate again, with no model call.
    /// The prices of the original plan are passed back so the estimate stays comparable.
    /// </summary>
    Task<GoalPlanResult> ArrangeAsync(
        Guid repositoryWorkspaceId,
        IReadOnlyList<GoalTaskPlan> tasks,
        decimal? inputPerMillionUsd,
        decimal? outputPerMillionUsd,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Splits a goal into deliverable tasks with one model call grounded in the repository's own code, then predicts
/// where each task will land with a cheap code search and arranges the tasks into waves. The expensive impact
/// analysis is deliberately not done here: it runs per task when that task's turn comes.
/// </summary>
public sealed class GoalPlanner : IGoalPlanner
{
    public const int MaxGoalLength = 20_000;
    public const int MaxTasks = 20;

    private const int ContextHits = 14;
    private const int SnippetChars = 280;
    private const int AreasPerTask = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string SystemPrompt =
        "You are a senior engineer who breaks a request into tasks for an autonomous coding agent working on one repository.\n" +
        "Return ONLY a JSON object, no prose and no code fences: " +
        "{\"tasks\":[{\"title\":string,\"description\":string,\"areas\":[string],\"dependsOn\":[number],\"size\":\"small\"|\"medium\"|\"large\"}]}\n" +
        "Rules:\n" +
        "- One task is one change that can be reviewed and merged on its own. Every distinct behaviour the request asks for is its own task: prefer more, smaller tasks over fewer large ones, and never fold two separate asks into one task. Do not split one change into pieces that cannot work alone.\n" +
        "- If the request already lists tasks, keep each one as its own task and keep the person's meaning and wording; do not drop or invent tasks. If it is a paragraph or rough notes, find the separate changes in it.\n" +
        "- Write title and description in the same language as the request. The title is short and specific. The description says what should change and what 'done' looks like, using only what the request and the code context support.\n" +
        "- areas: the specific FILES (full repository paths, with extension) this task will most likely change, taken from the code context when possible. Name files, not folders. Use an empty list when unsure; never invent paths that look unrelated to the context.\n" +
        "- dependsOn: 1-based numbers of EARLIER tasks that must be finished first because this task needs their result. Usually empty. Do not use it just because two tasks touch the same files.\n" +
        "- size: small = a few lines in one or two files, medium = a normal feature or fix, large = touches many files.\n" +
        $"- At most {MaxTasks} tasks. A task that would change more than {ExecutionCapacityPolicy.MaxImpactedFiles} files must be split.";

    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly ISemanticSearchQueryHandler _search;
    private readonly IAiProvider _ai;
    private readonly IGoalHistoryReader _history;
    private readonly AiPricingOptions _pricing;
    private readonly IAutomationPolicyStore _policies;
    private readonly ILogger<GoalPlanner> _logger;

    public GoalPlanner(
        IRepositoryWorkspaceQuery workspaceQuery,
        ISemanticSearchQueryHandler search,
        IAiProvider ai,
        IGoalHistoryReader history,
        AiPricingOptions pricing,
        IAutomationPolicyStore policies,
        ILogger<GoalPlanner> logger)
    {
        _policies = policies;
        _workspaceQuery = workspaceQuery;
        _search = search;
        _ai = ai;
        _history = history;
        _pricing = pricing;
        _logger = logger;
    }

    public async Task<GoalPlanResult> PlanAsync(Guid repositoryWorkspaceId, string text, CancellationToken cancellationToken = default)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new GoalPlanResult { ErrorMessage = "Describe what you want to build." };
        }

        if (text.Length > MaxGoalLength)
        {
            return new GoalPlanResult { ErrorMessage = "The description is too long." };
        }

        var workspace = await _workspaceQuery.GetByIdAsync(repositoryWorkspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            return new GoalPlanResult { NotFound = true, ErrorMessage = "Repository workspace not found." };
        }

        var warnings = new List<GoalPlanWarning>();
        var context = await BuildContextAsync(workspace.Id, workspace.LocalPath, text, cancellationToken).ConfigureAwait(false);

        List<GoalTaskPlan>? tasks = null;
        var source = "ai";
        long planningTokens = 0;
        string? model = null;

        var response = await SendAsync(text, context, cancellationToken).ConfigureAwait(false);
        if (response is { IsSuccess: true })
        {
            planningTokens = (response.InputTokens ?? 0) + (response.OutputTokens ?? 0);
            model = response.Model;
            tasks = ParseTasks(response.Content);
            if (tasks is null)
            {
                _logger.LogWarning("The goal planner could not read the model's answer; falling back to the plain parser.");
            }
        }
        else
        {
            _logger.LogWarning("The goal planner's model call failed: {Error}", response?.ErrorMessage);
        }

        if (tasks is null || tasks.Count == 0)
        {
            // The model is unavailable or answered unusably: whatever structure the text has is still honoured.
            tasks = FromParser(text);
            source = "parser";
            warnings.Add(new GoalPlanWarning("AiUnavailable"));
        }

        if (tasks.Count == 0)
        {
            return new GoalPlanResult { ErrorMessage = "No tasks could be found in the description. Try describing the changes you want, one per line." };
        }

        tasks = await FillMissingAreasAsync(workspace.Id, workspace.LocalPath, tasks, warnings, cancellationToken).ConfigureAwait(false);

        var (waves, conflicts) = GoalWavePlanner.Plan(tasks, await ModeAsync(workspace.Id, cancellationToken).ConfigureAwait(false));
        var history = await _history.GetRecentUsageAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
        decimal? inputPrice = null, outputPrice = null;
        if (_pricing.TryGetPrice(model, out var inPrice, out var outPrice))
        {
            inputPrice = inPrice;
            outputPrice = outPrice;
        }

        foreach (var task in tasks.Where(t => t.Description.Length == 0))
        {
            warnings.Add(new GoalPlanWarning("MissingDescription", task.Key));
        }

        return new GoalPlanResult
        {
            Success = true,
            Plan = new GoalPlan(
                tasks,
                waves,
                conflicts,
                GoalCostEstimator.Estimate(tasks, history, inputPrice, outputPrice),
                warnings,
                source,
                planningTokens)
        };
    }

    public async Task<GoalPlanResult> ArrangeAsync(
        Guid repositoryWorkspaceId,
        IReadOnlyList<GoalTaskPlan> tasks,
        decimal? inputPerMillionUsd,
        decimal? outputPerMillionUsd,
        CancellationToken cancellationToken = default)
    {
        if (tasks.Count == 0 || tasks.Count > MaxTasks)
        {
            return new GoalPlanResult { ErrorMessage = $"A goal needs between 1 and {MaxTasks} tasks." };
        }

        var workspace = await _workspaceQuery.GetByIdAsync(repositoryWorkspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            return new GoalPlanResult { NotFound = true, ErrorMessage = "Repository workspace not found." };
        }

        // Keys are renumbered by position so a removed task leaves no gap; dependencies on removed tasks vanish.
        var renamed = tasks.Select((t, i) => (Old: t.Key, New: $"t{i + 1}")).ToDictionary(x => x.Old, x => x.New, StringComparer.Ordinal);
        var arranged = tasks
            .Select((t, i) => t with
            {
                Key = renamed[t.Key],
                DependsOn = t.DependsOn.Where(renamed.ContainsKey).Select(d => renamed[d]).Where(d => d != renamed[t.Key]).Distinct().ToList(),
                Size = GoalCostEstimator.NormalizeSize(t.Size)
            })
            .ToList();

        var (waves, conflicts) = GoalWavePlanner.Plan(arranged, await ModeAsync(workspace.Id, cancellationToken).ConfigureAwait(false));
        var history = await _history.GetRecentUsageAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
        var warnings = arranged
            .Where(t => t.Areas.Count == 0)
            .Select(t => new GoalPlanWarning("AreasUnknown", t.Key))
            .Concat(arranged.Where(t => t.Description.Length == 0).Select(t => new GoalPlanWarning("MissingDescription", t.Key)))
            .ToList();

        return new GoalPlanResult
        {
            Success = true,
            Plan = new GoalPlan(
                arranged,
                waves,
                conflicts,
                GoalCostEstimator.Estimate(arranged, history, inputPerMillionUsd, outputPerMillionUsd),
                warnings,
                "ai",
                0)
        };
    }

    /// <summary>The repository's conflict mode; a repository that never configured automation gets the balanced default.</summary>
    private async Task<ConflictMode> ModeAsync(Guid workspaceId, CancellationToken ct) =>
        (await _policies.GetAsync(workspaceId, ct).ConfigureAwait(false))?.ConflictMode ?? ConflictMode.Balanced;

    private async Task<AiResponse?> SendAsync(string text, string context, CancellationToken ct)
    {
        try
        {
            return await _ai.SendAsync(
                new AiRequest
                {
                    Stage = AiStage.Planning,
                    SystemPrompt = SystemPrompt,
                    UserPrompt = $"REQUEST:\n{text}\n\nCODE CONTEXT (most relevant parts of the repository):\n{context}",
                    MaxTokens = 6000
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The goal planner's model call threw.");
            return null;
        }
    }

    private async Task<string> BuildContextAsync(Guid workspaceId, string localPath, string text, CancellationToken ct)
    {
        try
        {
            var result = await _search
                .HandleAsync(
                    new SemanticSearchQuery
                    {
                        RepositoryWorkspaceId = workspaceId,
                        WorkspacePath = localPath,
                        QueryText = text.Length > 2000 ? text[..2000] : text,
                        MaxResults = ContextHits
                    },
                    ct)
                .ConfigureAwait(false);

            if (!result.Success || result.Hits.Count == 0)
            {
                return "(the repository index has nothing relevant; leave areas empty when unsure)";
            }

            var builder = new StringBuilder();
            foreach (var hit in result.Hits)
            {
                var snippet = hit.Chunk.Content.Length > SnippetChars ? hit.Chunk.Content[..SnippetChars] : hit.Chunk.Content;
                builder.Append("- ").AppendLine(hit.Chunk.RelativePath);
                builder.AppendLine(snippet.Replace("\r", string.Empty).Trim());
            }

            return builder.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The goal planner could not read repository context.");
            return "(repository context is unavailable)";
        }
    }

    /// <summary>Public for tests: reads the model's JSON answer, tolerating code fences and surrounding prose.</summary>
    public static List<GoalTaskPlan>? ParseTasks(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        ModelAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize<ModelAnswer>(content[start..(end + 1)], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (answer?.Tasks is null)
        {
            return null;
        }

        var tasks = new List<GoalTaskPlan>();
        foreach (var item in answer.Tasks.Where(t => !string.IsNullOrWhiteSpace(t.Title)).Take(MaxTasks))
        {
            var number = tasks.Count + 1;
            tasks.Add(new GoalTaskPlan(
                $"t{number}",
                Truncate(item.Title!.Trim(), TaskBatchParser.MaxTitleLength),
                Truncate((item.Description ?? string.Empty).Trim(), TaskBatchParser.MaxDescriptionLength),
                (item.Areas ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct().Take(8).ToList(),
                (item.DependsOn ?? new List<int>()).Where(d => d >= 1 && d < number).Select(d => $"t{d}").Distinct().ToList(),
                GoalCostEstimator.NormalizeSize(item.Size)));
        }

        return tasks;
    }

    private static List<GoalTaskPlan> FromParser(string text) =>
        TaskBatchParser.Parse(text).Drafts
            .Where(d => d.Title.Length > 0)
            .Take(MaxTasks)
            .Select((d, i) => new GoalTaskPlan(
                $"t{i + 1}",
                Truncate(d.Title, TaskBatchParser.MaxTitleLength),
                Truncate(d.Description, TaskBatchParser.MaxDescriptionLength),
                Array.Empty<string>(),
                Array.Empty<string>(),
                "medium"))
            .ToList();

    /// <summary>A task without predicted areas gets them from a code search of its own text: no model call, so it is cheap.</summary>
    private async Task<List<GoalTaskPlan>> FillMissingAreasAsync(
        Guid workspaceId,
        string localPath,
        List<GoalTaskPlan> tasks,
        List<GoalPlanWarning> warnings,
        CancellationToken ct)
    {
        var result = new List<GoalTaskPlan>(tasks.Count);
        foreach (var task in tasks)
        {
            if (task.Areas.Count > 0)
            {
                result.Add(task);
                continue;
            }

            var areas = new List<string>();
            try
            {
                var search = await _search
                    .HandleAsync(
                        new SemanticSearchQuery
                        {
                            RepositoryWorkspaceId = workspaceId,
                            WorkspacePath = localPath,
                            QueryText = $"{task.Title}\n{task.Description}".Trim(),
                            MaxResults = 8
                        },
                        ct)
                    .ConfigureAwait(false);
                if (search.Success)
                {
                    areas = search.Hits.Select(h => h.Chunk.RelativePath).Distinct().Take(AreasPerTask).ToList();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Area prediction failed for task {Key}.", task.Key);
            }

            if (areas.Count == 0)
            {
                warnings.Add(new GoalPlanWarning("AreasUnknown", task.Key));
            }

            result.Add(task with { Areas = areas, AreasGuessed = areas.Count > 0 });
        }

        return result;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed class ModelAnswer
    {
        public List<ModelTask>? Tasks { get; set; }
    }

    private sealed class ModelTask
    {
        public string? Title { get; set; }

        public string? Description { get; set; }

        public List<string>? Areas { get; set; }

        public List<int>? DependsOn { get; set; }

        public string? Size { get; set; }
    }
}
