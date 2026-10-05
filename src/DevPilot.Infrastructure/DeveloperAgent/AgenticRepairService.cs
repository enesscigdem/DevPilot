using System.Text;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.DeveloperAgent;

public sealed class AgenticRepairService : IAgenticRepairService
{
    private const int MaxPromptChars = 70_000;
    private const int RecentFullObservations = 4;
    private const int OldObservationChars = 400;
    private const int MaxConsecutiveUnparsableReplies = 3;
    private const int MaxStalledCheckRuns = 3;

    private readonly IAiProvider _aiProvider;
    private readonly IExecutionActivityRecorder? _activityRecorder;
    private readonly ILogger<AgenticRepairService> _logger;
    private readonly int _maxTurns;
    private readonly int _maxCheckRuns;

    public AgenticRepairService(
        IAiProvider aiProvider,
        ILogger<AgenticRepairService> logger,
        ExecutionReliabilityOptions? options = null,
        IExecutionActivityRecorder? activityRecorder = null)
    {
        _aiProvider = aiProvider;
        _logger = logger;
        _activityRecorder = activityRecorder;
        var effective = options ?? new ExecutionReliabilityOptions();
        _maxTurns = Math.Max(1, effective.MaxAgenticTurns);
        _maxCheckRuns = Math.Max(1, effective.MaxAgenticCheckRuns);
    }

    public async Task<AgenticRepairOutcome> RunAsync(AgenticRepairRequest request, CancellationToken cancellationToken = default)
    {
        var tools = new AgenticRepairTools(request.WorkspacePath);
        var history = new List<(string Action, string Observation)>();
        var turns = 0;
        var checkRuns = 0;
        var unparsable = 0;
        var stalled = 0;
        var bestFailureCount = int.MaxValue;
        var editedSinceLastRun = true;
        string? model = request.Model;

        AgenticRepairOutcome Finish(bool success, string reason) =>
            new(success, reason, turns, checkRuns, tools.ChangedFiles, tools.OriginalContents, model);

        while (turns < _maxTurns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            turns++;

            var response = await _aiProvider.SendAsync(
                new AiRequest
                {
                    Model = request.Model ?? string.Empty,
                    Stage = AiStage.Repair,
                    SystemPrompt = SystemPrompt,
                    UserPrompt = BuildUserPrompt(request, history),
                    MaxTokens = 4096
                },
                cancellationToken).ConfigureAwait(false);
            model = string.IsNullOrWhiteSpace(response.Model) ? model : response.Model;

            if (!response.IsSuccess)
            {
                _logger.LogWarning("Agentic repair provider call failed on turn {Turn}: {Error}", turns, response.ErrorMessage);
                return Finish(false, "ProviderError");
            }

            var action = AgenticAction.TryParse(response.Content);
            if (action == null)
            {
                if (++unparsable >= MaxConsecutiveUnparsableReplies)
                {
                    return Finish(false, "UnparsableReplies");
                }

                history.Add(("(invalid reply)", "Your reply was not a single JSON action. Reply with exactly one JSON object such as {\"tool\":\"read_file\",\"path\":\"src/App.tsx\"}."));
                continue;
            }

            unparsable = 0;
            var tool = action.Tool!.Trim().ToLowerInvariant();

            if (tool == "done")
            {
                return Finish(false, "ModelDone");
            }

            if (tool == "run_checks")
            {
                if (!editedSinceLastRun)
                {
                    history.Add(("run_checks", "Nothing was edited since the last run, so the result would be identical. Change a file first."));
                    continue;
                }

                if (checkRuns >= _maxCheckRuns)
                {
                    return Finish(false, "CheckRunBudgetExhausted");
                }

                checkRuns++;
                await RecordAsync(request, $"Agentic repair · run checks ({checkRuns}/{_maxCheckRuns})", cancellationToken).ConfigureAwait(false);
                var observation = await request.RunChecksAsync(cancellationToken).ConfigureAwait(false);
                editedSinceLastRun = false;
                if (observation.Success)
                {
                    return Finish(true, "ChecksPassed");
                }

                if (observation.FailureCount < bestFailureCount)
                {
                    bestFailureCount = observation.FailureCount;
                    stalled = 0;
                }
                else if (++stalled >= MaxStalledCheckRuns)
                {
                    return Finish(false, "NoProgress");
                }

                history.Add(("run_checks", $"FAILED ({observation.FailureCount} failing). Best so far: {bestFailureCount}.\n{observation.Summary}"));
                continue;
            }

            var result = tools.Execute(action);
            if (tool is "edit_file" or "write_file")
            {
                editedSinceLastRun |= result.StartsWith("OK", StringComparison.Ordinal);
                await RecordAsync(request, $"Agentic repair · {tool} {action.Path}", cancellationToken).ConfigureAwait(false);
            }

            history.Add((Describe(action), result));
        }

        return Finish(false, "TurnBudgetExhausted");
    }

