using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevPilot.Application.AiProviders;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.TaskImpactAnalysis.Dtos;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Application.TaskImpactAnalysis.Services;
using DevPilot.Application.Tasks.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using DevPilot.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using TaskImpactAnalysisEntity = DevPilot.Domain.Entities.TaskImpactAnalysis;

namespace DevPilot.Application.TaskImpactAnalysis.Commands.AnalyzeTaskImpact;

public interface IAnalyzeTaskImpactCommandHandler
{
    Task<AnalyzeTaskImpactResult> HandleAsync(
        AnalyzeTaskImpactCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class AnalyzeTaskImpactCommandHandler : IAnalyzeTaskImpactCommandHandler
{
    private const int MaxRelevantChunks = 8;
    private const int MaxChunkCharacters = 1500;
    private const int MaxRoslynProjects = 10;
    private const int MaxTypesPerProject = 15;
    private const int MaxControllersPerProject = 10;
    private const int MaxActionsPerController = 5;
    private const int MaxCompilationErrors = 5;

    private const string SystemPrompt =
        "You are DevPilot's Change Intelligence and impact analysis engine. " +
        "Analyze the impact of a software change request against actual repository evidence. " +
        "Respond with a single compact JSON object only. Do not wrap it in markdown code fences and do not add commentary. " +
        "Keep all descriptions concise (1-2 sentences max). Do not duplicate file lists or descriptions across sections. " +
        "All confidence values are integers between 0 and 100. " +
        "changeType: Add, Modify, Delete, or Refactor; impactLevel and risk level: Low, Medium, High, or Critical. " +
        "Supported change dimension areas: CODE, API, DATA, TESTS, RUNTIME, DEPENDENCIES, INFRASTRUCTURE. " +
        "Emit a dimension ONLY when supported by repository evidence. " +
        "Unknowns must be first-class output — never guess unknown deployment, database rollback, or external contracts.";

    private const string JsonSchema = @"{
  ""summary"": ""Concise 1-2 sentence technical summary (max 200 chars)."",
  ""confidence"": 85,
  ""impactedFiles"": [
    {
      ""filePath"": ""relative/path/to/file.cs"",
      ""changeType"": ""Modify"",
      ""reason"": ""Concise 1-sentence reason (max 100 chars)""
    }
  ],
  ""dimensions"": [
    {
      ""area"": ""API / DATA / TESTS / RUNTIME / DEPENDENCIES / INFRASTRUCTURE"",
      ""impactLevel"": ""Low / Medium / High / Critical"",
      ""summary"": ""Concise 1-sentence summary (max 120 chars)""
    }
  ],
  ""proposedPlan"": [
    {
      ""order"": 1,
      ""title"": ""Concise step title (max 50 chars)"",
      ""description"": ""Concise step description (max 120 chars)""
    }
  ],
  ""risks"": [
    {
      ""level"": ""Low / Medium / High / Critical"",
      ""description"": ""Concise risk description (max 120 chars)""
    }
  ],
  ""unknowns"": [
    ""Concise unknown item (max 100 chars)""
  ]
}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ITaskRepository _taskRepository;
    private readonly IRepositoryWorkspaceQuery _workspaceQuery;
    private readonly IImpactAnalysisRepository _analysisRepository;
    private readonly IRepositoryAnalyzer _repositoryAnalyzer;
    private readonly IAiProvider _aiProvider;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ISemanticSearchService _semanticSearchService;
    private readonly IRepositoryCheckRunner? _repositoryCheckRunner;
    private readonly ILogger<AnalyzeTaskImpactCommandHandler> _logger;

    public AnalyzeTaskImpactCommandHandler(
        ITaskRepository taskRepository,
        IRepositoryWorkspaceQuery workspaceQuery,
        IImpactAnalysisRepository analysisRepository,
        IRepositoryAnalyzer repositoryAnalyzer,
        IAiProvider aiProvider,
        IEmbeddingProvider embeddingProvider,
        ISemanticSearchService semanticSearchService,
        ILogger<AnalyzeTaskImpactCommandHandler> logger,
        IRepositoryCheckRunner? repositoryCheckRunner = null)
    {
        _taskRepository = taskRepository;
        _workspaceQuery = workspaceQuery;
        _analysisRepository = analysisRepository;
        _repositoryAnalyzer = repositoryAnalyzer;
        _aiProvider = aiProvider;
        _embeddingProvider = embeddingProvider;
        _semanticSearchService = semanticSearchService;
        _logger = logger;
        _repositoryCheckRunner = repositoryCheckRunner;
    }

    public async Task<AnalyzeTaskImpactResult> HandleAsync(
        AnalyzeTaskImpactCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.TaskId == Guid.Empty)
        {
            return new AnalyzeTaskImpactResult
            {
                Success = false,
                ErrorMessage = "Task id must be provided.",
            };
        }

        DevelopmentTask task;
        try
        {
            var loadedTask = await _taskRepository
                .GetByIdAsync(command.TaskId, cancellationToken)
                .ConfigureAwait(false);

            if (loadedTask is null)
            {
                return new AnalyzeTaskImpactResult
                {
                    Success = false,
                    NotFound = true,
                    ErrorMessage = "Task not found.",
                };
            }

            task = loadedTask;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Failed to load development task {TaskId} for impact analysis.",
                command.TaskId);

            return new AnalyzeTaskImpactResult
            {
                Success = false,
                ErrorMessage = "Failed to load the task.",
            };
        }

