using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevPilot.Application.AiProviders;
using DevPilot.Application.CodeAnalysis;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Application.TaskImpactAnalysis.Dtos;
using DevPilot.Application.TaskImpactAnalysis.Ports;
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
        "You are DevPilot's impact analysis engine. " +
        "Analyze the impact of a software change request against a repository context. " +
        "Respond with a single JSON object only. Do not wrap it in markdown code fences and do not add commentary. " +
        "All confidence values are integers between 0 and 100. " +
        "Use PascalCase enum string values: changeType can be Unknown, Add, Modify, Delete or Refactor; " +
        "impactLevel and risk level can be Low, Medium, High or Critical.";

    private const string JsonSchema = @"{
  ""summary"": ""A concise impact summary for a technical audience (string, required)."",
  ""confidence"": 0,
  ""impactedFiles"": [
    {
      ""filePath"": ""relative/path/to/file.cs"",
      ""changeType"": ""Modify"",
      ""reason"": ""Why this file is impacted"",
      ""confidence"": 0
    }
  ],
  ""proposedPlan"": [
    {
      ""order"": 1,
      ""title"": ""Step title"",
      ""description"": ""What should be done in this step"",
      ""relatedFiles"": [""relative/path.cs""]
    }
  ],
  ""systemImpacts"": [
    {
      ""area"": ""API / Database / UI / Tests / Infrastructure"",
      ""impactLevel"": ""Low"",
      ""description"": ""Description of the impact""
    }
  ],
  ""risks"": [
    {
      ""level"": ""Medium"",
      ""description"": ""Risk description"",
      ""mitigation"": ""How to mitigate it""
    }
  ],
      ""metadata"": {}
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
    private readonly ILogger<AnalyzeTaskImpactCommandHandler> _logger;

    public AnalyzeTaskImpactCommandHandler(
        ITaskRepository taskRepository,
        IRepositoryWorkspaceQuery workspaceQuery,
        IImpactAnalysisRepository analysisRepository,
        IRepositoryAnalyzer repositoryAnalyzer,
        IAiProvider aiProvider,
        IEmbeddingProvider embeddingProvider,
        ISemanticSearchService semanticSearchService,
        ILogger<AnalyzeTaskImpactCommandHandler> logger)
    {
        _taskRepository = taskRepository;
        _workspaceQuery = workspaceQuery;
        _analysisRepository = analysisRepository;
        _repositoryAnalyzer = repositoryAnalyzer;
        _aiProvider = aiProvider;
        _embeddingProvider = embeddingProvider;
        _semanticSearchService = semanticSearchService;
        _logger = logger;
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

        try
        {
            task.Status = DevelopmentTaskStatus.Analyzing;
            task.UpdatedAt = DateTime.UtcNow;
            await _taskRepository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Failed to set task {TaskId} status to Analyzing.",
                task.Id);

            return new AnalyzeTaskImpactResult
            {
                Success = false,
                ErrorMessage = "Failed to start the analysis.",
            };
        }

        string? rawResponse = null;
        string? model = null;
        var providerName = _aiProvider.ProviderName;

        try
        {
            var projectGraph = ProjectGraphHelper.DiscoverProjectGraph(workspace.LocalPath);
            var projectRoots = ProjectGraphHelper.DiscoverProjectRoots(workspace.LocalPath);

            var context = await BuildContextAsync(task, workspace, cancellationToken).ConfigureAwait(false);
            var aiRequest = new AiRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = BuildUserPrompt(task, workspace, context, projectGraph, projectRoots),
            };

            var stopwatch = Stopwatch.StartNew();
            var aiResponse = await _aiProvider
                .SendAsync(aiRequest, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            rawResponse = aiResponse.Content;
            model = aiResponse.Model;
            providerName = string.IsNullOrWhiteSpace(aiResponse.Provider)
                ? _aiProvider.ProviderName
                : aiResponse.Provider;

            if (!aiResponse.IsSuccess)
            {
                return await FailAnalysisAsync(
                    task,
                    aiResponse.ErrorMessage ?? "AI provider returned an unsuccessful response.",
                    rawResponse,
                    model,
                    providerName,
                    cancellationToken).ConfigureAwait(false);
            }

            var parseResult = TryParseStructuredResult(rawResponse, projectGraph, projectRoots, workspace.LocalPath);
            if (!parseResult.Success)
            {
                return await FailAnalysisAsync(
                    task,
                    parseResult.ErrorMessage ?? "Failed to parse the AI response.",
                    rawResponse,
                    model,
                    providerName,
                    cancellationToken).ConfigureAwait(false);
            }

            var now = DateTime.UtcNow;
            var structuredResult = parseResult.ResultData!;
            var analysis = new TaskImpactAnalysisEntity
            {
                Id = Guid.NewGuid(),
                DevelopmentTaskId = task.Id,
                Status = ImpactAnalysisStatus.Completed,
                Summary = structuredResult.Summary,
                Confidence = structuredResult.Confidence,
                Model = model,
                ProviderName = providerName,
                RawResponse = rawResponse,
                StructuredResult = structuredResult,
                CreatedAt = now,
                CompletedAt = now,
            };

            await _analysisRepository
                .AddAsync(analysis, cancellationToken)
                .ConfigureAwait(false);

            task.Status = DevelopmentTaskStatus.AwaitingApproval;
            task.UpdatedAt = now;
            await _taskRepository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Impact analysis {AnalysisId} completed for task {TaskId} in {ElapsedMs}ms.",
                analysis.Id,
                task.Id,
                stopwatch.ElapsedMilliseconds);

            return new AnalyzeTaskImpactResult
            {
                Success = true,
                AnalysisId = analysis.Id,
                Analysis = MapToDto(analysis),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Unexpected error while analyzing impact for task {TaskId}.",
                task.Id);

            return await FailAnalysisAsync(
                task,
                "An unexpected error occurred during impact analysis.",
                rawResponse,
                model,
                providerName,
                cancellationToken).ConfigureAwait(false);
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

    private async Task<string> BuildContextAsync(
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

        return builder.ToString();
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

    private static string BuildUserPrompt(
        DevelopmentTask task,
        RepositoryWorkspace workspace,
        string context,
        IReadOnlyList<DiscoveredProjectNode> projectGraph,
        IReadOnlyList<string> projectRoots)
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
        if (projectGraph != null && projectGraph.Count > 0)
        {
            foreach (var proj in projectGraph)
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

        builder.AppendLine("# Context");
        builder.AppendLine(context);
        builder.AppendLine();

        builder.AppendLine("# Instructions");
        builder.AppendLine(
            "Analyze the impact of implementing this task on the repository. " +
            "Respond with a single JSON object only, no markdown fences, no extra commentary. " +
            "CRITICAL PROJECT RULES:\n" +
            "1. All proposed C# (*.cs) file paths MUST be located within one of the discovered .NET project directories listed above.\n" +
            "2. Do NOT invent new or nonexistent project directories (e.g. if the test project is 'tests/DevPilot.Tests', do NOT invent 'tests/DevPilot.Api.Tests').\n" +
            "3. Unit and integration test files MUST be placed in an existing discovered test project.\n" +
            "4. STRICT ARCHITECTURAL GROUNDING: You MUST strictly adhere to the existing architectural patterns, interfaces, abstractions, and libraries referenced in the project graph.\n" +
            "5. DO NOT INVENT FRAMEWORKS OR PATTERNS: Do NOT introduce or propose third-party packages, libraries, or architectural patterns (such as MediatR, direct Entity Framework Core access in Application layer, or nonexistent DbContext interfaces) that are not referenced in the target project.\n" +
            "Confidence must be an integer 0-100. Use the following schema:");
        builder.AppendLine(JsonSchema);

        return builder.ToString();
    }

    private static ParseResult TryParseStructuredResult(
        string rawResponse,
        IReadOnlyList<DiscoveredProjectNode> projectGraph,
        IReadOnlyList<string> projectRoots,
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

        return MapToResultData(response, projectGraph, projectRoots, workspaceLocalPath);
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
        IReadOnlyList<DiscoveredProjectNode> projectGraph,
        IReadOnlyList<string> projectRoots,
        string workspaceLocalPath)
    {
        if (string.IsNullOrWhiteSpace(response.Summary))
        {
            return ParseResult.Failure("The AI response is missing a summary.");
        }

        var effectiveGraph = projectGraph ?? Array.Empty<DiscoveredProjectNode>();
        var effectiveRoots = projectRoots ?? Array.Empty<string>();

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
            Summary = response.Summary.Trim(),
            Confidence = NormalizeConfidence(response.Confidence),
            Metadata = response.Metadata,
        };

        if (response.ImpactedFiles is not null)
        {
            var impactedFiles = new List<ImpactedFile>();
            foreach (var f in response.ImpactedFiles)
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
                                return ParseResult.Failure(err ?? $"Impacted C# test file '{rawPath}' is outside all discovered .NET project roots.");
                            }
                        }
                        else
                        {
                            return ParseResult.Failure($"Impacted C# file '{rawPath}' is outside all discovered .NET project roots.");
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
                        return ParseResult.Failure(modifyErr ?? $"Impacted file path '{rawPath}' with action '{changeType}' does not exist in the repository and cannot be deterministically resolved.");
                    }
                    normalizedPath = resolvedModifyPath;
                }

                impactedFiles.Add(new ImpactedFile
                {
                    FilePath = normalizedPath,
                    ChangeType = changeType,
                    Reason = f.Reason?.Trim() ?? string.Empty,
                    Confidence = NormalizeConfidence(f.Confidence),
                });
            }

            resultData.ImpactedFiles = impactedFiles;
        }

        if (response.ProposedPlan is not null)
        {
            var order = 1;
            var planSteps = new List<ProposedPlanStep>();
            foreach (var s in response.ProposedPlan)
            {
                if (s is null || string.IsNullOrWhiteSpace(s.Title)) continue;

                var related = new List<string>();
                if (s.RelatedFiles != null)
                {
                    foreach (var rf in s.RelatedFiles)
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
                    Title = s.Title.Trim(),
                    Description = s.Description?.Trim() ?? string.Empty,
                    RelatedFiles = related,
                });
            }
            resultData.ProposedPlan = planSteps;
        }

        if (response.SystemImpacts is not null)
        {
            resultData.SystemImpacts = response.SystemImpacts
                .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Area))
                .Select(i => new SystemImpact
                {
                    Area = i!.Area!.Trim(),
                    ImpactLevel = ParseSystemImpactLevel(i.ImpactLevel),
                    Description = i.Description?.Trim() ?? string.Empty,
                })
                .ToList();
        }

        if (response.Risks is not null)
        {
            resultData.Risks = response.Risks
                .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Description))
                .Select(r => new Risk
                {
                    Level = ParseRiskLevel(r!.Level),
                    Description = r.Description!.Trim(),
                    Mitigation = r.Mitigation?.Trim() ?? string.Empty,
                })
                .ToList();
        }

        return ParseResult.Succeeded(resultData);
    }

    private static int NormalizeConfidence(int? confidence)
    {
        return confidence.HasValue ? Math.Clamp(confidence.Value, 0, 100) : 0;
    }

    private static ImpactFileChangeType ParseChangeType(string? value)
    {
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
        var failedAnalysis = new TaskImpactAnalysisEntity
        {
            Id = Guid.NewGuid(),
            DevelopmentTaskId = task.Id,
            Status = ImpactAnalysisStatus.Failed,
            Summary = string.Empty,
            Confidence = 0,
            Model = model,
            ProviderName = providerName,
            RawResponse = rawResponse,
            ErrorMessage = errorMessage,
            CreatedAt = now,
            CompletedAt = now,
        };

        try
        {
            await _analysisRepository
                .AddAsync(failedAnalysis, cancellationToken)
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
            AnalysisId = failedAnalysis.Id,
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

    private sealed class ParseResult
    {
        public bool Success { get; private init; }

        public string? ErrorMessage { get; private init; }

        public ImpactAnalysisResultData? ResultData { get; private init; }

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
    }

    private sealed class ImpactAnalysisResponse
    {
        public string? Summary { get; set; }

        public int? Confidence { get; set; }

        public List<ImpactedFileResponse>? ImpactedFiles { get; set; }

        public List<ProposedPlanStepResponse>? ProposedPlan { get; set; }

        public List<SystemImpactResponse>? SystemImpacts { get; set; }

        public List<RiskResponse>? Risks { get; set; }

        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    private sealed class ImpactedFileResponse
    {
        public string? FilePath { get; set; }

        public string? ChangeType { get; set; }

        public string? Reason { get; set; }

        public int? Confidence { get; set; }
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