    private async Task RecordAsync(AgenticRepairRequest request, string message, CancellationToken cancellationToken)
    {
        if (_activityRecorder == null)
        {
            return;
        }

        try
        {
            // Never the Test stage: a Completed Test-stage activity is read as "tests passed" by the stage evaluator.
            await _activityRecorder.RecordActivityAsync(
                request.ExecutionId,
                ExecutionStage.DeveloperAgent,
                ExecutionActivityStatus.Completed,
                message,
                new ExecutionActivityMetadata(EventKind: "AgenticRepairStep"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Telemetry must never fail the repair.
        }
    }

    private static string Describe(AgenticAction action) =>
        $"{action.Tool} {action.Path ?? action.Pattern}".Trim();

    private static string BuildUserPrompt(AgenticRepairRequest request, IReadOnlyList<(string Action, string Observation)> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task: {request.TaskTitle}");
        if (!string.IsNullOrWhiteSpace(request.AcceptanceCriteria))
        {
            sb.AppendLine($"Acceptance criteria: {request.AcceptanceCriteria}");
        }

        if (request.TouchedFiles.Count > 0)
        {
            sb.AppendLine("Files already changed for this task: " + string.Join(", ", request.TouchedFiles));
        }

        sb.AppendLine();
        sb.AppendLine("=== Failing verification (initial) ===");
        sb.AppendLine(request.InitialFailure);
        sb.AppendLine("=== End ===");
        sb.AppendLine();

        var transcript = new StringBuilder();
        for (var i = 0; i < history.Count; i++)
        {
            var (action, observation) = history[i];
            var recent = i >= history.Count - RecentFullObservations;
            var text = recent || observation.Length <= OldObservationChars
                ? observation
                : observation[..OldObservationChars] + " […]";
            transcript.AppendLine($"--- Step {i + 1}: {action}");
            transcript.AppendLine(text);
        }

        var transcriptText = transcript.ToString();
        if (transcriptText.Length > MaxPromptChars)
        {
            transcriptText = "[earlier steps omitted]\n" + transcriptText[^MaxPromptChars..];
        }

        if (transcriptText.Length > 0)
        {
            sb.AppendLine("=== What you have done so far ===");
            sb.AppendLine(transcriptText);
            sb.AppendLine("=== End ===");
            sb.AppendLine();
        }

        sb.AppendLine("Reply with exactly one JSON action.");
        return sb.ToString();
    }

    internal const string SystemPrompt = """
        You are repairing a failing build or test run inside a project's checked-out workspace, working like a careful developer.
        Each turn you reply with EXACTLY ONE JSON object and nothing else. The tool result comes back on the next turn.

        Tools:
        {"tool":"list_dir","path":"src"}
        {"tool":"read_file","path":"src/App.tsx","startLine":1,"endLine":200}
        {"tool":"search","pattern":"execCommand","path":"src"}            (regex or literal, case-insensitive)
        {"tool":"edit_file","path":"src/App.tsx","searchReplaceEdits":[{"search":"exact unique excerpt","replace":"new text"}]}
        {"tool":"write_file","path":"src/new.ts","content":"complete file"}   (new files only)
        {"tool":"run_checks"}                                               (runs the repository's own build and tests)
        {"tool":"done","summary":"why you stopped"}

        Rules:
        - Investigate before editing: read the failing test and the source it exercises, and look at the project's config
          (package.json, test runner and environment such as jsdom) when the error may be environmental.
        - Fix the cause. Never delete, skip, or weaken tests or assertions to make them pass.
        - Browser-only APIs (e.g. document.execCommand, scrollIntoView, matchMedia) do not exist in jsdom: either implement the
          behaviour with APIs that do, or stub the API in the test setup and assert only what the stub can produce.
        - Each edit_file 'search' must match the file exactly once; read the file first and copy the text exactly.
        - Fix all related failures before calling run_checks; each run costs time.
        - Do not edit lockfiles or CI configuration. Do not run any command other than run_checks.
        - Call done only if you cannot make further progress.
        """;
}