        if (task.Status is DevelopmentTaskStatus.Executing or DevelopmentTaskStatus.Completed)
        {
            return new AnalyzeTaskImpactResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = $"Cannot analyze impact for a task in '{task.Status}' status.",
            };
        }

        // Auto-reconcile any stale InProgress analysis before evaluating active state
        try
        {
            await _analysisRepository
                .ReconcileStaleAnalysesAsync(DateTime.UtcNow - TimeSpan.FromMinutes(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to reconcile stale analyses before starting analysis for task {TaskId}.", task.Id);
        }

        // Optimistic pre-check for active analysis
        var hasActive = await _analysisRepository
            .HasActiveAnalysisForTaskAsync(command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (hasActive)
        {
            return new AnalyzeTaskImpactResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = "An impact analysis is already in progress for this task.",
            };
        }

        var workspace = await _workspaceQuery
            .GetByIdAsync(task.RepositoryWorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        if (workspace is null)
        {
            return new AnalyzeTaskImpactResult
            {
                Success = false,
                ErrorMessage = "Repository workspace not found.",
            };
        }

        var workspaceError = ValidateWorkspace(workspace);
        if (!string.IsNullOrEmpty(workspaceError))
        {
            return new AnalyzeTaskImpactResult
            {
                Success = false,
                ErrorMessage = workspaceError,
            };
        }

        var now = DateTime.UtcNow;
        var analysis = new TaskImpactAnalysisEntity
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.InProgress,
            Summary = string.Empty,
            Confidence = 0,
            CreatedAt = now,
            CompletedAt = null,
            ErrorMessage = null,
        };

        task.Status = DevelopmentTaskStatus.Analyzing;
        task.UpdatedAt = now;

        var persisted = await _analysisRepository
            .StartAnalysisAtomicAsync(analysis, task, cancellationToken)
            .ConfigureAwait(false);

        if (!persisted)
        {
            return new AnalyzeTaskImpactResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = "An impact analysis is already in progress for this task.",
            };
        }

        string? rawResponse = null;
        string? model = null;
        var providerName = _aiProvider.ProviderName;

        // Use a server-owned timeout token so that client disconnect / browser refresh
        // does not abort the server-side analysis execution.
        using var serverTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var executionToken = serverTimeoutCts.Token;

        try
        {
            // Discover preflight repository verification profile (reuses PR #15 discovery logic)
            RepositoryProfile verificationProfile;
            if (_repositoryCheckRunner != null && !string.IsNullOrWhiteSpace(workspace.LocalPath) && Directory.Exists(workspace.LocalPath))
            {
                try
                {
                    verificationProfile = await _repositoryCheckRunner.DiscoverAsync(
                        new RepositoryPreflightRequest(workspace.LocalPath, workspace.Branch),
                        executionToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Preflight verification discovery failed for workspace {WorkspaceId}", workspace.Id);
                    verificationProfile = new RepositoryProfile(
                        State: RepositoryVerificationState.InfrastructureFailure,
                        Ecosystems: Array.Empty<string>(),
                        Checks: Array.Empty<RepositoryCheck>(),
                        Message: "Preflight verification discovery encountered an error.");
                }
            }
            else
            {
                verificationProfile = new RepositoryProfile(
                    State: RepositoryVerificationState.Unconfigured,
                    Ecosystems: Array.Empty<string>(),
                    Checks: Array.Empty<RepositoryCheck>(),
                    Message: "Verification runner not configured.");
            }

            var (context, roslynResult) = await BuildContextWithRoslynAsync(task, workspace, executionToken).ConfigureAwait(false);
            var evidenceProfile = ChangeIntelligenceEvidenceCollector.CollectEvidence(workspace.LocalPath, verificationProfile, roslynResult);

            const int defaultImpactMaxTokens = 2048;
            var totalProviderCalls = 1;
            var compactRecoveryCount = 0;

            var aiRequest = new AiRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = BuildUserPrompt(task, workspace, context, evidenceProfile),
                MaxTokens = defaultImpactMaxTokens,
            };

            var stopwatch = Stopwatch.StartNew();
            var aiResponse = await _aiProvider
                .SendAsync(aiRequest, executionToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            rawResponse = aiResponse.Content;
            model = aiResponse.Model;
            providerName = string.IsNullOrWhiteSpace(aiResponse.Provider)
                ? _aiProvider.ProviderName
                : aiResponse.Provider;

            var isTruncated = aiResponse.FailureKind == AiFailureKind.TokenLimitExceeded ||
                              string.Equals(aiResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                              (aiResponse.ErrorMessage != null && aiResponse.ErrorMessage.Contains("exhausted the configured output token limit", StringComparison.OrdinalIgnoreCase));

            if (isTruncated)
            {
                _logger.LogWarning(
                    "Impact analysis for task {TaskId} truncated on initial attempt (FinishReason: {FinishReason}). Initiating single compact recovery (budget: {Budget} tokens).",
                    task.Id,
                    aiResponse.FinishReason ?? "length",
                    defaultImpactMaxTokens);

                compactRecoveryCount = 1;
                totalProviderCalls++;

                var recoveryPrompt = BuildImpactTruncationRecoveryPrompt(task, workspace, context, evidenceProfile);
                var recoveryRequest = new AiRequest
                {
                    SystemPrompt =
                        "You are DevPilot's compact impact analysis recovery engine. " +
                        "The previous response was truncated because it exceeded output token limits. " +
                        "Output ONLY the minimal, ultra-compact JSON object matching the required schema. " +
                        "No explanations, no markdown fences, no conversational text. " +
                        "Keep summaries and reasons to 1 concise sentence. Output valid JSON only.",
                    UserPrompt = recoveryPrompt,
                    MaxTokens = defaultImpactMaxTokens,
                };

                var recoveryResponse = await _aiProvider
                    .SendAsync(recoveryRequest, executionToken)
                    .ConfigureAwait(false);

                if (!recoveryResponse.IsSuccess)
                {
                    var isRecoveryTruncated = recoveryResponse.FailureKind == AiFailureKind.TokenLimitExceeded ||
                                              string.Equals(recoveryResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                                              (recoveryResponse.ErrorMessage != null && recoveryResponse.ErrorMessage.Contains("exhausted the configured output token limit", StringComparison.OrdinalIgnoreCase));

                    var errorMsg = isRecoveryTruncated
                        ? "Impact analysis failed: AI response exhausted token limit on initial call and compact recovery also truncated."
                        : $"Impact analysis failed: Compact truncation recovery failed: {recoveryResponse.ErrorMessage ?? "Unknown provider error."}";

                    return await FailAnalysisAsync(
                        analysis,
                        task,
                        errorMsg,
                        recoveryResponse.Content,
                        recoveryResponse.Model ?? model,
                        recoveryResponse.Provider ?? providerName,
                        executionToken).ConfigureAwait(false);
                }

                rawResponse = recoveryResponse.Content;
                model = recoveryResponse.Model ?? model;
                providerName = string.IsNullOrWhiteSpace(recoveryResponse.Provider) ? providerName : recoveryResponse.Provider;
            }
            else if (!aiResponse.IsSuccess)
            {
                return await FailAnalysisAsync(
                    analysis,
                    task,
                    aiResponse.ErrorMessage ?? "AI provider returned an unsuccessful response.",
                    rawResponse,
                    model,
                    providerName,
                    executionToken).ConfigureAwait(false);
            }

            var parseResult = TryParseStructuredResult(rawResponse, evidenceProfile, workspace.LocalPath);

            // Bounded single repair attempt if deterministic grounding failure occurs (ONLY if truncation recovery was not used)
            if (!parseResult.Success && parseResult.IsGroundingError && parseResult.GroundingErrorDetails != null && compactRecoveryCount == 0)
            {
                _logger.LogWarning(
                    "Impact analysis for task {TaskId} encountered grounding error: {Error}. Initiating bounded 1-attempt impact plan repair.",
                    task.Id,
                    parseResult.ErrorMessage);

                totalProviderCalls++;
                var repairPrompt = BuildImpactPlanRepairPrompt(
                    task,
                    workspace,
                    parseResult.GroundingErrorDetails,
                    evidenceProfile);

                var repairAiRequest = new AiRequest
                {
                    SystemPrompt =
                        "You are DevPilot's impact analysis repair engine. " +
                        "Correct the deterministic grounding errors in the proposed impact analysis. " +
                        "Preserve all valid entries. Use ONLY real existing repository file paths for Modify/Delete. " +
                        "Use Create/Add ONLY for new files that do not currently exist in the repository. " +
                        "Respond with a single JSON object only matching the required schema. Do not wrap in markdown fences or commentary.",
                    UserPrompt = repairPrompt,
                    MaxTokens = defaultImpactMaxTokens,
                };

                var repairAiResponse = await _aiProvider
                    .SendAsync(repairAiRequest, executionToken)
                    .ConfigureAwait(false);

                if (repairAiResponse.IsSuccess && !string.IsNullOrWhiteSpace(repairAiResponse.Content))
                {
                    rawResponse = repairAiResponse.Content;
                    model = repairAiResponse.Model ?? model;
                    providerName = string.IsNullOrWhiteSpace(repairAiResponse.Provider) ? providerName : repairAiResponse.Provider;

                    parseResult = TryParseStructuredResult(rawResponse, evidenceProfile, workspace.LocalPath);
                }
                else
                {
                    _logger.LogWarning(
                        "Impact plan repair call failed for task {TaskId}: {Error}",
                        task.Id,
                        repairAiResponse.ErrorMessage);
                }
            }

            if (!parseResult.Success)
            {
                return await FailAnalysisAsync(
                    analysis,
                    task,
                    parseResult.ErrorMessage ?? "Failed to parse the AI response.",
                    rawResponse,
                    model,
                    providerName,
                    executionToken).ConfigureAwait(false);
            }

            var completedAt = DateTime.UtcNow;
            var structuredResult = parseResult.ResultData!;

            analysis.Status = ImpactAnalysisStatus.Completed;
            analysis.Summary = structuredResult.Summary;
            analysis.Confidence = structuredResult.Confidence;
            analysis.Model = model;
            analysis.ProviderName = providerName;
            analysis.RawResponse = rawResponse;
            analysis.StructuredResult = structuredResult;
            analysis.CompletedAt = completedAt;
            analysis.ErrorMessage = null;

            await _analysisRepository
                .UpdateAsync(analysis, executionToken)
                .ConfigureAwait(false);

            task.Status = DevelopmentTaskStatus.AwaitingApproval;
            task.UpdatedAt = completedAt;
            await _taskRepository.UpdateAsync(task, executionToken).ConfigureAwait(false);

            var finishReason = aiResponse.FinishReason ?? "stop";
            var actualOutputTokens = aiResponse.OutputTokens;

            _logger.LogInformation(
                "Change intelligence impact analysis {AnalysisId} completed for task {TaskId} in {ElapsedMs}ms. " +
                "ProviderCalls: {ProviderCalls}, CompactRecoveries: {CompactRecoveries}, RequestedBudget: {RequestedBudget}, OutputTokens: {OutputTokens}, FinishReason: {FinishReason}.",
                analysis.Id,
                task.Id,
                stopwatch.ElapsedMilliseconds,
                totalProviderCalls,
                compactRecoveryCount,
                defaultImpactMaxTokens,
                actualOutputTokens?.ToString() ?? "unknown",
                finishReason);

            return new AnalyzeTaskImpactResult
            {
                Success = true,
                AnalysisId = analysis.Id,
                Analysis = MapToDto(analysis),
            };
        }
        catch (OperationCanceledException)
        {
            return await FailAnalysisAsync(
                analysis,
                task,
                "Impact analysis operation timed out.",
                rawResponse,
                model,
                providerName,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Unexpected error while analyzing impact for task {TaskId}.",
                task.Id);

            return await FailAnalysisAsync(
                analysis,
                task,
                "An unexpected error occurred during impact analysis.",
                rawResponse,
                model,
                providerName,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string? ValidateWorkspace(RepositoryWorkspace workspace)
    {
        if (workspace.Status is not RepositoryWorkspaceStatus.Completed
            and not RepositoryWorkspaceStatus.AlreadyExists)
        {
            return $"Repository workspace is not ready (status: {workspace.Status}).";
        }

        if (string.IsNullOrWhiteSpace(workspace.LocalPath))
        {
            return "Repository workspace has no local path.";
        }

        return null;
    }

    private async Task<(string ContextText, RepositoryAnalysisResult? RoslynResult)> BuildContextWithRoslynAsync(
        DevelopmentTask task,
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();

        RepositoryAnalysisResult? roslynResult = null;
        try
        {
            roslynResult = await _repositoryAnalyzer
                .AnalyzeAsync(
                    new RepositoryAnalysisRequest { WorkspacePath = workspace.LocalPath },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Roslyn repository analysis failed for workspace {WorkspaceId}.",
                workspace.Id);
        }

        AppendRoslynContext(builder, roslynResult);

        var semanticContext = await TryBuildSemanticContextAsync(task, workspace, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(semanticContext))
        {
            builder.AppendLine();
            builder.AppendLine(semanticContext);
        }

        if (builder.Length == 0)
        {
            builder.AppendLine("No structural or semantic context was available for this workspace.");
        }

        return (builder.ToString(), roslynResult);
    }

    private static void AppendRoslynContext(
        StringBuilder builder,
        RepositoryAnalysisResult? result)
    {
        builder.AppendLine("## Repository structure (Roslyn)");

        if (result is null)
        {
            builder.AppendLine("- Roslyn analysis was not available.");
            return;
        }

        if (!result.Success)
        {
            builder.AppendLine($"- Roslyn analysis reported an error: {result.Error ?? "unknown"}");
        }

        var projects = result.Solutions
            .SelectMany(s => s.Projects)
            .Concat(result.StandaloneProjects)
            .Take(MaxRoslynProjects)
            .ToList();

        if (projects.Count == 0)
        {
            builder.AppendLine("- No projects were discovered in the workspace.");
            return;
        }

        foreach (var project in projects)
        {
            builder.AppendLine(
                $"- Project {project.Name} ({project.ProjectType}, target: {project.TargetFramework ?? "unknown"})");

            if (!project.CompilationSucceeded)
            {
                var errors = project.CompilationErrors.Take(MaxCompilationErrors);
                builder.AppendLine(
                    $"  - Compilation failed with {project.CompilationErrors.Count} errors. First errors: {string.Join("; ", errors)}");
            }

            var controllers = project.Controllers.Take(MaxControllersPerProject).ToList();
            foreach (var controller in controllers)
            {
                var actions = controller.Actions
                    .Take(MaxActionsPerController)
                    .Select(a => $"{a.HttpMethod ?? "HTTP"} {a.Name}");

                builder.AppendLine(
                    $"  - Controller {controller.Name}: {string.Join(", ", actions)}");
            }

            var typeNames = project.Classes
                .Cast<TypeAnalysisResult>()
                .Concat(project.Interfaces)
                .Concat(project.Records)
                .Take(MaxTypesPerProject)
                .Select(t => string.IsNullOrWhiteSpace(t.Namespace) ? t.Name : $"{t.Namespace}.{t.Name}");

            var typesList = string.Join(", ", typeNames);
            if (!string.IsNullOrWhiteSpace(typesList))
            {
                builder.AppendLine($"  - Types: {typesList}");
            }
        }
    }

    private async Task<string?> TryBuildSemanticContextAsync(
        DevelopmentTask task,
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var queryText = BuildSemanticQueryText(task);

        EmbeddingResult embeddingResult;
        try
        {
            embeddingResult = await _embeddingProvider
                .GenerateAsync(new[] { queryText }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Embedding generation failed for task {TaskId}; skipping semantic context.",
                task.Id);
            return null;
        }

        if (!embeddingResult.Success ||
            embeddingResult.Embeddings.Count == 0 ||
            embeddingResult.Embeddings[0] is null)
        {
            _logger.LogWarning(
                "Embedding provider returned no embeddings for task {TaskId}; skipping semantic context.",
                task.Id);
            return null;
        }

        var searchResult = await _semanticSearchService
            .SearchAsync(
                new SemanticSearchQuery
                {
                    WorkspacePath = workspace.LocalPath,
                    QueryText = queryText,
                    MaxResults = MaxRelevantChunks,
                },
                embeddingResult.Embeddings[0]!,
                cancellationToken)
            .ConfigureAwait(false);

        if (!searchResult.Success || searchResult.Hits.Count == 0)
        {
            _logger.LogWarning(
                "Semantic search returned no results for task {TaskId}: {Error}",
                task.Id,
                searchResult.ErrorMessage ?? "no hits");
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## Relevant code (Project Brain semantic search)");

        foreach (var hit in searchResult.Hits.Take(MaxRelevantChunks))
        {
            var chunk = hit.Chunk;
            builder.AppendLine();
            builder.AppendLine(
                $"### {chunk.RelativePath} (lines {chunk.StartLine}-{chunk.EndLine}, score {hit.Score:F3})");

            if (!string.IsNullOrWhiteSpace(chunk.SymbolName))
            {
                builder.AppendLine($"Symbol: {chunk.SymbolName}");
            }

            builder.AppendLine("```");
            builder.AppendLine(Truncate(chunk.Content, MaxChunkCharacters));
            builder.AppendLine("```");
        }

        return builder.ToString();
    }

    private static string BuildSemanticQueryText(DevelopmentTask task)
    {
        var builder = new StringBuilder();
        builder.Append(task.Title);
        builder.Append(' ');
        builder.Append(task.Description);

        if (!string.IsNullOrWhiteSpace(task.AcceptanceCriteria))
        {
            builder.Append(' ');
            builder.Append(task.AcceptanceCriteria);
        }

        return builder.ToString().Trim();
    }

    private static string BuildRepositoryFileInventory(string workspacePath, int maxFiles = 250)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return string.Empty;

        try
        {
            var canonical = Path.GetFullPath(workspacePath);
            var files = ProjectGraphHelper.SafeFindFiles(canonical, "*.cs")
                .Select(f => Path.GetRelativePath(canonical, f).Replace('\\', '/'))
                .Where(f => !f.StartsWith("..", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(maxFiles)
                .ToList();

            if (files.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("# Existing Repository Files (Grounding Inventory)");
            foreach (var file in files)
            {
                sb.AppendLine($"- {file}");
            }
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<string> FindCandidatePaths(string targetPath, string workspaceLocalPath, IReadOnlyList<string> projectRoots)
    {
        var candidates = new List<string>();
        if (string.IsNullOrWhiteSpace(workspaceLocalPath) || !Directory.Exists(workspaceLocalPath))
            return candidates;

        try
        {
            var canonical = Path.GetFullPath(workspaceLocalPath);
            var allCsFiles = ProjectGraphHelper.SafeFindFiles(canonical, "*.cs")
                .Select(f => Path.GetRelativePath(canonical, f).Replace('\\', '/'))
                .Where(f => !f.StartsWith("..", StringComparison.Ordinal))
                .ToList();

            var targetFileName = Path.GetFileNameWithoutExtension(targetPath);

            // 1. Files containing target stem / domain name
            var stemMatches = allCsFiles
                .Where(f => !string.IsNullOrEmpty(targetFileName) && Path.GetFileName(f).Contains(targetFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            candidates.AddRange(stemMatches);

            // 2. Files in same project folder
            var targetProjectRoot = projectRoots?.FirstOrDefault(r =>
                !string.IsNullOrEmpty(r) && targetPath.StartsWith(r.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));

            if (targetProjectRoot != null)
            {
                var projectFiles = allCsFiles
                    .Where(f => f.StartsWith(targetProjectRoot.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                    .Take(25);
                candidates.AddRange(projectFiles);
            }

            // 3. Fallback to all files
            candidates.AddRange(allCsFiles.Take(30));

            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return candidates;
        }
    }

    private static string BuildImpactPlanRepairPrompt(
        DevelopmentTask task,
        RepositoryWorkspace workspace,
        ImpactGroundingErrorDetails errorDetails,
        RepositoryEvidenceProfile evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Task");
        builder.AppendLine($"Title: {task.Title}");
        builder.AppendLine($"Description: {task.Description}");
        builder.AppendLine();

        builder.AppendLine("# Grounding Error Detected in Previous Impact Plan");
        builder.AppendLine($"Error: {errorDetails.ExactError}");
        builder.AppendLine($"Invalid Entry: '{errorDetails.InvalidFilePath}' with Action '{errorDetails.InvalidChangeType}'");
        builder.AppendLine();

        if (errorDetails.ValidImpactedFiles.Count > 0)
        {
            builder.AppendLine("# Valid Impacted Entries to Preserve");
            foreach (var vf in errorDetails.ValidImpactedFiles)
            {
                builder.AppendLine($"- {vf.FilePath} ({vf.ChangeType}): {vf.Reason}");
            }
            builder.AppendLine();
        }

        if (errorDetails.CandidateRepositoryPaths.Count > 0)
        {
            builder.AppendLine("# Available Real Repository Files for Selection");
            foreach (var cf in errorDetails.CandidateRepositoryPaths.Take(50))
            {
                builder.AppendLine($"- {cf}");
            }
            builder.AppendLine();
        }

        builder.AppendLine("# Instructions for Repair");
        builder.AppendLine(
            "1. Correct ONLY the invalid impacted entries while preserving all valid entries.\n" +
            "2. For changeType 'Modify' or 'Delete': Select ONLY from the available real repository files listed above. Do NOT propose nonexistent files.\n" +
            "3. For changeType 'Add' (or 'Create'): Use Add ONLY for genuinely new files that do not currently exist in the repository.\n" +
            "4. Return the complete corrected impact analysis as a single JSON object matching the schema below (no markdown fences, no commentary):");
        builder.AppendLine(JsonSchema);

        return builder.ToString();
    }

    private static string BuildImpactTruncationRecoveryPrompt(
        DevelopmentTask task,
        RepositoryWorkspace workspace,
        string context,
        RepositoryEvidenceProfile evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Task");
        builder.AppendLine($"Title: {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            builder.AppendLine($"Description: {task.Description}");
        }
        if (!string.IsNullOrWhiteSpace(task.AcceptanceCriteria))
        {
            builder.AppendLine($"Acceptance Criteria: {task.AcceptanceCriteria}");
        }
        builder.AppendLine();

        builder.AppendLine("# Candidate Repository Files");
        if (evidence.InventoryCsFiles.Count > 0)
        {
            foreach (var f in evidence.InventoryCsFiles.Take(25))
            {
                builder.AppendLine($"- {f}");
            }
            builder.AppendLine();
        }

        builder.AppendLine("# Minimal Compact Output Instructions");
        builder.AppendLine(
            "CRITICAL: The previous response was truncated because it exceeded output token limits.\n" +
            "Provide ONLY the minimal, ultra-compact JSON impact analysis without extra prose or nested duplicate fields.\n" +
            "- Output valid JSON only, no markdown code blocks.\n" +
            "- Max 200 chars for summary.\n" +
            "- Max 100 chars per file reason.\n" +
            "- Max 100 chars per plan step description.\n" +
            "- Do NOT repeat change briefs, expected checks, or file evidence details (DevPilot computes them deterministically).");
        builder.AppendLine();
        builder.AppendLine("Required JSON Schema:");
        builder.AppendLine(JsonSchema);

        return builder.ToString();
    }

    private static string BuildUserPrompt(
        DevelopmentTask task,
        RepositoryWorkspace workspace,
        string context,
        RepositoryEvidenceProfile evidence)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# Task");
        builder.AppendLine($"Title: {task.Title}");
        builder.AppendLine($"Description: {task.Description}");

        if (!string.IsNullOrWhiteSpace(task.AcceptanceCriteria))
        {
            builder.AppendLine($"Acceptance criteria: {task.AcceptanceCriteria}");
        }

        builder.AppendLine($"Priority: {task.Priority}");
        builder.AppendLine();

        builder.AppendLine("# Repository");
        builder.AppendLine(
            $"{workspace.Owner}/{workspace.Repository} (branch: {workspace.Branch}, commit: {workspace.CommitSha})");
        builder.AppendLine();

        builder.AppendLine("# Discovered .NET Project Graph");
        if (evidence.ProjectGraph != null && evidence.ProjectGraph.Count > 0)
        {
            foreach (var proj in evidence.ProjectGraph)
            {
                var pkgList = proj.PackageReferences.Count > 0 ? string.Join(", ", proj.PackageReferences) : "none";
                var projRefList = proj.ProjectReferences.Count > 0 ? string.Join(", ", proj.ProjectReferences) : "none";
                builder.AppendLine($"- Project: {proj.ProjectPath} (Name: {proj.ProjectName}, Directory: {proj.ProjectDirectory}, TestProject: {proj.IsTestProject})");
                builder.AppendLine($"  PackageReferences: [{pkgList}]");
                builder.AppendLine($"  ProjectReferences: [{projRefList}]");
            }
        }
        else
        {
            builder.AppendLine("- No projects were discovered in the workspace.");
        }
        builder.AppendLine();

        builder.AppendLine("# Repository Verification Preflight");
        if (evidence.VerificationProfile.State == RepositoryVerificationState.Configured && evidence.VerificationProfile.Checks.Count > 0)
        {
            builder.AppendLine($"- State: Configured ({evidence.VerificationProfile.Checks.Count} checks)");
            foreach (var check in evidence.VerificationProfile.Checks)
            {
                builder.AppendLine($"  - [{check.Kind}] {check.DisplayName} (Required: {check.Required}, Source: {check.Source})");
            }
        }
        else if (evidence.VerificationProfile.State == RepositoryVerificationState.Unconfigured)
        {
            builder.AppendLine($"- State: Unconfigured ({evidence.VerificationProfile.Message ?? "No trustworthy verification checks discovered in repository"})");
        }
        else
        {
            builder.AppendLine($"- State: Infrastructure Failure ({evidence.VerificationProfile.Message ?? "Preflight error"})");
        }
        builder.AppendLine();

        builder.AppendLine("# Database & Migration Intelligence");
        if (evidence.HasEfCore)
        {
            builder.AppendLine("- Migration Mechanism: EF Core Migrations detected in package references.");
            if (evidence.MigrationFiles.Count > 0)
            {
                builder.AppendLine($"- Existing Migrations: {evidence.MigrationFiles.Count} migration/snapshot file(s) found in repository.");
            }
            if (evidence.PersistenceFiles.Count > 0)
            {
                builder.AppendLine($"- Persistence Entities & DbContext: {evidence.PersistenceFiles.Count} file(s) found.");
            }
        }
        else
        {
            builder.AppendLine("- No EF Core migration framework detected in project references.");
        }
        builder.AppendLine();

        var inventory = BuildRepositoryFileInventory(workspace.LocalPath);
        if (!string.IsNullOrWhiteSpace(inventory))
        {
            builder.AppendLine(inventory);
            builder.AppendLine();
        }

        builder.AppendLine("# Context");
        builder.AppendLine(context);
        builder.AppendLine();

        builder.AppendLine("# Instructions");
        builder.AppendLine(
            "Analyze the impact of implementing this task on the repository and produce evidence-backed Change Intelligence. " +
            "Respond with a single compact JSON object only, no markdown fences, no extra commentary.\n" +
            "CRITICAL CHANGE INTELLIGENCE RULES:\n" +
            "1. All proposed C# (*.cs) file paths MUST be located within one of the discovered .NET project directories listed above.\n" +
            "2. For changeType 'Modify' or 'Delete': The file path MUST EXACTLY match an existing file from the 'Existing Repository Files' inventory above. Never invent a file path for Modify or Delete.\n" +
            "3. For changeType 'Add' (or 'Create'): Use Add ONLY for genuinely NEW files that do not currently exist in the repository inventory.\n" +
            "4. Unit and integration test files MUST be placed in an existing discovered test project.\n" +
            "5. Keep all strings short (1-2 sentences max). Do not duplicate file lists inside plan or dimensions.\n" +
            "6. Database/migration statements must remain probabilistic ('migration likely/expected') unless deterministic repository evidence proves otherwise.\n" +
            "7. Supported dimensions: CODE, API, DATA, TESTS, RUNTIME, DEPENDENCIES, INFRASTRUCTURE. Emit ONLY dimensions supported by repository evidence.\n" +
            "8. Unknowns must be explicit first-class outputs (e.g. unconfigured tests, deployment sequencing, external contracts).\n" +
            "9. STRICT ARCHITECTURAL GROUNDING: Strictly adhere to existing architectural patterns, interfaces, and libraries referenced in the project graph.\n" +
            "Confidence must be an integer 0-100. Use the following schema:");
        builder.AppendLine(JsonSchema);

        return builder.ToString();
    }

    public static ParseResult TryParseStructuredResult(
        string rawResponse,
        RepositoryEvidenceProfile evidence,
        string workspaceLocalPath)
    {
        var json = ExtractJson(rawResponse);

        if (string.IsNullOrWhiteSpace(json))
        {
            return ParseResult.Failure("The AI response did not contain a JSON object.");
        }

        ImpactAnalysisResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<ImpactAnalysisResponse>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            return ParseResult.Failure($"Failed to deserialize the AI response as JSON: {exception.Message}");
        }

        if (response is null)
        {
            return ParseResult.Failure("The AI response deserialized to null.");
        }

        return MapToResultData(response, evidence, workspaceLocalPath);
    }

    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return trimmed;
        }

        return trimmed[start..(end + 1)];
    }

    private static ParseResult MapToResultData(
        ImpactAnalysisResponse response,
        RepositoryEvidenceProfile evidence,
        string workspaceLocalPath)
    {
        if (string.IsNullOrWhiteSpace(response.Summary))
        {
            return ParseResult.Failure("The AI response is missing a summary.");
        }

        var effectiveGraph = evidence.ProjectGraph ?? Array.Empty<DiscoveredProjectNode>();
        var effectiveRoots = evidence.ProjectRoots ?? Array.Empty<string>();

        // Check for unsupported framework hallucination in plan/summary
        var allPackageRefs = effectiveGraph
            .SelectMany(p => p.PackageReferences)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var combinedPlanText = $"{response.Summary}\n{string.Join("\n", response.ProposedPlan?.Select(p => $"{p?.Title} {p?.Description}") ?? Array.Empty<string>())}";
        if ((combinedPlanText.Contains("MediatR", StringComparison.OrdinalIgnoreCase) ||
             combinedPlanText.Contains("IRequest<", StringComparison.OrdinalIgnoreCase) ||
             combinedPlanText.Contains("IRequestHandler<", StringComparison.OrdinalIgnoreCase)) &&
            !allPackageRefs.Contains("MediatR"))
        {
            return ParseResult.Failure("The proposed impact analysis references unsupported framework 'MediatR' which is not referenced by the repository. Implement standard DevPilot queries/handlers without MediatR.");
        }

        var resultData = new ImpactAnalysisResultData
        {
            Summary = Truncate(response.Summary.Trim(), 400),
            Confidence = NormalizeConfidence(response.Confidence),
            Metadata = response.Metadata,
        };

        var impactedFiles = new List<ImpactedFile>();

        if (response.ImpactedFiles is not null)
        {
            foreach (var f in response.ImpactedFiles.Take(30))
            {
                if (f is null || string.IsNullOrWhiteSpace(f.FilePath)) continue;

                var rawPath = f.FilePath.Trim();
                string normalizedPath;
                try
                {
                    normalizedPath = ProjectGraphHelper.NormalizeAndValidateRelativePath(rawPath);
                }
                catch (Exception ex)
                {
                    return ParseResult.Failure($"Impacted file path '{rawPath}' is invalid: {ex.Message}");
                }

                var changeType = ParseChangeType(f.ChangeType);

                if (normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ProjectGraphHelper.IsCsFileInProjectRoot(normalizedPath, effectiveRoots))
                    {
                        if (ProjectGraphHelper.IsTestFileCandidate(normalizedPath))
                        {
                            if (ProjectGraphHelper.TryRemapTestFileToSingleTestProject(normalizedPath, effectiveGraph, out var remappedPath, out var err))
                            {
                                normalizedPath = remappedPath;
                            }
                            else
                            {
                                var remapErr = err ?? $"Impacted C# test file '{rawPath}' is outside all discovered .NET project roots.";
                                return ParseResult.GroundingFailure(
                                    remapErr,
                                    new ImpactGroundingErrorDetails
                                    {
                                        InvalidFilePath = rawPath,
                                        InvalidChangeType = changeType.ToString(),
                                        ExactError = remapErr,
                                        ValidImpactedFiles = impactedFiles.ToList(),
                                        CandidateRepositoryPaths = FindCandidatePaths(normalizedPath, workspaceLocalPath, effectiveRoots)
                                    });
                            }
                        }
                        else
                        {
                            var outsideErr = $"Impacted C# file '{rawPath}' is outside all discovered .NET project roots.";
                            return ParseResult.GroundingFailure(
                                outsideErr,
                                new ImpactGroundingErrorDetails
                                {
                                    InvalidFilePath = rawPath,
                                    InvalidChangeType = changeType.ToString(),
                                    ExactError = outsideErr,
                                    ValidImpactedFiles = impactedFiles.ToList(),
                                    CandidateRepositoryPaths = FindCandidatePaths(normalizedPath, workspaceLocalPath, effectiveRoots)
                                });
                        }
                    }
                }

                // Deterministic Modify/Refactor/Delete Grounding Check
                if (changeType is ImpactFileChangeType.Modify or ImpactFileChangeType.Refactor or ImpactFileChangeType.Delete)
                {
                    if (!ProjectGraphHelper.TryResolveModifyTarget(
                        normalizedPath,
                        workspaceLocalPath,
                        effectiveGraph,
                        effectiveRoots,
                        out var resolvedModifyPath,
                        out var modifyErr))
                    {
                        var err = modifyErr ?? $"Impacted file path '{rawPath}' with action '{changeType}' does not exist in the repository and cannot be deterministically resolved.";
                        return ParseResult.GroundingFailure(
                            err,
                            new ImpactGroundingErrorDetails
                            {
                                InvalidFilePath = rawPath,
                                InvalidChangeType = changeType.ToString(),
                                ExactError = err,
                                ValidImpactedFiles = impactedFiles.ToList(),
                                CandidateRepositoryPaths = FindCandidatePaths(normalizedPath, workspaceLocalPath, effectiveRoots)
                            });
                    }
                    normalizedPath = resolvedModifyPath;
                }
                else if (changeType == ImpactFileChangeType.Add && !string.IsNullOrWhiteSpace(workspaceLocalPath) && Directory.Exists(workspaceLocalPath))
                {
                    var fullPath = Path.Combine(workspaceLocalPath, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fullPath))
                    {
                        var err = $"Impacted file path '{normalizedPath}' with action 'Create' already exists in the repository. Use action 'Modify' to update an existing file.";
                        return ParseResult.GroundingFailure(
                            err,
                            new ImpactGroundingErrorDetails
                            {
                                InvalidFilePath = rawPath,
                                InvalidChangeType = "Create",
                                ExactError = err,
                                ValidImpactedFiles = impactedFiles.ToList(),
                                CandidateRepositoryPaths = FindCandidatePaths(normalizedPath, workspaceLocalPath, effectiveRoots)
                            });
                    }
                }

                var (evType, evDetails, isUncertain, calibratedConfidence) = ChangeIntelligenceEvidenceCollector.ClassifyAndCalibrateFileEvidence(
                    normalizedPath,
                    changeType,
                    f.Confidence,
                    evidence);

                var finalEvType = !string.IsNullOrWhiteSpace(f.EvidenceType) ? f.EvidenceType.Trim() : evType;
                var finalEvDetails = !string.IsNullOrWhiteSpace(f.EvidenceDetails) ? f.EvidenceDetails.Trim() : evDetails;

                impactedFiles.Add(new ImpactedFile
                {
                    FilePath = normalizedPath,
                    ChangeType = changeType,
                    Reason = Truncate(f.Reason?.Trim() ?? string.Empty, 200),
                    Confidence = calibratedConfidence,
                    EvidenceType = finalEvType,
                    EvidenceDetails = Truncate(finalEvDetails, 200),
                    IsUncertain = isUncertain,
                });
            }

            resultData.ImpactedFiles = impactedFiles;
            resultData.Confidence = CalibrateOverallConfidence(response.Confidence, impactedFiles);
        }
        else
        {
            resultData.Confidence = CalibrateOverallConfidence(response.Confidence, impactedFiles);
        }

        if (response.ProposedPlan is not null && response.ProposedPlan.Count > 0)
        {
            var order = 1;
            var planSteps = new List<ProposedPlanStep>();
            foreach (var s in response.ProposedPlan.Take(8))
            {
                if (s is null || string.IsNullOrWhiteSpace(s.Title)) continue;

                var related = new List<string>();
                if (s.RelatedFiles != null)
                {
                    foreach (var rf in s.RelatedFiles.Take(10))
                    {
                        if (string.IsNullOrWhiteSpace(rf)) continue;
                        var raw = rf.Trim();
                        string norm;
                        try
                        {
                            norm = ProjectGraphHelper.NormalizeAndValidateRelativePath(raw);
                        }
                        catch
                        {
                            norm = raw.Replace('\\', '/').TrimStart('/');
                        }

                        if (norm.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                            !ProjectGraphHelper.IsCsFileInProjectRoot(norm, effectiveRoots) &&
                            ProjectGraphHelper.IsTestFileCandidate(norm) &&
                            ProjectGraphHelper.TryRemapTestFileToSingleTestProject(norm, effectiveGraph, out var remapped, out _))
                        {
                            norm = remapped;
                        }

                        if (ProjectGraphHelper.TryResolveModifyTarget(norm, workspaceLocalPath, effectiveGraph, effectiveRoots, out var resolvedRel, out _))
                        {
                            norm = resolvedRel;
                        }

                        if (!related.Contains(norm, StringComparer.OrdinalIgnoreCase))
                        {
                            related.Add(norm);
                        }
                    }
                }

                planSteps.Add(new ProposedPlanStep
                {
                    Order = s.Order is > 0 ? s.Order.Value : order++,
                    Title = Truncate(s.Title.Trim(), 100),
                    Description = Truncate(s.Description?.Trim() ?? string.Empty, 250),
                    RelatedFiles = related,
                });
            }
            resultData.ProposedPlan = planSteps;
        }
        else if (impactedFiles.Count > 0)
        {
            var planSteps = new List<ProposedPlanStep>();
            var nonTestFiles = impactedFiles.Where(f => f.EvidenceType != "RelevantTest").Select(f => f.FilePath).Take(5).ToList();
            var testFiles = impactedFiles.Where(f => f.EvidenceType == "RelevantTest").Select(f => f.FilePath).Take(5).ToList();

            planSteps.Add(new ProposedPlanStep
            {
                Order = 1,
                Title = "Implement changes across domain and application layers",
                Description = "Implement core logic, entities, and services per requirements.",
                RelatedFiles = nonTestFiles
            });

            if (testFiles.Count > 0)
            {
                planSteps.Add(new ProposedPlanStep
                {
                    Order = 2,
                    Title = "Update test suite and verify changes",
                    Description = "Add and update unit/integration tests to cover modified functionality.",
                    RelatedFiles = testFiles
                });
            }

            resultData.ProposedPlan = planSteps;
        }

        var systemImpacts = new List<SystemImpact>();
        if (response.SystemImpacts is not null)
        {
            systemImpacts = response.SystemImpacts
                .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Area))
                .Take(6)
                .Select(i => new SystemImpact
                {
                    Area = i!.Area!.Trim(),
                    ImpactLevel = ParseSystemImpactLevel(i.ImpactLevel),
                    Description = Truncate(i.Description?.Trim() ?? string.Empty, 200),
                })
                .ToList();
            resultData.SystemImpacts = systemImpacts;
        }

        var risks = new List<Risk>();
        if (response.Risks is not null)
        {
            risks = response.Risks
                .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Description))
                .Take(5)
                .Select(r => new Risk
                {
                    Level = ParseRiskLevel(r!.Level),
                    Description = Truncate(r.Description!.Trim(), 200),
                    Mitigation = Truncate(r.Mitigation?.Trim() ?? string.Empty, 200),
                })
                .ToList();
            resultData.Risks = risks;
        }

        // Change Dimensions
        var dimensions = new List<ChangeDimensionImpact>();
        if (response.Dimensions != null && response.Dimensions.Count > 0)
        {
            foreach (var dim in response.Dimensions.Take(7))
            {
                if (dim == null || string.IsNullOrWhiteSpace(dim.Area)) continue;
                var normArea = ChangeDimensionArea.Normalize(dim.Area);
                dimensions.Add(new ChangeDimensionImpact
                {
                    Area = normArea,
                    ImpactLevel = ParseSystemImpactLevel(dim.ImpactLevel),
                    Summary = Truncate(dim.Summary?.Trim() ?? dim.Description?.Trim() ?? string.Empty, 200),
                    Details = dim.Details?.Where(d => !string.IsNullOrWhiteSpace(d)).Take(3).Select(d => Truncate(d.Trim(), 150)).ToList() ?? new List<string>(),
                    Evidence = dim.Evidence?.Where(e => !string.IsNullOrWhiteSpace(e)).Take(5).Select(e => Truncate(e.Trim(), 200)).ToList() ?? new List<string>()
                });
            }
        }
        else if (systemImpacts.Count > 0)
        {
            // Map legacy system impacts to change dimensions
            foreach (var si in systemImpacts)
            {
                var area = ChangeDimensionArea.Normalize(si.Area);
                dimensions.Add(new ChangeDimensionImpact
                {
                    Area = area,
                    ImpactLevel = si.ImpactLevel,
                    Summary = si.Description,
                    Details = new List<string> { si.Description },
                    Evidence = new List<string>()
                });
            }
        }

        // Ensure grounded dimensions are populated if repository evidence supports them
        if (!dimensions.Any(d => string.Equals(d.Area, ChangeDimensionArea.Api, StringComparison.OrdinalIgnoreCase)) &&
            impactedFiles.Any(f => f.EvidenceType == "ControllerUsage"))
        {
            dimensions.Add(new ChangeDimensionImpact
            {
                Area = ChangeDimensionArea.Api,
                ImpactLevel = SystemImpactLevel.Medium,
                Summary = "API surface modified: controller endpoint affected",
                Details = new List<string> { "Controller endpoint definition updated" },
                Evidence = impactedFiles.Where(f => f.EvidenceType == "ControllerUsage").Select(f => f.FilePath).ToList()
            });
        }

        if (!dimensions.Any(d => string.Equals(d.Area, ChangeDimensionArea.Data, StringComparison.OrdinalIgnoreCase)) &&
            impactedFiles.Any(f => f.EvidenceType == "PersistenceRelationship" || f.EvidenceType == "MigrationRelationship"))
        {
            dimensions.Add(new ChangeDimensionImpact
            {
                Area = ChangeDimensionArea.Data,
                ImpactLevel = SystemImpactLevel.Medium,
                Summary = "Database schema/persistence affected; migration likely/expected",
                Details = new List<string> { "Persistence entity, DbContext, or configuration touched" },
                Evidence = impactedFiles.Where(f => f.EvidenceType is "PersistenceRelationship" or "MigrationRelationship").Select(f => f.FilePath).ToList()
            });
        }

        if (!dimensions.Any(d => string.Equals(d.Area, ChangeDimensionArea.Tests, StringComparison.OrdinalIgnoreCase)))
        {
            if (!evidence.HasTestProjects)
            {
                dimensions.Add(new ChangeDimensionImpact
                {
                    Area = ChangeDimensionArea.Tests,
                    ImpactLevel = SystemImpactLevel.Medium,
                    Summary = "Missing test coverage: no automated test project discovered",
                    Details = new List<string> { "No test project found in repository" },
                    Evidence = new List<string>()
                });
            }
            else if (impactedFiles.Any(f => f.EvidenceType == "RelevantTest"))
            {
                dimensions.Add(new ChangeDimensionImpact
                {
                    Area = ChangeDimensionArea.Tests,
                    ImpactLevel = SystemImpactLevel.Low,
                    Summary = "Test suite updated with relevant test coverage",
                    Details = new List<string> { "Automated test files touched" },
                    Evidence = impactedFiles.Where(f => f.EvidenceType == "RelevantTest").Select(f => f.FilePath).ToList()
                });
            }
        }

        resultData.Dimensions = dimensions;

        // Unknowns synthesis
        var rawUnknowns = new List<string>();
        if (response.Unknowns != null) rawUnknowns.AddRange(response.Unknowns);
        if (response.ChangeBrief?.Unknowns != null) rawUnknowns.AddRange(response.ChangeBrief.Unknowns);

        var synthesizedUnknowns = ChangeIntelligenceEvidenceCollector.SynthesizeUnknowns(
            rawUnknowns,
            impactedFiles,
            dimensions,
            evidence);

        resultData.Unknowns = synthesizedUnknowns;

        // Change Brief synthesis
        var changeBrief = ChangeIntelligenceEvidenceCollector.BuildChangeBrief(
            impactedFiles,
            risks,
            systemImpacts,
            dimensions,
            synthesizedUnknowns,
            evidence);

        if (resultData.SystemImpacts.Count == 0 && dimensions.Count > 0)
        {
            systemImpacts = dimensions
                .Select(d => new SystemImpact
                {
                    Area = d.Area,
                    ImpactLevel = d.ImpactLevel,
                    Description = d.Summary
                })
                .ToList();
            resultData.SystemImpacts = systemImpacts;
        }

        if (resultData.Risks.Count == 0 && changeBrief.RiskReasons.Count > 0)
        {
            risks = changeBrief.RiskReasons.Take(3)
                .Select(r => new Risk
                {
                    Level = changeBrief.RiskLevel,
                    Description = r,
                    Mitigation = "Execute configured verification checks and review changes."
                })
                .ToList();
            resultData.Risks = risks;
        }

        resultData.ChangeBrief = changeBrief;
        resultData.RiskReasons = changeBrief.RiskReasons;

        return ParseResult.Succeeded(resultData);
    }

    private static int NormalizeConfidence(int? confidence)
    {
        return confidence.HasValue ? Math.Clamp(confidence.Value, 0, 100) : 0;
    }

    private static int CalibrateOverallConfidence(int? modelConfidence, IReadOnlyList<ImpactedFile> files)
    {
        if (modelConfidence.HasValue && modelConfidence.Value >= 1 && modelConfidence.Value <= 100)
        {
            return Math.Clamp(modelConfidence.Value, 1, 100);
        }

        if (files != null && files.Count > 0)
        {
            return (int)Math.Round(files.Average(f => f.Confidence));
        }

        return 75;
    }

    private static ImpactFileChangeType ParseChangeType(string? value)
    {
        if (string.Equals(value, "Create", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "New", StringComparison.OrdinalIgnoreCase))
        {
            return ImpactFileChangeType.Add;
        }

        return Enum.TryParse<ImpactFileChangeType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ImpactFileChangeType.Unknown;
    }

    private static SystemImpactLevel ParseSystemImpactLevel(string? value)
    {
        return Enum.TryParse<SystemImpactLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : SystemImpactLevel.Medium;
    }

    private static RiskLevel ParseRiskLevel(string? value)
    {
        return Enum.TryParse<RiskLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : RiskLevel.Medium;
    }

    private async Task<AnalyzeTaskImpactResult> FailAnalysisAsync(
        TaskImpactAnalysisEntity analysis,
        DevelopmentTask task,
        string errorMessage,
        string? rawResponse,
        string? model,
        string providerName,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Impact analysis failed for task {TaskId}: {ErrorMessage}",
            task.Id,
            errorMessage);

        var now = DateTime.UtcNow;
        analysis.Status = ImpactAnalysisStatus.Failed;
        analysis.Model = model;
        analysis.ProviderName = providerName;
        analysis.RawResponse = rawResponse;
        analysis.ErrorMessage = errorMessage;
        analysis.CompletedAt = now;

        try
        {
            await _analysisRepository
                .UpdateAsync(analysis, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Failed to persist the failed impact analysis for task {TaskId}.",
                task.Id);
        }

        try
        {
            task.Status = DevelopmentTaskStatus.Failed;
            task.UpdatedAt = now;
            await _taskRepository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Failed to mark task {TaskId} as failed after analysis failure.",
                task.Id);
        }

        return new AnalyzeTaskImpactResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            AnalysisId = analysis.Id,
        };
    }

    private static ImpactAnalysisDto MapToDto(TaskImpactAnalysisEntity analysis)
    {
        return new ImpactAnalysisDto
        {
            Id = analysis.Id,
            DevelopmentTaskId = analysis.DevelopmentTaskId,
            Status = analysis.Status,
            Summary = analysis.Summary,
            Confidence = analysis.Confidence,
            Model = analysis.Model,
            ProviderName = analysis.ProviderName,
            RawResponse = analysis.RawResponse,
            ErrorMessage = analysis.ErrorMessage,
            StructuredResult = analysis.StructuredResult is null
                ? null
                : MapStructuredResult(analysis.StructuredResult),
            CreatedAt = analysis.CreatedAt,
            CompletedAt = analysis.CompletedAt,
        };
    }

    private static StructuredResultDto MapStructuredResult(ImpactAnalysisResultData data)
    {
        return new StructuredResultDto
        {
            Summary = data.Summary,
            Confidence = data.Confidence,
            ImpactedFiles = data.ImpactedFiles
                .Select(f => new ImpactedFileDto
                {
                    FilePath = f.FilePath,
                    ChangeType = f.ChangeType,
                    Reason = f.Reason,
                    Confidence = f.Confidence,
                    EvidenceType = f.EvidenceType,
                    EvidenceDetails = f.EvidenceDetails,
                    IsUncertain = f.IsUncertain,
                })
                .ToList(),
            ProposedPlan = data.ProposedPlan
                .Select(s => new ProposedPlanStepDto
                {
                    Order = s.Order,
                    Title = s.Title,
                    Description = s.Description,
                    RelatedFiles = s.RelatedFiles,
                })
                .ToList(),
            SystemImpacts = data.SystemImpacts
                .Select(i => new SystemImpactDto
                {
                    Area = i.Area,
                    ImpactLevel = i.ImpactLevel,
                    Description = i.Description,
                })
                .ToList(),
            Risks = data.Risks
                .Select(r => new RiskDto
                {
                    Level = r.Level,
                    Description = r.Description,
                    Mitigation = r.Mitigation,
                })
                .ToList(),
            ChangeBrief = data.ChangeBrief is null
                ? null
                : new ChangeBriefDto
                {
                    FileCount = data.ChangeBrief.FileCount,
                    ProjectCount = data.ChangeBrief.ProjectCount,
                    RiskLevel = data.ChangeBrief.RiskLevel,
                    RiskReasons = data.ChangeBrief.RiskReasons,
                    ApiSummary = data.ChangeBrief.ApiSummary,
                    DataSummary = data.ChangeBrief.DataSummary,
                    RuntimeSummary = data.ChangeBrief.RuntimeSummary,
                    TestsSummary = data.ChangeBrief.TestsSummary,
                    VerificationSummary = data.ChangeBrief.VerificationSummary,
                    ExpectedChecks = data.ChangeBrief.ExpectedChecks
                        .Select(c => new ExpectedVerificationCheckDto
                        {
                            CheckId = c.CheckId,
                            DisplayName = c.DisplayName,
                            Kind = c.Kind,
                            Required = c.Required,
                            Source = c.Source,
                            DiscoveryEvidence = c.DiscoveryEvidence,
                        })
                        .ToList(),
                    Unknowns = data.ChangeBrief.Unknowns,
                },
            Dimensions = data.Dimensions
                .Select(d => new ChangeDimensionImpactDto
                {
                    Area = d.Area,
                    ImpactLevel = d.ImpactLevel,
                    Summary = d.Summary,
                    Details = d.Details,
                    Evidence = d.Evidence,
                })
                .ToList(),
            Unknowns = data.Unknowns,
            RiskReasons = data.RiskReasons,
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    public sealed class ImpactGroundingErrorDetails
    {
        public string InvalidFilePath { get; init; } = string.Empty;
        public string InvalidChangeType { get; init; } = string.Empty;
        public string ExactError { get; init; } = string.Empty;
        public List<ImpactedFile> ValidImpactedFiles { get; init; } = new();
        public List<string> CandidateRepositoryPaths { get; init; } = new();
    }

    public sealed class ParseResult
    {
        public bool Success { get; private init; }

        public string? ErrorMessage { get; private init; }

        public ImpactAnalysisResultData? ResultData { get; private init; }

        public bool IsGroundingError { get; private init; }

        public ImpactGroundingErrorDetails? GroundingErrorDetails { get; private init; }

        public static ParseResult Succeeded(ImpactAnalysisResultData resultData)
        {
            return new ParseResult
            {
                Success = true,
                ResultData = resultData,
            };
        }

        public static ParseResult Failure(string errorMessage)
        {
            return new ParseResult
            {
                Success = false,
                ErrorMessage = errorMessage,
            };
        }

        public static ParseResult GroundingFailure(string errorMessage, ImpactGroundingErrorDetails details)
        {
            return new ParseResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                IsGroundingError = true,
                GroundingErrorDetails = details,
            };
        }
    }

    private sealed class ImpactAnalysisResponse
    {
        public string? Summary { get; set; }

        public int? Confidence { get; set; }

        public List<ImpactedFileResponse>? ImpactedFiles { get; set; }

        public List<ProposedPlanStepResponse>? ProposedPlan { get; set; }

        public List<SystemImpactResponse>? SystemImpacts { get; set; }

        public List<ChangeDimensionResponse>? Dimensions { get; set; }

        public ChangeBriefResponse? ChangeBrief { get; set; }

        public List<string>? Unknowns { get; set; }

        public List<RiskResponse>? Risks { get; set; }

        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    private sealed class ChangeBriefResponse
    {
        public string? ApiSummary { get; set; }

        public string? DataSummary { get; set; }

        public string? RuntimeSummary { get; set; }

        public string? TestsSummary { get; set; }

        public List<string>? Unknowns { get; set; }
    }

    private sealed class ChangeDimensionResponse
    {
        public string? Area { get; set; }

        public string? ImpactLevel { get; set; }

        public string? Summary { get; set; }

        public string? Description { get; set; }

        public List<string>? Details { get; set; }

        public List<string>? Evidence { get; set; }
    }

    private sealed class ImpactedFileResponse
    {
        public string? FilePath { get; set; }

        public string? ChangeType { get; set; }

        public string? Reason { get; set; }

        public int? Confidence { get; set; }

        public string? EvidenceType { get; set; }

        public string? EvidenceDetails { get; set; }
    }

    private sealed class ProposedPlanStepResponse
    {
        public int? Order { get; set; }

        public string? Title { get; set; }

        public string? Description { get; set; }

        public List<string>? RelatedFiles { get; set; }
    }

    private sealed class SystemImpactResponse
    {
        public string? Area { get; set; }

        public string? ImpactLevel { get; set; }

        public string? Description { get; set; }
    }

    private sealed class RiskResponse
    {
        public string? Level { get; set; }

        public string? Description { get; set; }

        public string? Mitigation { get; set; }
    }
}
