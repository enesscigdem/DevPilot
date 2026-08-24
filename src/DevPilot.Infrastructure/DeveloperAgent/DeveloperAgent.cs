using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Infrastructure.Executions;
using DevPilot.Domain.Constants;
using DevPilot.Domain.Enums;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.DeveloperAgent;

public sealed class DeveloperAgent : IDeveloperAgent
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Regex MarkdownCodeFenceRegex = new(
        @"```(?:json)?\s*\n?([\s\S]*?)\n?```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IAiProvider _aiProvider;
    private readonly IWorktreeEditApplier _editApplier;
    private readonly ILogger<DeveloperAgent> _logger;
    private readonly IExecutionActivityRecorder? _activityRecorder;
    private readonly int _maxOutputTokens;
    private readonly int _maxCompactRetryOutputTokens;
    private readonly int _budgetDtoOrModel;
    private readonly int _budgetInterfaceOrContract;
    private readonly int _budgetQueryOrCommand;
    private readonly int _budgetHandlerOrService;
    private readonly int _budgetController;
    private readonly int _budgetModifyPatch;
    private readonly int _budgetTestFile;
    private readonly int _budgetFallback;
    private readonly int _maxManifestFiles;
    private readonly int _maxGenerationCalls;
    private readonly int _maxConcurrentFileGenerations;
    private readonly string? _mechanicalReasoningEffort;

    public DeveloperAgent(
        IAiProvider aiProvider,
        IWorktreeEditApplier editApplier,
        ILogger<DeveloperAgent> logger,
        IConfiguration? configuration = null,
        IExecutionActivityRecorder? activityRecorder = null)
    {
        _aiProvider = aiProvider;
        _editApplier = editApplier;
        _logger = logger;
        _activityRecorder = activityRecorder;

        if (configuration != null &&
            int.TryParse(configuration["DeveloperAgent:MaxOutputTokens"], out var cfgTokens) &&
            cfgTokens > 0)
        {
            _maxOutputTokens = cfgTokens;
        }
        else
        {
            _maxOutputTokens = 32768;
        }

        if (configuration != null &&
            int.TryParse(configuration["DeveloperAgent:MaxCompactRetryOutputTokens"], out var cfgRetryTokens) &&
            cfgRetryTokens > 0)
        {
            _maxCompactRetryOutputTokens = cfgRetryTokens;
        }
        else
        {
            _maxCompactRetryOutputTokens = Math.Max(24576, _maxOutputTokens);
        }

        // Adaptive category budgets
        _budgetDtoOrModel = ParseConfigBudget(configuration, "DeveloperAgent:TokenBudgets:DtoOrModel", 4096);
        _budgetInterfaceOrContract = ParseConfigBudget(configuration, "DeveloperAgent:TokenBudgets:InterfaceOrContract", 4096);
        _budgetQueryOrCommand = ParseConfigBudget(configuration, "DeveloperAgent:TokenBudgets:QueryOrCommand", 4096);
        _budgetHandlerOrService = ParseConfigBudget(configuration, "DeveloperAgent:TokenBudgets:HandlerOrService", 8192);
        _budgetController = ParseConfigBudget(configuration, "DeveloperAgent:TokenBudgets:Controller", 8192);
        _budgetModifyPatch = ParseConfigBudget(configuration, "DeveloperAgent:TokenBudgets:ModifyPatch", 4096);
        _budgetTestFile = ParseConfigBudget(configuration, "DeveloperAgent:TokenBudgets:TestFile", 8192);
        _budgetFallback = ParseConfigBudget(configuration, "DeveloperAgent:TokenBudgets:Fallback", 8192);

        if (configuration != null &&
            int.TryParse(configuration["DeveloperAgent:MaxManifestFiles"], out var cfgManifest) &&
            cfgManifest > 0)
        {
            _maxManifestFiles = cfgManifest;
        }
        else
        {
            _maxManifestFiles = ExecutionCapacityPolicy.MaxImpactedFiles;
        }

        if (configuration != null &&
            int.TryParse(configuration["DeveloperAgent:MaxGenerationCalls"], out var cfgCalls) &&
            cfgCalls > 0)
        {
            _maxGenerationCalls = cfgCalls;
        }
        else
        {
            _maxGenerationCalls = ExecutionCapacityPolicy.MaxGenerationCalls;
        }

        if (configuration != null &&
            int.TryParse(configuration["DeveloperAgent:MaxConcurrentFileGenerations"], out var cfgConcurrent) &&
            cfgConcurrent > 0)
        {
            _maxConcurrentFileGenerations = Math.Clamp(cfgConcurrent, 1, 4);
        }
        else
        {
            _maxConcurrentFileGenerations = 1;
        }

        var configuredReasoning = configuration?["DeveloperAgent:MechanicalReasoningEffort"];
        _mechanicalReasoningEffort = string.IsNullOrWhiteSpace(configuredReasoning)
            ? "low"
            : configuredReasoning.Trim();
    }

    private int ParseConfigBudget(IConfiguration? config, string key, int defaultValue)
    {
        if (config != null && int.TryParse(config[key], out var val) && val > 0)
        {
            return Math.Min(val, _maxOutputTokens);
        }
        return Math.Min(defaultValue, _maxOutputTokens);
    }

    public int DetermineInitialBudget(string filePath, FileEditAction action, string? targetContent = null)
    {
        if (action == FileEditAction.Modify)
        {
            // Existing-file Modify is always SEARCH/REPLACE. The budget is a bounded
            // patch contract and must not scale with the current file size.
            return Math.Min(Math.Min(_budgetModifyPatch, 4096), _maxOutputTokens);
        }

        if (ProjectGraphHelper.IsTestFileCandidate(filePath))
        {
            return Math.Min(_budgetTestFile, _maxOutputTokens);
        }

        // Ordinary source Create stays a bounded compile-complete new file.
        // Do not start at 8K merely because the semantic layer is a service/controller.
        return Math.Min(4096, _maxOutputTokens);
    }

    public int DetermineFocusedRepairBudget()
    {
        // Lightweight verification repair is always a compact SEARCH/REPLACE response.
        // Do not use the small-file full-file budget and do not escalate tokens.
        return Math.Min(Math.Min(_budgetModifyPatch, 8192), _maxOutputTokens);
    }

    public int DetermineCompactRetryBudget(int initialBudget, string? targetContent, ManifestFileEntry fileEntry, bool isRepair = false)
    {
        if (fileEntry.Action == FileEditAction.Modify)
        {
            // Modify recovery stays a compact SEARCH/REPLACE response. It must become
            // more concise, not receive a larger allowance.
            var boundedPatch = Math.Min(Math.Min(_budgetModifyPatch, 4096), _maxOutputTokens);
            return Math.Min(initialBudget, boundedPatch);
        }

        // Create recovery stays a bounded newContent file. Never double 8K->16K->32K.
        // Ordinary source and tests keep the same or a lower cap; tests may retain 8192.
        var isTestFile = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);
        var createCap = isTestFile
            ? Math.Min(_budgetTestFile, 8192)
            : Math.Min(4096, _maxOutputTokens);
        return Math.Min(Math.Min(initialBudget, createCap), Math.Min(_maxOutputTokens, _maxCompactRetryOutputTokens));
    }

    private AiRequest CreateMechanicalRequest(string? model, string? systemPrompt, string userPrompt, int? maxTokens)
    {
        return new AiRequest
        {
            Model = model ?? string.Empty,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            MaxTokens = maxTokens,
            ReasoningEffort = _mechanicalReasoningEffort
        };
    }

    public async Task<DeveloperAgentResult> ExecuteFocusedRepairAsync(
        FocusedRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        if (request.RepairFiles == null || request.RepairFiles.Count == 0)
        {
            return DeveloperAgentResult.Fail("No repair files specified for focused repair.", request.Model);
        }

        var filePath = request.RepairFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return DeveloperAgentResult.Fail("No repair files specified for focused repair.", request.Model);
        }

        _logger.LogInformation(
            "DeveloperAgent: starting lightweight focused repair for task {TaskId} on '{FilePath}' in workspace '{Workspace}'.",
            request.TaskId,
            filePath,
            request.WorkspacePath);

        string resolvedPath;
        try
        {
            resolvedPath = WorktreeEditApplier.ValidateAndResolvePath(request.WorkspacePath, filePath);
        }
        catch (Exception ex)
        {
            return DeveloperAgentResult.Fail($"Failed to resolve path for '{filePath}': {ex.Message}", request.Model);
        }

        if (!File.Exists(resolvedPath))
        {
            return DeveloperAgentResult.Fail($"Repair target file '{filePath}' does not exist on disk.", request.Model);
        }

        var currentBytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        var currentContent = WorktreeEditApplier.DecodeUtf8Text(currentBytes, out _) ?? string.Empty;

        request = BoundFocusedRepairToSelectedFile(request, filePath);

        var manifestEntry = new ManifestFileEntry(filePath, FileEditAction.Modify);
        var budget = DetermineFocusedRepairBudget();
        var peerContext = CollectFocusedRepairPeerContext(
            request.WorkspacePath,
            filePath,
            request,
            currentContent);
        var boundedTarget = BuildFocusedRepairTargetWindow(
            currentContent,
            request.DiagnosticLocations,
            request.DiagnosticEvidence);

        var callKind = request.IsFinalDiagnosticAttempt ? "FinalDiagnosticRepair" : "FocusedVerificationRepair";
        var systemPrompt = BuildFocusedDiagnosticRepairSystemPrompt(filePath, request.IsFinalDiagnosticAttempt);
        var userPrompt = BuildFocusedDiagnosticRepairUserPrompt(
            filePath,
            boundedTarget,
            request,
            peerContext);

        var primaryCall = await SendMechanicalRepairCallAsync(
            request,
            filePath,
            callKind,
            systemPrompt,
            userPrompt,
            budget,
            cancellationToken).ConfigureAwait(false);
        if (primaryCall.Error != null)
        {
            return DeveloperAgentResult.Fail(primaryCall.Error, request.Model);
        }

        var aiResponse = primaryCall.Response!;

        if (aiResponse.FailureKind == AiFailureKind.TokenLimitExceeded)
        {
            return await ExecuteMicroDiagnosticRepairAsync(
                request,
                filePath,
                resolvedPath,
                manifestEntry,
                budget,
                cancellationToken).ConfigureAwait(false);
        }

        if (aiResponse.FailureKind != AiFailureKind.None && string.IsNullOrWhiteSpace(aiResponse.Content))
        {
            return DeveloperAgentResult.Fail($"Focused repair AI provider call failed ({aiResponse.FailureKind}).", request.Model);
        }

        if (!TryBuildFocusedRepairPlan(aiResponse.Content, manifestEntry, currentContent, filePath, out var structuredPlan, out var parseError))
        {
            return DeveloperAgentResult.Fail($"Focused repair output was invalid: {parseError}", request.Model);
        }

        var applyResult = await _editApplier.ApplyEditsAsync(request.WorkspacePath, request.BranchName, structuredPlan, cancellationToken).ConfigureAwait(false);
        if (applyResult.Success)
        {
            return DeveloperAgentResult.Ok(applyResult.ModifiedFiles ?? new[] { filePath }, model: request.Model);
        }

        if (IsMissingSearchMatchFailure(applyResult.ErrorMessage))
        {
            return await ExecuteAnchorRefreshRepairAsync(
                request,
                filePath,
                resolvedPath,
                manifestEntry,
                budget,
                cancellationToken).ConfigureAwait(false);
        }

        return DeveloperAgentResult.Fail(applyResult.ErrorMessage ?? "Focused repair apply failed.", request.Model);
    }

    private async Task<DeveloperAgentResult> ExecuteAnchorRefreshRepairAsync(
        FocusedRepairRequest request,
        string filePath,
        string resolvedPath,
        ManifestFileEntry manifestEntry,
        int budget,
        CancellationToken cancellationToken)
    {
        var currentBytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        var currentContent = WorktreeEditApplier.DecodeUtf8Text(currentBytes, out _) ?? string.Empty;
        var boundedTarget = BuildFocusedRepairTargetWindow(
            currentContent,
            request.DiagnosticLocations,
            request.DiagnosticEvidence);

        var refreshCall = await SendMechanicalRepairCallAsync(
            request,
            filePath,
            "AnchorRefreshRepair",
            BuildAnchorRefreshRepairSystemPrompt(filePath),
            BuildAnchorRefreshRepairUserPrompt(filePath, boundedTarget, request),
            budget,
            cancellationToken).ConfigureAwait(false);
        if (refreshCall.Error != null)
        {
            return DeveloperAgentResult.Fail(refreshCall.Error, request.Model);
        }

        var aiResponse = refreshCall.Response!;

        if (aiResponse.FailureKind != AiFailureKind.None && string.IsNullOrWhiteSpace(aiResponse.Content))
        {
            return DeveloperAgentResult.Fail($"AnchorRefreshRepair AI provider call failed ({aiResponse.FailureKind}).", request.Model);
        }

        if (!TryBuildFocusedRepairPlan(aiResponse.Content, manifestEntry, currentContent, filePath, out var structuredPlan, out var parseError))
        {
            return DeveloperAgentResult.Fail($"AnchorRefreshRepair output was invalid: {parseError}", request.Model);
        }

        var applyResult = await _editApplier.ApplyEditsAsync(request.WorkspacePath, request.BranchName, structuredPlan, cancellationToken).ConfigureAwait(false);
        if (!applyResult.Success)
        {
            return DeveloperAgentResult.Fail(applyResult.ErrorMessage ?? "AnchorRefreshRepair apply failed.", request.Model);
        }

        return DeveloperAgentResult.Ok(applyResult.ModifiedFiles ?? new[] { filePath }, model: request.Model);
    }

    private async Task<DeveloperAgentResult> ExecuteMicroDiagnosticRepairAsync(
        FocusedRepairRequest request,
        string filePath,
        string resolvedPath,
        ManifestFileEntry manifestEntry,
        int priorBudget,
        CancellationToken cancellationToken)
    {
        var currentBytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        var currentContent = WorktreeEditApplier.DecodeUtf8Text(currentBytes, out _) ?? string.Empty;
        var microBudget = Math.Min(priorBudget, DetermineFocusedRepairBudget());
        var microRequest = BoundFocusedRepairToSelectedFile(request, filePath);
        var boundedTarget = BuildFocusedRepairTargetWindow(
            currentContent,
            microRequest.DiagnosticLocations,
            microRequest.DiagnosticEvidence,
            contextRadius: 6,
            smallFileMaxLines: 36,
            smallFileMaxChars: 1800);

        var microCall = await SendMechanicalRepairCallAsync(
            microRequest,
            filePath,
            "MicroDiagnosticRepair",
            BuildMicroDiagnosticRepairSystemPrompt(filePath),
            BuildMicroDiagnosticRepairUserPrompt(filePath, boundedTarget, microRequest),
            microBudget,
            cancellationToken).ConfigureAwait(false);
        if (microCall.Error != null)
        {
            return DeveloperAgentResult.Fail(microCall.Error, request.Model);
        }

        var aiResponse = microCall.Response!;

        if (aiResponse.FailureKind != AiFailureKind.None && string.IsNullOrWhiteSpace(aiResponse.Content))
        {
            return DeveloperAgentResult.Fail($"MicroDiagnosticRepair AI provider call failed ({aiResponse.FailureKind}).", request.Model);
        }

        if (!TryBuildFocusedRepairPlan(aiResponse.Content, manifestEntry, currentContent, filePath, out var structuredPlan, out var parseError))
        {
            return DeveloperAgentResult.Fail($"MicroDiagnosticRepair output was invalid: {parseError}", request.Model);
        }

        var applyResult = await _editApplier.ApplyEditsAsync(request.WorkspacePath, request.BranchName, structuredPlan, cancellationToken).ConfigureAwait(false);
        if (!applyResult.Success)
        {
            return DeveloperAgentResult.Fail(applyResult.ErrorMessage ?? "MicroDiagnosticRepair apply failed.", request.Model);
        }

        return DeveloperAgentResult.Ok(applyResult.ModifiedFiles ?? new[] { filePath }, model: request.Model);
    }

    private async Task<(AiRequest? Request, AiResponse? Response, string? Error)> SendMechanicalRepairCallAsync(
        FocusedRepairRequest request,
        string filePath,
        string callKind,
        string systemPrompt,
        string userPrompt,
        int budget,
        CancellationToken cancellationToken)
    {
        var aiRequest = CreateMechanicalRequest(request.Model, systemPrompt, userPrompt, budget);
        var repairSw = Stopwatch.StartNew();
        AiResponse aiResponse;
        try
        {
            aiResponse = await _aiProvider.SendAsync(aiRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (aiRequest, null, $"{callKind} AI call failed: {ex.Message}");
        }
        repairSw.Stop();

        await RecordProviderCallActivityAsync(
            request.ExecutionId,
            filePath,
            callKind,
            aiRequest,
            aiResponse,
            repairSw.Elapsed,
            cancellationToken).ConfigureAwait(false);

        return (aiRequest, aiResponse, null);
    }

    private static bool TryBuildFocusedRepairPlan(
        string? content,
        ManifestFileEntry manifestEntry,
        string currentContent,
        string filePath,
        out StructuredEditPlan plan,
        out string error)
    {
        plan = new StructuredEditPlan(Array.Empty<FileEditSpec>());
        error = string.Empty;
        try
        {
            var editSpec = ParseSingleFileEditSpec(content ?? string.Empty, manifestEntry);
            ValidateSingleFileEditSpec(editSpec, manifestEntry, currentContent, useFullFileReplacement: false);
            ValidateFocusedRepairSearchAnchors(editSpec, filePath);
            var structuredPlan = new StructuredEditPlan(new[] { editSpec });
            ValidateStructuredPlan(structuredPlan);
            plan = structuredPlan;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsMissingSearchMatchFailure(string? errorMessage) =>
        !string.IsNullOrWhiteSpace(errorMessage) &&
        errorMessage.Contains("Missing search match", StringComparison.OrdinalIgnoreCase);

    public static FocusedRepairRequest BoundFocusedRepairToSelectedFile(FocusedRepairRequest request, string filePath)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return request;
        }

        var evidenceLines = (request.DiagnosticEvidence ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (evidenceLines.Any(ExecutionDiagnosticEvidence.IsCompilerDiagnosticLine))
        {
            evidenceLines = evidenceLines
                .Where(line => ExecutionDiagnosticEvidence.DiagnosticLineBelongsToFile(line, filePath))
                .Take(ExecutionDiagnosticEvidence.MaxScopedDiagnosticsPerFile)
                .ToList();
        }

        var locations = (request.DiagnosticLocations ?? Array.Empty<string>()).ToList();
        if (locations.Any(location => !string.IsNullOrWhiteSpace(ExtractPathFromDiagnosticLocation(location))))
        {
            locations = locations
                .Where(location =>
                {
                    var path = ExtractPathFromDiagnosticLocation(location);
                    return path != null && ExecutionDiagnosticEvidence.PathMatchesFile(path, filePath);
                })
                .Take(ExecutionDiagnosticEvidence.MaxScopedDiagnosticsPerFile)
                .ToList();
        }

        return request with
        {
            DiagnosticEvidence = string.Join('\n', evidenceLines),
            DiagnosticLocations = locations
        };
    }

    public async Task<DeveloperAgentResult> GenerateAndApplyEditsAsync(
        DeveloperAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "DeveloperAgent: starting multi-step edit generation for task {TaskId} in workspace '{Workspace}' on branch '{Branch}'.",
            request.TaskId,
            request.WorkspacePath,
            request.BranchName);

        // 1. Read context files safely within limits
        IReadOnlyDictionary<string, string> contextFiles;
        try
        {
            contextFiles = await _editApplier.ReadContextFilesAsync(
                request.WorkspacePath,
                request.BranchName,
                request.ImpactedFilePaths,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeveloperAgent: failed to read context files for task {TaskId}.", request.TaskId);
            return DeveloperAgentResult.Fail($"Context loading failed: {ex.Message}");
        }

        var projectGraph = WorktreeEditApplier.DiscoverProjectGraph(request.WorkspacePath);
        var projectRoots = WorktreeEditApplier.DiscoverProjectRoots(request.WorkspacePath);

        // 2. Phase 1 — Derive Edit Manifest directly from approved completed TaskImpactAnalysis
        EditManifest manifest;
        try
        {
            manifest = BuildManifestFromImpactAnalysis(request, request.WorkspacePath, projectRoots, _maxManifestFiles, projectGraph);
            _logger.LogInformation(
                "DeveloperAgent: derived edit manifest with {Count} files from approved impact analysis for task {TaskId}.",
                manifest.Files.Count,
                request.TaskId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "DeveloperAgent: failed to derive edit manifest from approved impact analysis for task {TaskId}: {Error}",
                request.TaskId,
                ex.Message);
            return DeveloperAgentResult.Fail($"Edit manifest construction failed: {ex.Message}");
        }

        // 3. Sort files in deterministic dependency order
        var sortedFiles = SortManifestEntries(manifest.Files, projectGraph);

        _logger.LogInformation(
            "DeveloperAgent: manifest validated with {Count} files for task {TaskId}. Starting per-file edit generation.",
            sortedFiles.Count,
            request.TaskId);

        // 4. Phase 2 — Generate File Edits with Dependency-Ready DAG Scheduler
        var collectedEdits = new ConcurrentDictionary<string, FileEditSpec>(StringComparer.OrdinalIgnoreCase);
        var virtualWorkspace = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lockedContracts = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var capturedModels = new ConcurrentBag<string>();
        var callCounter = new ConcurrencyCallCounter(_maxGenerationCalls);
        int totalFiles = sortedFiles.Count;

        var fileIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sortedFiles.Count; i++)
        {
            fileIndexMap[sortedFiles[i].FilePath] = i + 1;
        }

        await SafeRecordActivityAsync(request.ExecutionId, $"Preparing {totalFiles} file edits.", cancellationToken).ConfigureAwait(false);

        // Ensure all Modify files have their content loaded into allContextFiles
        var allContextFiles = new Dictionary<string, string>(contextFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in sortedFiles)
        {
            if (file.Action == FileEditAction.Modify && !allContextFiles.ContainsKey(file.FilePath))
            {
                try
                {
                    var resolved = WorktreeEditApplier.ValidateAndResolvePath(request.WorkspacePath, file.FilePath);
                    if (File.Exists(resolved))
                    {
                        var bytes = await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
                        if (!WorktreeEditApplier.IsBinaryContent(bytes))
                        {
                            allContextFiles[file.FilePath] = WorktreeEditApplier.DecodeUtf8Text(bytes, out _);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DeveloperAgent: failed to pre-load content for '{FilePath}'.", file.FilePath);
                }
            }
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var generationStopwatch = Stopwatch.StartNew();
        var runTelemetry = new GenerationRunState(_maxConcurrentFileGenerations);

        try
        {
            await ExecuteDagGenerationAsync(
                sortedFiles,
                fileIndexMap,
                totalFiles,
                request,
                allContextFiles,
                collectedEdits,
                virtualWorkspace,
                lockedContracts,
                projectGraph,
                callCounter,
                capturedModels,
                runTelemetry,
                linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            generationStopwatch.Stop();
            linkedCts.Cancel();
            _logger.LogError(ex, "DeveloperAgent: multi-step edit generation failed for task {TaskId}.", request.TaskId);
            await SafeRecordActivityAsync(
                request.ExecutionId,
                "Generation technical summary.",
                cancellationToken,
                ExecutionActivityStatus.Failed,
                BuildGenerationSummaryMetadata(callCounter, generationStopwatch, runTelemetry)).ConfigureAwait(false);
            return DeveloperAgentResult.Fail(ex.Message, model: capturedModels.FirstOrDefault() ?? request.Model);
        }

        generationStopwatch.Stop();
        await SafeRecordActivityAsync(
            request.ExecutionId,
            "Generation technical summary.",
            cancellationToken,
            ExecutionActivityStatus.Completed,
            BuildGenerationSummaryMetadata(callCounter, generationStopwatch, runTelemetry)).ConfigureAwait(false);

        // 5. Phase 3 — Atomically Validate and Apply Edits
        await SafeRecordActivityAsync(request.ExecutionId, $"Validating {totalFiles} generated edits.", cancellationToken).ConfigureAwait(false);

        var orderedEdits = sortedFiles
            .Select(f => collectedEdits.TryGetValue(f.FilePath, out var spec) ? spec : null)
            .Where(s => s != null)
            .Cast<FileEditSpec>()
            .ToList();

        if (orderedEdits.Count != totalFiles)
        {
            return DeveloperAgentResult.Fail($"Failed to generate edits for all planned files ({orderedEdits.Count}/{totalFiles} generated).", model: capturedModels.FirstOrDefault() ?? request.Model);
        }

        var completePlan = new StructuredEditPlan(orderedEdits);
        try
        {
            ValidateStructuredPlan(completePlan);
        }
        catch (Exception ex)
        {
            return DeveloperAgentResult.Fail($"Complete edit plan validation failed: {ex.Message}", model: capturedModels.FirstOrDefault() ?? request.Model);
        }

        await SafeRecordActivityAsync(request.ExecutionId, "Applying generated edits.", cancellationToken).ConfigureAwait(false);

        var applyResult = await _editApplier.ApplyEditsAsync(
            request.WorkspacePath,
            request.BranchName,
            completePlan,
            cancellationToken).ConfigureAwait(false);

        return applyResult with { Model = capturedModels.FirstOrDefault() ?? request.Model };
    }

    private async Task ExecuteDagGenerationAsync(
        IReadOnlyList<ManifestFileEntry> sortedFiles,
        IReadOnlyDictionary<string, int> fileIndexMap,
        int totalFiles,
        DeveloperAgentRequest request,
        IReadOnlyDictionary<string, string> contextFiles,
        ConcurrentDictionary<string, FileEditSpec> collectedEdits,
        ConcurrentDictionary<string, string> virtualWorkspace,
        ConcurrentDictionary<string, string> lockedContracts,
        IReadOnlyList<DiscoveredProjectNode> projectGraph,
        ConcurrencyCallCounter callCounter,
        ConcurrentBag<string> capturedModels,
        GenerationRunState runTelemetry,
        CancellationToken cancellationToken)
    {
        var prerequisites = GenerationDependencyAnalyzer.BuildPrerequisiteMap(sortedFiles, contextFiles);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<ManifestFileEntry>>(StringComparer.OrdinalIgnoreCase);
        var fileByPath = sortedFiles.ToDictionary(file => file.FilePath, StringComparer.OrdinalIgnoreCase);

        foreach (var file in sortedFiles)
        {
            inDegree[file.FilePath] = 0;
            dependents[file.FilePath] = new List<ManifestFileEntry>();
        }

        foreach (var (consumerPath, producers) in prerequisites)
        {
            foreach (var producerPath in producers)
            {
                if (!fileByPath.TryGetValue(producerPath, out var producer) ||
                    !fileByPath.ContainsKey(consumerPath))
                {
                    continue;
                }

                inDegree[consumerPath]++;
                dependents[producer.FilePath].Add(fileByPath[consumerPath]);
            }
        }

        var readySet = new SortedSet<ManifestFileEntry>(new ReadyFileComparer());
        var readyAt = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var schedulerStart = Stopwatch.StartNew();
        foreach (var file in sortedFiles)
        {
            if (inDegree[file.FilePath] == 0)
            {
                readySet.Add(file);
                readyAt[file.FilePath] = 0;
            }
        }

        int remainingFiles = sortedFiles.Count;
        var stateLock = new object();
        using var concurrencySemaphore = new SemaphoreSlim(_maxConcurrentFileGenerations, _maxConcurrentFileGenerations);
        var activeTasks = new List<Task>();

        ManifestFileEntry? TryTakeReadyFile()
        {
            lock (stateLock)
            {
                if (readySet.Count == 0)
                {
                    return null;
                }

                var next = readySet.Min!;
                readySet.Remove(next);
                return next;
            }
        }

        void MarkReady(ManifestFileEntry file)
        {
            lock (stateLock)
            {
                if (readySet.Add(file))
                {
                    readyAt.TryAdd(file.FilePath, schedulerStart.ElapsedMilliseconds);
                }
            }
        }

        while (remainingFiles > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nextFile = TryTakeReadyFile();
            if (nextFile != null)
            {
                await concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                var queuedFile = nextFile;
                var dependencyWaitMs = readyAt.TryGetValue(queuedFile.FilePath, out var becameReadyAt) ? becameReadyAt : 0;
                var queueWaitMs = Math.Max(0, schedulerStart.ElapsedMilliseconds - dependencyWaitMs);
                var activeAtStart = runTelemetry.BeginFile();

                var task = Task.Run(async () =>
                {
                    try
                    {
                        var editSpec = await GenerateSingleFileEditAsync(
                            queuedFile,
                            fileIndexMap[queuedFile.FilePath],
                            totalFiles,
                            request,
                            contextFiles,
                            collectedEdits,
                            virtualWorkspace,
                            lockedContracts,
                            projectGraph,
                            callCounter,
                            capturedModels,
                            runTelemetry,
                            queueWaitMs,
                            dependencyWaitMs,
                            activeAtStart,
                            cancellationToken).ConfigureAwait(false);

                        collectedEdits[queuedFile.FilePath] = editSpec;

                        lock (stateLock)
                        {
                            Interlocked.Decrement(ref remainingFiles);
                            foreach (var dep in dependents[queuedFile.FilePath])
                            {
                                inDegree[dep.FilePath]--;
                                if (inDegree[dep.FilePath] == 0)
                                {
                                    MarkReady(dep);
                                }
                            }
                        }
                    }
                    finally
                    {
                        runTelemetry.EndFile();
                        concurrencySemaphore.Release();
                    }
                }, cancellationToken);

                activeTasks.Add(task);
                continue;
            }

            if (activeTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(activeTasks).ConfigureAwait(false);
                activeTasks.Remove(completedTask);
                await completedTask.ConfigureAwait(false);
                continue;
            }

            if (remainingFiles > 0)
            {
                lock (stateLock)
                {
                    var lowest = sortedFiles
                        .Where(f => !collectedEdits.ContainsKey(f.FilePath) && !readySet.Contains(f))
                        .OrderBy(f => inDegree[f.FilePath])
                        .ThenBy(f => GetSemanticLayerScore(f.FilePath))
                        .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    if (lowest != null)
                    {
                        inDegree[lowest.FilePath] = 0;
                        MarkReady(lowest);
                    }
                }
            }
        }

        if (activeTasks.Count > 0)
        {
            await Task.WhenAll(activeTasks).ConfigureAwait(false);
        }
    }

    private async Task<FileEditSpec> GenerateSingleFileEditAsync(
        ManifestFileEntry fileEntry,
        int fileIndex,
        int totalFiles,
        DeveloperAgentRequest request,
        IReadOnlyDictionary<string, string> contextFiles,
        IReadOnlyDictionary<string, FileEditSpec> completedEdits,
        ConcurrentDictionary<string, string> virtualWorkspace,
        ConcurrentDictionary<string, string> lockedContracts,
        IReadOnlyList<DiscoveredProjectNode> projectGraph,
        ConcurrencyCallCounter callCounter,
        ConcurrentBag<string> capturedModels,
        GenerationRunState runTelemetry,
        long queueWaitMs,
        long dependencyWaitMs,
        int activeGenerationCount,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(fileEntry.FilePath);
        var fileGenerationStopwatch = Stopwatch.StartNew();

        await SafeRecordActivityAsync(
            request.ExecutionId,
            $"Generating edit {fileIndex}/{totalFiles} · {fileName}",
            cancellationToken).ConfigureAwait(false);

        string? targetContent = null;
        string? targetContentHash = null;

        if (fileEntry.Action == FileEditAction.Modify)
        {
            if (contextFiles.TryGetValue(fileEntry.FilePath, out var loadedContent))
            {
                targetContent = loadedContent;
            }
            else
            {
                var resolvedPath = WorktreeEditApplier.ValidateAndResolvePath(request.WorkspacePath, fileEntry.FilePath);
                if (!File.Exists(resolvedPath))
                {
                    throw new InvalidOperationException($"Strict Modify action failed: target file does not exist at '{fileEntry.FilePath}'.");
                }

                var originalBytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
                if (WorktreeEditApplier.IsBinaryContent(originalBytes))
                {
                    throw new InvalidOperationException($"Target file '{fileEntry.FilePath}' is a binary file and cannot be modified.");
                }

                targetContent = WorktreeEditApplier.DecodeUtf8Text(originalBytes, out _);
            }

            targetContentHash = WorktreeEditApplier.ComputeContentHash(targetContent);
        }

        // Existing-file Modify is always SEARCH/REPLACE. Create still uses full newContent.
        var useFullFileReplacement = false;
        var recoveryUsed = false;

        if (!callCounter.TryIncrement(out var callNumber))
        {
            throw new InvalidOperationException($"Developer Agent exceeded maximum generation call limit ({_maxGenerationCalls}) at file '{fileEntry.FilePath}'.");
        }

        int initialBudget = DetermineInitialBudget(fileEntry.FilePath, fileEntry.Action, targetContent);
        var referencePattern = RoslynPatternHelper.DiscoverNearestPattern(request.WorkspacePath, fileEntry.FilePath);

        var singleFileSystemPrompt = BuildSingleFileSystemPrompt(fileEntry, useFullFileReplacement);
        var singleFileUserPrompt = BuildSingleFileUserPrompt(
            request, fileEntry, contextFiles, completedEdits, projectGraph, lockedContracts, referencePattern, useFullFileReplacement, virtualWorkspace);

        var fileAiRequest = CreateMechanicalRequest(
            request.Model,
            singleFileSystemPrompt,
            singleFileUserPrompt,
            initialBudget);

        var fileSw = Stopwatch.StartNew();
        AiResponse fileResponse;
        try
        {
            fileResponse = await _aiProvider.SendAsync(fileAiRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeveloperAgent: AI provider call failed for file '{FilePath}' in task {TaskId}.", fileEntry.FilePath, request.TaskId);
            throw new InvalidOperationException($"AI provider request failed for file '{fileEntry.FilePath}'.", ex);
        }
        fileSw.Stop();

        if (!string.IsNullOrWhiteSpace(fileResponse.Model))
        {
            capturedModels.Add(fileResponse.Model);
        }

        LogGenerationAudit(fileEntry.FilePath, fileEntry.Action.ToString(), callNumber, fileAiRequest, fileResponse, fileSw.Elapsed);
        runTelemetry.AddProviderDuration(fileSw.ElapsedMilliseconds);
        await RecordProviderCallActivityAsync(
            request.ExecutionId,
            fileEntry.FilePath,
            "Generation",
            fileAiRequest,
            fileResponse,
            fileSw.Elapsed,
            cancellationToken,
            queueWaitMs,
            dependencyWaitMs,
            activeGenerationCount,
            retryOccurred: false).ConfigureAwait(false);

        // File-local token recovery: one bounded retry for this file only.
        // Modify: surgical retry, then one micro window retry. Create: one minimal retry at the same budget.
        if (fileResponse.FailureKind == AiFailureKind.TokenLimitExceeded ||
            string.Equals(fileResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            int compactBudget = DetermineCompactRetryBudget(initialBudget, targetContent, fileEntry, isRepair: false);
            var isModify = fileEntry.Action == FileEditAction.Modify;
            var isCreate = fileEntry.Action == FileEditAction.Create;
            if ((isModify || isCreate) && callCounter.TryIncrement(out var escCallNumber))
            {
                recoveryUsed = true;
                callCounter.RecordCompactRetry();
                var retryKind = isModify ? "SurgicalModifyRetry" : "MinimalCreateRetry";
                await SafeRecordActivityAsync(
                    request.ExecutionId,
                    isModify
                        ? $"Performing surgical modify retry for {fileName} (budget {initialBudget} -> {compactBudget}) due to token length limit."
                        : $"Performing minimal create retry for {fileName} (budget {initialBudget} -> {compactBudget}) due to token length limit.",
                    cancellationToken).ConfigureAwait(false);

                var compactUserPrompt = isCreate
                    ? BuildMinimalCreateRetryUserPrompt(request, fileEntry, lockedContracts, virtualWorkspace)
                    : BuildCompactSingleFileUserPrompt(
                        request, fileEntry, targetContent, lockedContracts, useFullFileReplacement, virtualWorkspace);
                var compactSystemPrompt = isModify
                    ? BuildSurgicalModifyRetrySystemPrompt(fileEntry)
                    : BuildMinimalCreateRetrySystemPrompt(fileEntry);

                var compactRequest = CreateMechanicalRequest(
                    request.Model,
                    compactSystemPrompt,
                    compactUserPrompt,
                    compactBudget);

                try
                {
                    var escSw = Stopwatch.StartNew();
                    var compactResponse = await _aiProvider.SendAsync(compactRequest, cancellationToken).ConfigureAwait(false);
                    escSw.Stop();

                    if (!string.IsNullOrWhiteSpace(compactResponse.Model))
                    {
                        capturedModels.Add(compactResponse.Model);
                    }

                    LogGenerationAudit(fileEntry.FilePath, retryKind, escCallNumber, compactRequest, compactResponse, escSw.Elapsed, isCompactRetry: true);
                    runTelemetry.AddProviderDuration(escSw.ElapsedMilliseconds);
                    runTelemetry.RecordRetry();
                    await RecordProviderCallActivityAsync(
                        request.ExecutionId,
                        fileEntry.FilePath,
                        retryKind,
                        compactRequest,
                        compactResponse,
                        escSw.Elapsed,
                        cancellationToken,
                        queueWaitMs,
                        dependencyWaitMs,
                        activeGenerationCount,
                        retryOccurred: true).ConfigureAwait(false);

                    if (compactResponse.IsSuccess)
                    {
                        fileResponse = compactResponse;
                    }
                    else if (string.Equals(compactResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                             compactResponse.FailureKind == AiFailureKind.TokenLimitExceeded)
                    {
                        fileResponse = compactResponse;
                    }
                    else
                    {
                        throw new InvalidOperationException($"AI response exhausted the configured output token limit while generating edits for '{fileEntry.FilePath}'.");
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DeveloperAgent: compact retry call failed for file '{FilePath}'.", fileEntry.FilePath);
                    throw new InvalidOperationException($"AI response exhausted the configured output token limit while generating edits for '{fileEntry.FilePath}'.");
                }
            }

            var stillTruncated = fileResponse.FailureKind == AiFailureKind.TokenLimitExceeded ||
                                 string.Equals(fileResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
            if (isModify && stillTruncated && callCounter.TryIncrement(out var microCallNumber))
            {
                recoveryUsed = true;
                callCounter.RecordCompactRetry();
                await SafeRecordActivityAsync(
                    request.ExecutionId,
                    $"Performing micro modify retry for {fileName} using a bounded target window.",
                    cancellationToken).ConfigureAwait(false);

                var microSystemPrompt = BuildMicroModifyRetrySystemPrompt(fileEntry);
                var microUserPrompt = BuildMicroModifyRetryUserPrompt(
                    request, fileEntry, targetContent, lockedContracts, virtualWorkspace);
                var microBudget = Math.Min(Math.Min(_budgetModifyPatch, 4096), _maxOutputTokens);
                var microRequest = CreateMechanicalRequest(
                    request.Model,
                    microSystemPrompt,
                    microUserPrompt,
                    microBudget);

                try
                {
                    var microSw = Stopwatch.StartNew();
                    var microResponse = await _aiProvider.SendAsync(microRequest, cancellationToken).ConfigureAwait(false);
                    microSw.Stop();

                    if (!string.IsNullOrWhiteSpace(microResponse.Model))
                    {
                        capturedModels.Add(microResponse.Model);
                    }

                    LogGenerationAudit(fileEntry.FilePath, "MicroModifyRetry", microCallNumber, microRequest, microResponse, microSw.Elapsed, isCompactRetry: true);
                    runTelemetry.AddProviderDuration(microSw.ElapsedMilliseconds);
                    runTelemetry.RecordRetry();
                    await RecordProviderCallActivityAsync(
                        request.ExecutionId,
                        fileEntry.FilePath,
                        "MicroModifyRetry",
                        microRequest,
                        microResponse,
                        microSw.Elapsed,
                        cancellationToken,
                        queueWaitMs,
                        dependencyWaitMs,
                        activeGenerationCount,
                        retryOccurred: true).ConfigureAwait(false);

                    if (microResponse.IsSuccess)
                    {
                        fileResponse = microResponse;
                    }
                    else if (string.Equals(microResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                             microResponse.FailureKind == AiFailureKind.TokenLimitExceeded)
                    {
                        fileResponse = microResponse;
                    }
                    else
                    {
                        throw new InvalidOperationException($"AI response exhausted the configured output token limit while generating edits for '{fileEntry.FilePath}'.");
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DeveloperAgent: micro modify retry call failed for file '{FilePath}'.", fileEntry.FilePath);
                    throw new InvalidOperationException($"AI response exhausted the configured output token limit while generating edits for '{fileEntry.FilePath}'.");
                }
            }
        }

        if (!fileResponse.IsSuccess)
        {
            if (cancellationToken.IsCancellationRequested || fileResponse.FailureKind == AiFailureKind.Cancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }

            if (fileResponse.FailureKind == AiFailureKind.TokenLimitExceeded ||
                string.Equals(fileResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                (fileResponse.ErrorMessage != null && fileResponse.ErrorMessage.Contains("exhausted the configured output token limit", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"AI response exhausted the configured output token limit while generating edits for '{fileEntry.FilePath}'.");
            }

            var classifiedError = BuildProviderFailureMessage(fileResponse, fileEntry.FilePath);
            throw new InvalidOperationException(classifiedError);
        }

        if (string.IsNullOrWhiteSpace(fileResponse.Content))
        {
            throw new InvalidOperationException($"AI provider returned empty edit response for file '{fileEntry.FilePath}'.");
        }

        FileEditSpec? editSpec = null;
        string? validationError = null;
        EditApplicabilityResult? applicabilityFailure = null;
        string? candidateCode = null;

        try
        {
            editSpec = ParseSingleFileEditSpec(fileResponse.Content, fileEntry);
            ValidateSingleFileEditSpec(editSpec, fileEntry, targetContent, useFullFileReplacement);

            if (fileEntry.Action == FileEditAction.Modify)
            {
                if (editSpec.NewContent != null)
                {
                    candidateCode = editSpec.NewContent;
                }
                else
                {
                    var appResult = WorktreeEditApplier.ValidateAndApplySearchReplaceEdits(
                        targetContent!,
                        editSpec.SearchReplaceEdits,
                        fileEntry.FilePath);

                    if (!appResult.Success)
                    {
                        validationError = appResult.ErrorMessage;
                        applicabilityFailure = appResult;
                    }
                    else
                    {
                        candidateCode = appResult.ModifiedContent;
                    }
                }
            }
            else
            {
                candidateCode = editSpec.NewContent;
            }
        }
        catch (Exception ex)
        {
            validationError = ex.Message;
        }

        if (validationError != null)
        {
            if (recoveryUsed)
            {
                throw new InvalidOperationException(
                    $"File edit validation failed for '{fileEntry.FilePath}' after the bounded compact retry: {SanitizeFailureReason(validationError, applicabilityFailure)}");
            }

            var sanitizedReason = SanitizeFailureReason(validationError, applicabilityFailure);
            _logger.LogWarning(
                "DeveloperAgent: edit response for file '{FilePath}' failed validation for task {TaskId}: {Error}. Attempting bounded repair.",
                fileEntry.FilePath,
                request.TaskId,
                sanitizedReason);

            await SafeRecordActivityAsync(
                request.ExecutionId,
                $"Repair triggered for {fileName}: {sanitizedReason}",
                cancellationToken).ConfigureAwait(false);

            if (!callCounter.TryIncrement(out var repairCallNumber))
            {
                throw new InvalidOperationException($"Developer Agent exceeded maximum generation call limit ({_maxGenerationCalls}) during repair of file '{fileEntry.FilePath}'.");
            }

            callCounter.RecordApplicabilityRepair();

            if (fileEntry.Action == FileEditAction.Modify)
            {
                var currentTarget = await ReadCurrentTargetContentAsync(
                    request.WorkspacePath,
                    fileEntry.FilePath,
                    cancellationToken).ConfigureAwait(false);
                targetContent = currentTarget.Content;
                targetContentHash = currentTarget.Hash;
                useFullFileReplacement = false;

                if (editSpec?.SearchReplaceEdits is { Count: > 0 })
                {
                    var currentApplicability = WorktreeEditApplier.ValidateAndApplySearchReplaceEdits(
                        targetContent,
                        editSpec.SearchReplaceEdits,
                        fileEntry.FilePath);
                    if (!currentApplicability.Success)
                    {
                        applicabilityFailure = currentApplicability;
                        validationError = currentApplicability.ErrorMessage;
                    }
                }
            }

            var relevantGenerated = GetRelevantGeneratedEdits(fileEntry, completedEdits, virtualWorkspace);
            var filteredLockedContracts = FilterRelevantContracts(fileEntry, lockedContracts, targetContent, request);
            var repairUserPrompt = BuildSingleFileRepairUserPrompt(
                validationError ?? "Generated edit validation failed.",
                fileResponse.Content,
                fileEntry,
                targetContent,
                relevantGenerated,
                filteredLockedContracts,
                applicabilityFailure,
                useFullFileReplacement);

            int repairBudget = DetermineInitialBudget(fileEntry.FilePath, fileEntry.Action, targetContent);
            var repairRequest = CreateMechanicalRequest(
                request.Model,
                BuildSingleFileRepairSystemPrompt(fileEntry, useFullFileReplacement),
                repairUserPrompt,
                repairBudget);

            var repairSw = Stopwatch.StartNew();
            AiResponse repairResponse;
            try
            {
                repairResponse = await _aiProvider.SendAsync(repairRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeveloperAgent: AI repair call failed for file '{FilePath}' in task {TaskId}.", fileEntry.FilePath, request.TaskId);
                throw new InvalidOperationException($"AI repair call failed for file '{fileEntry.FilePath}': {ex.Message}", ex);
            }
            repairSw.Stop();

            if (!string.IsNullOrWhiteSpace(repairResponse.Model))
            {
                capturedModels.Add(repairResponse.Model);
            }

            LogGenerationAudit(fileEntry.FilePath, "Repair", repairCallNumber, repairRequest, repairResponse, repairSw.Elapsed);
            runTelemetry.AddProviderDuration(repairSw.ElapsedMilliseconds);
            runTelemetry.RecordRetry();
            await RecordProviderCallActivityAsync(
                request.ExecutionId,
                fileEntry.FilePath,
                "ApplicabilityRepair",
                repairRequest,
                repairResponse,
                repairSw.Elapsed,
                cancellationToken,
                queueWaitMs,
                dependencyWaitMs,
                activeGenerationCount,
                retryOccurred: true).ConfigureAwait(false);

            if (!repairResponse.IsSuccess)
            {
                if (string.Equals(repairResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                    repairResponse.FailureKind == AiFailureKind.TokenLimitExceeded)
                {
                    throw new InvalidOperationException($"AI response exhausted the configured output token limit while repairing edits for '{fileEntry.FilePath}'.");
                }

                var rawError = !string.IsNullOrWhiteSpace(repairResponse.ErrorMessage)
                    ? repairResponse.ErrorMessage
                    : "AI provider repair request failed";

                var msg = rawError.Contains(fileEntry.FilePath, StringComparison.OrdinalIgnoreCase)
                    ? rawError
                    : $"{rawError.TrimEnd('.')} while repairing '{fileEntry.FilePath}'.";

                throw new InvalidOperationException(msg);
            }

            if (string.IsNullOrWhiteSpace(repairResponse.Content))
            {
                throw new InvalidOperationException($"AI provider returned empty edit response for file '{fileEntry.FilePath}' during repair.");
            }

            try
            {
                var repEditSpec = ParseSingleFileEditSpec(repairResponse.Content, fileEntry);
                ValidateSingleFileEditSpec(repEditSpec, fileEntry, targetContent, useFullFileReplacement);

                string? repCandidateCode = null;
                if (fileEntry.Action == FileEditAction.Modify)
                {
                    if (repEditSpec.NewContent != null)
                    {
                        repCandidateCode = repEditSpec.NewContent;
                    }
                    else
                    {
                        var repAppResult = WorktreeEditApplier.ValidateAndApplySearchReplaceEdits(
                            targetContent!,
                            repEditSpec.SearchReplaceEdits,
                            fileEntry.FilePath);

                        if (!repAppResult.Success)
                        {
                            throw new InvalidOperationException(repAppResult.ErrorMessage);
                        }

                        repCandidateCode = repAppResult.ModifiedContent;
                    }
                }
                else
                {
                    repCandidateCode = repEditSpec.NewContent;
                }

                editSpec = repEditSpec;
                candidateCode = repCandidateCode;
            }
            catch (Exception ex)
            {
                var singleLineReason = SanitizeForDiagnostics(ex.Message);
                throw new InvalidOperationException($"File edit validation failed for '{fileEntry.FilePath}' after repair: {singleLineReason}", ex);
            }
        }

        if (editSpec == null)
        {
            throw new InvalidOperationException($"File edit generation failed to produce a valid edit spec for '{fileEntry.FilePath}'.");
        }

        if (fileEntry.Action == FileEditAction.Modify)
        {
            editSpec = editSpec with { TargetContentHash = targetContentHash };
        }

        // Store authoritative synthesized resulting content into the virtual workspace overlay
        if (!string.IsNullOrWhiteSpace(candidateCode))
        {
            virtualWorkspace[fileEntry.FilePath] = candidateCode;
        }

        // Lock Public Contract for downstream consumers if C# file
        if (fileEntry.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(candidateCode))
        {
            var role = RoslynPatternHelper.ClassifyRole(fileEntry.FilePath) ?? "General";
            RoslynPatternHelper.DiscoverNearestPattern(request.WorkspacePath, fileEntry.FilePath, out var refFile);
            var contractSig = RoslynContractExtractor.ExtractPublicContracts(fileEntry.FilePath, candidateCode);
            if (!string.IsNullOrWhiteSpace(contractSig))
            {
                var provenanceHeader = $"// Provenance: Role={role}, PatternReference={refFile ?? "None"}, Status=ValidatedAgainstRepositoryPattern\n";
                lockedContracts[fileEntry.FilePath] = provenanceHeader + contractSig;
            }
        }

        fileGenerationStopwatch.Stop();
        runTelemetry.RecordFileDuration(fileEntry.FilePath, fileGenerationStopwatch.ElapsedMilliseconds);
        var durationSec = (int)Math.Max(1, Math.Round(fileGenerationStopwatch.Elapsed.TotalSeconds));
        await SafeRecordActivityAsync(
            request.ExecutionId,
            $"Generated edit {fileIndex}/{totalFiles} · {fileName} · {durationSec}s",
            cancellationToken,
            ExecutionActivityStatus.Completed,
            new ExecutionActivityMetadata(
                EventKind: "FileGeneration",
                TargetFile: fileEntry.FilePath,
                ProviderCallKind: "Generation",
                QueueWaitMs: queueWaitMs,
                DependencyWaitMs: dependencyWaitMs,
                TotalFileDurationMs: fileGenerationStopwatch.ElapsedMilliseconds,
                ConfiguredConcurrency: runTelemetry.ConfiguredConcurrency,
                ActiveGenerationCount: activeGenerationCount,
                PeakConcurrentGenerationCount: runTelemetry.PeakConcurrent,
                RetryOccurred: recoveryUsed,
                RequestedReasoningEffort: _mechanicalReasoningEffort)).ConfigureAwait(false);

        return editSpec;
    }

    private static async Task<(string Content, string Hash)> ReadCurrentTargetContentAsync(
        string workspacePath,
        string filePath,
        CancellationToken cancellationToken)
    {
        var resolvedPath = WorktreeEditApplier.ValidateAndResolvePath(workspacePath, filePath);
        if (!File.Exists(resolvedPath))
        {
            throw new InvalidOperationException($"Strict Modify action failed: target file does not exist at '{filePath}'.");
        }

        var bytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        if (WorktreeEditApplier.IsBinaryContent(bytes))
        {
            throw new InvalidOperationException($"Target file '{filePath}' is a binary file and cannot be modified.");
        }

        var content = WorktreeEditApplier.DecodeUtf8Text(bytes, out _);
        return (content, WorktreeEditApplier.ComputeContentHash(content));
    }

    private async Task SafeRecordActivityAsync(
        Guid executionId,
        string message,
        CancellationToken cancellationToken,
        ExecutionActivityStatus status = ExecutionActivityStatus.Started,
        ExecutionActivityMetadata? metadata = null)
    {
        if (executionId == Guid.Empty || _activityRecorder == null) return;
        try
        {
            await _activityRecorder.RecordActivityAsync(
                executionId,
                ExecutionStage.DeveloperAgent,
                status,
                message,
                metadata,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeveloperAgent: failed to record activity '{Message}' for execution {ExecutionId}.", message, executionId);
        }
    }

    private async Task RecordProviderCallActivityAsync(
        Guid executionId,
        string targetFile,
        string callKind,
        AiRequest request,
        AiResponse response,
        TimeSpan duration,
        CancellationToken cancellationToken,
        long? queueWaitMs = null,
        long? dependencyWaitMs = null,
        int? activeGenerationCount = null,
        bool? retryOccurred = null)
    {
        var promptChars = (request.SystemPrompt?.Length ?? 0) + (request.UserPrompt?.Length ?? 0);
        var metadata = new ExecutionActivityMetadata(
            EventKind: "ProviderCall",
            LogicalProviderCallCount: 1,
            ProviderCallKind: callKind,
            ProviderAttemptCount: response.AttemptCount,
            RequestedOutputTokens: request.MaxTokens,
            InputTokens: response.InputTokens,
            OutputTokens: response.OutputTokens,
            StageDurationMs: (long)duration.TotalMilliseconds,
            ProviderDurationMs: (long)duration.TotalMilliseconds,
            TargetFile: targetFile,
            FailureKind: response.FailureKind == AiFailureKind.None ? null : response.FailureKind.ToString(),
            ReasoningTokens: response.ReasoningTokens,
            ResponseContentCharCount: response.Content?.Length,
            RequestedReasoningEffort: request.ReasoningEffort,
            QueueWaitMs: queueWaitMs,
            DependencyWaitMs: dependencyWaitMs,
            ConfiguredConcurrency: _maxConcurrentFileGenerations,
            ActiveGenerationCount: activeGenerationCount,
            InputPromptCharCount: promptChars,
            RetryOccurred: retryOccurred);

        await SafeRecordActivityAsync(
            executionId,
            $"Provider call completed: {callKind}.",
            cancellationToken,
            response.IsSuccess ? ExecutionActivityStatus.Completed : ExecutionActivityStatus.Failed,
            metadata).ConfigureAwait(false);
    }

    private void LogGenerationAudit(
        string targetPath,
        string action,
        int callNumber,
        AiRequest request,
        AiResponse response,
        TimeSpan duration,
        bool isCompactRetry = false)
    {
        var inputTokensEstimate = Math.Max(1, ((request.SystemPrompt?.Length ?? 0) + (request.UserPrompt?.Length ?? 0)) / 4);
        var responseCharCount = response.Content?.Length ?? 0;
        var finishReason = response.FinishReason ?? (response.IsSuccess ? "stop" : (response.FailureKind == AiFailureKind.TokenLimitExceeded ? "length" : "error"));

        bool isCompleteJson = false;
        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            var trimmed = response.Content.Trim();
            isCompleteJson = (trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
                             (trimmed.StartsWith('[') && trimmed.EndsWith(']')) ||
                             (trimmed.StartsWith("```") && trimmed.EndsWith("```"));
        }

        var provider = !string.IsNullOrWhiteSpace(response.Provider) ? response.Provider : "UnknownProvider";
        var model = !string.IsNullOrWhiteSpace(response.Model) ? response.Model : request.Model;

        _logger.LogInformation(
            "DeveloperAgent Call #{CallNumber} [Target: {TargetPath}, Action: {Action}, AttemptType: {AttemptType}, Provider: {Provider}, Model: {Model}] - " +
            "Success: {IsSuccess}, FinishReason: {FinishReason}, MaxOutputTokens: {MaxOutputTokens}, InputTokens: {InputTokens}, " +
            "OutputTokens: {OutputTokens}, ResponseChars: {ResponseChars}, CompleteJson: {CompleteJson}, DurationMs: {DurationMs}",
            callNumber,
            targetPath,
            action,
            isCompactRetry ? "CompactRetry" : "Initial",
            provider,
            model,
            response.IsSuccess,
            finishReason,
            request.MaxTokens?.ToString() ?? "unspecified",
            response.InputTokens ?? inputTokensEstimate,
            response.OutputTokens.HasValue ? response.OutputTokens.Value.ToString() : "n/a",
            responseCharCount,
            isCompleteJson,
            (long)duration.TotalMilliseconds);
    }

    public static EditManifest BuildManifestFromImpactAnalysis(
        DeveloperAgentRequest request,
        string workspacePath,
        IReadOnlyList<string> projectRoots,
        int maxManifestFiles,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph = null)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        projectGraph ??= WorktreeEditApplier.DiscoverProjectGraph(workspacePath);

        // 1. Collect candidate items
        var candidates = new List<ImpactedFileDetail>();
        if (request.ImpactedFiles != null && request.ImpactedFiles.Count > 0)
        {
            candidates.AddRange(request.ImpactedFiles.Where(f => !string.IsNullOrWhiteSpace(f?.FilePath)));
        }
        else if (request.ImpactedFilePaths != null && request.ImpactedFilePaths.Count > 0)
        {
            candidates.AddRange(request.ImpactedFilePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => new ImpactedFileDetail(p, null, null)));
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("Approved TaskImpactAnalysis contains no impacted files to construct an edit manifest.");
        }

        if (candidates.Count > maxManifestFiles)
        {
            throw new InvalidOperationException($"Approved impact analysis contains {candidates.Count} impacted files, exceeding maximum allowed limit of {maxManifestFiles}.");
        }

        var entries = new List<ManifestFileEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in candidates)
        {
            var rawPath = item.FilePath.Trim();
            var normalizedPath = WorktreeEditApplier.NormalizeAndValidateRelativePath(rawPath);

            // C# source project root validation & safe correction for test files
            if (normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (!WorktreeEditApplier.IsCsFileInProjectRoot(normalizedPath, projectRoots))
                {
                    if (WorktreeEditApplier.IsTestFileCandidate(normalizedPath))
                    {
                        if (WorktreeEditApplier.TryRemapTestFileToSingleTestProject(normalizedPath, projectGraph, out var remappedPath, out var err))
                        {
                            normalizedPath = remappedPath;
                        }
                        else
                        {
                            throw new InvalidOperationException(err ?? $"Impacted C# test file '{normalizedPath}' is outside all discovered .NET project roots.");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Impacted C# file '{normalizedPath}' is outside all discovered .NET project roots.");
                    }
                }
            }

            if (!seenPaths.Add(normalizedPath))
            {
                throw new InvalidOperationException($"Duplicate file path in impact analysis: '{rawPath}'.");
            }

            // Action resolution
            FileEditAction action;
            if (!string.IsNullOrWhiteSpace(item.ChangeType) &&
                (item.ChangeType.Equals("Add", StringComparison.OrdinalIgnoreCase) ||
                 item.ChangeType.Equals("Create", StringComparison.OrdinalIgnoreCase)))
            {
                action = FileEditAction.Create;
            }
            else if (!string.IsNullOrWhiteSpace(item.ChangeType) &&
                     (item.ChangeType.Equals("Modify", StringComparison.OrdinalIgnoreCase) ||
                      item.ChangeType.Equals("Refactor", StringComparison.OrdinalIgnoreCase)))
            {
                action = FileEditAction.Modify;
                if (WorktreeEditApplier.TryResolveModifyTarget(normalizedPath, workspacePath, projectGraph, projectRoots, out var resolvedModifyPath, out var _))
                {
                    normalizedPath = resolvedModifyPath;
                }
            }
            else
            {
                // Inspect disk existence
                var fullPath = Path.Combine(workspacePath, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
                action = File.Exists(fullPath) ? FileEditAction.Modify : FileEditAction.Create;
            }

            var purpose = !string.IsNullOrWhiteSpace(item.Reason)
                ? item.Reason.Trim()
                : $"Implement changes for task '{request.TaskTitle}'";

            entries.Add(new ManifestFileEntry(
                FilePath: normalizedPath,
                Action: action,
                Purpose: purpose,
                Dependencies: null));
        }

        return new EditManifest(entries);
    }

    public static string BuildManifestSystemPrompt()
    {
        return """
            You are a software architect agent. Your task is to inspect the development task requirements and project structure to produce an EDIT MANIFEST listing all files that must be Created or Modified.

            CRITICAL RULES:
            1. Output ONLY a valid JSON object matching the JSON schema below. Do not include markdown code fence formatting outside the JSON object or any conversational text.
            2. Do NOT output file contents, search/replace blocks, or full code implementations in this step.
            3. Target file paths MUST be repository-relative using '/' (e.g. 'src/Project/File.cs').
            4. For C# source files (*.cs), files MUST be located inside an existing discovered .NET project directory.
            5. Do NOT include sensitive files (.env, .git, passwords, keys).
            6. Keep the file list minimal and strictly focused on fulfilling the task requirements and acceptance criteria.
            7. EXACT VALUES & LITERALS: When the task specifies exact literal strings, property values, status names, or field contents, you MUST output the exact literal values specified in the task requirements. Do NOT substitute required literal values with dynamic environment lookups.
            8. TEST PROJECT REFERENCE COMPLIANCE: Before creating or modifying unit test files in a test project, inspect the 'Discovered .NET Project Graph'. Verify that the test project already references the target project containing the types being tested.
               - If NO test project in the graph references the target project:
                 a. Do NOT create uncompilable unit test files in that test project.
                 b. Do NOT modify project files (.csproj) to add new ProjectReference tags merely to make a generated test compile.
                 c. If the development task does NOT explicitly mandate creating unit tests, omit the test file and implement only the production code.
                 d. If the task explicitly requires unit tests but the architecture lacks the necessary project reference, focus strictly on valid compilable code and omit uncompilable test files.

            JSON Schema:
            {
              "files": [
                {
                  "filePath": "relative/path/to/file.ext",
                  "action": "Create", // or "Modify"
                  "purpose": "Brief description of what this file contains or changes",
                  "dependencies": ["relative/path/to/dependency.ext"]
                }
              ]
            }
            """;
    }

    public static string BuildManifestRepairSystemPrompt()
    {
        return """
            You are a software architect agent repairing a malformed edit manifest JSON response.
            Output ONLY a valid JSON object matching the schema below.
            Do NOT include markdown fences, code content, or conversational text.

            JSON Schema:
            {
              "files": [
                {
                  "filePath": "relative/path/to/file.ext",
                  "action": "Create", // or "Modify"
                  "purpose": "Brief description of what this file contains or changes",
                  "dependencies": ["relative/path/to/dependency.ext"]
                }
              ]
            }
            """;
    }

    public static string BuildManifestRepairUserPrompt(string parseError, string previousResponse)
    {
        var boundedPrevious = previousResponse ?? string.Empty;
        if (boundedPrevious.Length > 2000)
        {
            boundedPrevious = boundedPrevious.Substring(0, 2000) + "\n...[truncated]";
        }

        return $"""
            The previous edit manifest could not be parsed or failed validation.

            Validation / Parse Error:
            {parseError}

            Previous Response:
            {boundedPrevious}

            Please output ONLY the corrected JSON object matching the required schema.
            """;
    }

    public static string BuildManifestUserPrompt(
        DeveloperAgentRequest request,
        IReadOnlyDictionary<string, string> contextFiles,
        IReadOnlyList<DiscoveredProjectNode> projectGraph)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Task Title: {request.TaskTitle}");
        sb.AppendLine($"Task Description: {request.TaskDescription}");
        if (!string.IsNullOrWhiteSpace(request.AcceptanceCriteria))
        {
            sb.AppendLine($"Acceptance Criteria: {request.AcceptanceCriteria}");
        }
        sb.AppendLine();
        sb.AppendLine("=== Discovered .NET Project Graph ===");
        if (projectGraph != null && projectGraph.Count > 0)
        {
            foreach (var proj in projectGraph)
            {
                sb.AppendLine($"- Project: {proj.ProjectPath} (Name: {proj.ProjectName}, Directory: {proj.ProjectDirectory}, TestProject: {proj.IsTestProject})");
                if (proj.ProjectReferences != null && proj.ProjectReferences.Count > 0)
                {
                    sb.AppendLine("  References:");
                    foreach (var r in proj.ProjectReferences)
                    {
                        sb.AppendLine($"    - {r}");
                    }
                }
            }
        }
        sb.AppendLine();
        sb.AppendLine("=== Impact Analysis Summary ===");
        sb.AppendLine(request.ImpactAnalysisSummary);
        sb.AppendLine();
        if (request.ChangeDimensions != null && request.ChangeDimensions.Count > 0)
        {
            sb.AppendLine("=== Change Dimensions ===");
            foreach (var dim in request.ChangeDimensions)
            {
                sb.AppendLine($"- {dim}");
            }
            sb.AppendLine();
        }
        if (request.ImpactedFiles != null && request.ImpactedFiles.Count > 0)
        {
            sb.AppendLine("=== Predicted Impacted Files ===");
            foreach (var f in request.ImpactedFiles)
            {
                var ev = !string.IsNullOrEmpty(f.EvidenceType) ? $", Evidence: {f.EvidenceType}" : "";
                var unc = f.IsUncertain ? " [Uncertain]" : "";
                sb.AppendLine($"- {f.FilePath} ({f.ChangeType ?? "Modify"}{ev}{unc})");
            }
            sb.AppendLine();
        }
        if (request.Unknowns != null && request.Unknowns.Count > 0)
        {
            sb.AppendLine("=== Unknowns / Boundaries ===");
            foreach (var u in request.Unknowns)
            {
                sb.AppendLine($"- {u}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("=== Proposed Plan ===");
        sb.AppendLine(request.ProposedPlan);
        sb.AppendLine();
        sb.AppendLine("=== Available Context Files ===");
        foreach (var path in contextFiles.Keys)
        {
            sb.AppendLine($"- {path}");
        }

        return sb.ToString();
    }

    public static string SanitizeFailureReason(string? validationError, EditApplicabilityResult? applicabilityFailure)
    {
        if (applicabilityFailure != null && !applicabilityFailure.Success)
        {
            if (applicabilityFailure.MatchCount == 0)
            {
                return "SEARCH anchor not found (0 matches)";
            }
            return $"SEARCH anchor ambiguous ({applicabilityFailure.MatchCount} matches)";
        }

        if (string.IsNullOrWhiteSpace(validationError))
        {
            return "Edit validation failed";
        }

        var firstLine = validationError.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Edit validation failed";
        if (firstLine.Length > 80)
        {
            firstLine = firstLine.Substring(0, 80) + "...";
        }
        return SanitizeForDiagnostics(firstLine);
    }

    public static string BuildBoundedTargetSourceWindow(
        string targetContent,
        EditApplicabilityResult? applicabilityFailure = null,
        bool isTestFile = false)
    {
        if (string.IsNullOrWhiteSpace(targetContent)) return targetContent;

        var lines = targetContent.Split('\n');

        // 1. If applicability failure context is present for a large file (> 100 lines / 4000 chars),
        // window specifically around the failure point to highlight the exact mismatch anchor.
        if (applicabilityFailure != null && !string.IsNullOrWhiteSpace(applicabilityFailure.SurroundingContext) && (lines.Length > 100 || targetContent.Length > 4000))
        {
            var sb = new System.Text.StringBuilder();
            int headerLinesCount = Math.Min(30, lines.Length);
            for (int i = 0; i < headerLinesCount; i++)
            {
                sb.AppendLine(lines[i]);
            }
            sb.AppendLine("\n// ... [prior methods omitted for brevity] ...\n");
            sb.AppendLine("// === Context Around Target Edit / Failure Point ===");
            sb.AppendLine(applicabilityFailure.SurroundingContext.Trim());
            sb.AppendLine("// === End Context Around Target Edit ===\n");

            int footerStart = Math.Max(headerLinesCount, lines.Length - 25);
            for (int i = footerStart; i < lines.Length; i++)
            {
                sb.AppendLine(lines[i]);
            }
            return sb.ToString();
        }

        // 2. For test files with repetitive suites (> 100 lines / 4000 chars),
        // window to fixture header, representative test convention, and class insertion anchor.
        if (isTestFile && (lines.Length > 100 || targetContent.Length > 4000))
        {
            var sb = new System.Text.StringBuilder();
            int headerCount = Math.Min(30, lines.Length);
            for (int i = 0; i < headerCount; i++)
            {
                sb.AppendLine(lines[i]);
            }

            int factIndex = -1;
            for (int i = headerCount; i < lines.Length - 30; i++)
            {
                if (lines[i].Contains("[Fact]") || lines[i].Contains("[Theory]"))
                {
                    factIndex = i;
                    break;
                }
            }

            if (factIndex > 0)
            {
                sb.AppendLine("\n// ... [prior existing test methods omitted for brevity] ...\n");
                sb.AppendLine("// === Representative Neighboring Test Method (Convention Example) ===");
                int sampleEnd = Math.Min(factIndex + 25, lines.Length - 30);
                for (int i = factIndex; i < sampleEnd; i++)
                {
                    sb.AppendLine(lines[i]);
                }
                sb.AppendLine("// === End Representative Neighboring Test Method ===\n");
            }

            sb.AppendLine("\n// ... [subsequent existing methods omitted for brevity] ...\n");
            sb.AppendLine("// === Insertion Anchor (End of Class) ===");
            int footStart = Math.Max(headerCount, lines.Length - 25);
            for (int i = footStart; i < lines.Length; i++)
            {
                sb.AppendLine(lines[i]);
            }

            return sb.ToString();
        }

        // 3. For production source files up to 600 lines / 25,000 chars,
        // provide complete content so the model can inspect all methods and produce exact verbatim search anchors.
        if (lines.Length <= 600 && targetContent.Length <= 25000)
        {
            return targetContent;
        }

        // 4. Extremely large files (> 600 lines / 25,000 chars)
        var sbLarge = new System.Text.StringBuilder();
        int hCount = Math.Min(30, lines.Length);
        for (int i = 0; i < hCount; i++)
        {
            sbLarge.AppendLine(lines[i]);
        }
        sbLarge.AppendLine("\n// ... [subsequent existing methods omitted for brevity] ...\n");
        sbLarge.AppendLine("// === Insertion Anchor (End of Class) ===");
        int fStart = Math.Max(hCount, lines.Length - 25);
        for (int i = fStart; i < lines.Length; i++)
        {
            sbLarge.AppendLine(lines[i]);
        }

        return sbLarge.ToString();
    }

    public static string BuildFocusedRepairTargetWindow(
        string targetContent,
        IReadOnlyList<string>? diagnosticLocations,
        string? diagnosticEvidence = null,
        int contextRadius = 12,
        int smallFileMaxLines = 80,
        int smallFileMaxChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(targetContent))
        {
            return targetContent;
        }

        var lines = targetContent.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= smallFileMaxLines && targetContent.Length <= smallFileMaxChars)
        {
            return targetContent;
        }

        var lineNumbers = new SortedSet<int>();
        foreach (var location in diagnosticLocations ?? Array.Empty<string>())
        {
            TryAddDiagnosticLineNumber(location, lineNumbers);
        }

        if (!string.IsNullOrWhiteSpace(diagnosticEvidence))
        {
            foreach (Match match in Regex.Matches(diagnosticEvidence, @"\((\d+),\d+\)"))
            {
                if (int.TryParse(match.Groups[1].Value, out var line) && line > 0)
                {
                    lineNumbers.Add(line);
                }
            }
        }

        if (lineNumbers.Count == 0)
        {
            return BuildBoundedTargetSourceWindow(targetContent);
        }

        var included = new bool[lines.Length];
        foreach (var lineNumber in lineNumbers)
        {
            var index = Math.Clamp(lineNumber - 1, 0, lines.Length - 1);
            var radius = Math.Max(2, contextRadius);
            var start = Math.Max(0, index - radius);
            var end = Math.Min(lines.Length - 1, index + radius);
            for (var i = start; i <= end; i++)
            {
                included[i] = true;
            }
        }

        var sb = new System.Text.StringBuilder();
        var emittingGap = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!included[i])
            {
                emittingGap = true;
                continue;
            }

            if (emittingGap)
            {
                sb.AppendLine("// ... [unrelated methods omitted] ...");
                emittingGap = false;
            }

            sb.AppendLine(lines[i]);
        }

        return sb.ToString();
    }

    private static void TryAddDiagnosticLineNumber(string location, ISet<int> lineNumbers)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        var match = Regex.Match(location, @"\((\d+),\d+\)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var line) && line > 0)
        {
            lineNumbers.Add(line);
        }
    }

    public static string BuildSingleFileSystemPrompt(
        ManifestFileEntry fileEntry,
        bool useFullFileReplacement = false)
    {
        var isTest = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);

        var actionSpecificRule = fileEntry.Action == FileEditAction.Create
            ? "For 'Create' actions, specify 'newContent' containing the smallest compile-complete source file. No prose, no duplicated explanations, no unnecessary comments, and no speculative extra functionality."
            : useFullFileReplacement
                ? "This is a small-file Modify. Return the complete resulting file once in 'newContent'; omit 'searchReplaceEdits'."
                : isTest
                    ? "This is an existing test-file Modify. Return ONLY compact 'searchReplaceEdits' inserting or updating specific test methods. DO NOT reproduce unchanged test methods or the entire test class. Use a short 2-5 line exact anchor that is unique in the target file (such as the attribute and signature of a neighboring test, or the tail of the preceding test plus its closing brace and surrounding lines; never use a bare closing brace alone) and include only the new/modified test code. Omit 'newContent'."
                    : "This is an existing-file Modify. Return only compact 'searchReplaceEdits'; each small exact search anchor must match once. Do not reproduce unchanged file content. Omit 'newContent'.";

        var testGuidance = isTest
            ? fileEntry.Action == FileEditAction.Modify
                ? "\n6. TEST MODIFY DISCIPLINE: Implement only the focused test methods required by the acceptance criteria and follow existing test conventions. Reuse existing test setup, fixtures, and helpers from the target file. DO NOT recreate or duplicate existing fixtures, fields, or unchanged tests."
                : "\n6. MINIMAL TESTS: Implement only the focused tests required by the acceptance criteria and follow existing test conventions."
            : "";

        var schema = fileEntry.Action == FileEditAction.Create || useFullFileReplacement
            ? $$"""{"filePath":"{{fileEntry.FilePath}}","action":"{{fileEntry.Action}}","newContent":"complete resulting file"}"""
            : $$"""{"filePath":"{{fileEntry.FilePath}}","action":"Modify","searchReplaceEdits":[{"search":"small exact unique anchor","replace":"replacement"}]}""";

        return $$"""
            You are a software developer agent implementing exact edits for a single target file: '{{fileEntry.FilePath}}' (Action: {{fileEntry.Action}}).

            CRITICAL RULES:
            1. Respond ONLY with the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            2. Do NOT attempt file deletion or rename.
            3. Do NOT execute arbitrary shell commands.
            4. {{actionSpecificRule}}
            5. EXACT VALUES & LITERALS: When the task specifies exact literal strings, property values, status names, or field contents, you MUST output the exact literal values specified in the task requirements.{{testGuidance}}

            Required JSON shape:
            {{schema}}
            """;
    }

    public static string BuildCompactSingleFileSystemPrompt(
        ManifestFileEntry fileEntry,
        bool useFullFileReplacement = false)
    {
        var isTest = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);

        var actionSpecificRule = fileEntry.Action == FileEditAction.Create
            ? "Provide the smallest compile-complete file in 'newContent'. No prose, comments, or extra features."
            : useFullFileReplacement
                ? "Provide the complete resulting small file once in 'newContent'; omit 'searchReplaceEdits'."
                : isTest
                    ? "Output was previously truncated because too much code was emitted. For this test file, return ONLY minimal 2-5 line 'searchReplaceEdits' inserting the new test method(s) using a unique 2-5 line anchor (never a bare closing brace alone). NEVER repeat existing tests or the test class. Omit 'newContent'."
                    : "Output was previously truncated because too much code was emitted. Return ONLY minimal 2-5 line 'searchReplaceEdits' targeting specific modified statements. NEVER repeat unchanged methods or the rest of the file. Omit 'newContent'.";

        var schema = fileEntry.Action == FileEditAction.Create || useFullFileReplacement
            ? $$"""{"filePath":"{{fileEntry.FilePath}}","action":"{{fileEntry.Action}}","newContent":"complete resulting file"}"""
            : $$"""{"filePath":"{{fileEntry.FilePath}}","action":"Modify","searchReplaceEdits":[{"search":"exact anchor","replace":"replacement"}]}""";

        return $$"""
            Output ONLY the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            {{actionSpecificRule}}
            {{schema}}
            """;
    }

    public static string BuildSurgicalModifyRetrySystemPrompt(ManifestFileEntry fileEntry)
    {
        return $$"""
            Output ONLY the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            The previous existing-file Modify response exhausted the output budget.
            Return ONLY 1-3 compact 'searchReplaceEdits' with exact 2-5 line unique anchors from the current target.
            Change only the required statements. NEVER reproduce unchanged methods or the entire file.
            Omit 'newContent'.
            {"filePath":"{{fileEntry.FilePath}}","action":"Modify","searchReplaceEdits":[{"search":"exact 2-5 line unique anchor","replace":"replacement"}]}
            """;
    }

    public static string BuildMinimalCreateRetrySystemPrompt(ManifestFileEntry fileEntry)
    {
        return $$"""
            Output ONLY the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            The previous Create response was too verbose and exhausted the output budget.
            Emit the smallest compile-complete source file in 'newContent'. No comments, no explanations, no unused helpers, no speculative extra functionality.
            The file must be syntactically complete and compile on its own.
            {"filePath":"{{fileEntry.FilePath}}","action":"Create","newContent":"smallest compile-complete source"}
            """;
    }

    public static string BuildMicroModifyRetrySystemPrompt(ManifestFileEntry fileEntry)
    {
        return $$"""
            Output ONLY the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            Previous Modify attempts exhausted the output budget. This is a MICRO repair of one required change.
            Return ONE compact 'searchReplaceEdit' with an exact 2-5 line unique anchor from the supplied target window.
            Change only the exact required statement. NEVER reproduce unchanged methods or the entire file.
            Omit 'newContent'.
            {"filePath":"{{fileEntry.FilePath}}","action":"Modify","searchReplaceEdits":[{"search":"exact 2-5 line unique anchor","replace":"replacement"}]}
            """;
    }

    public static string BuildSingleFileRepairSystemPrompt(
        ManifestFileEntry fileEntry,
        bool useFullFileReplacement = false)
    {
        var isTest = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);

        var actionSpecificRule = fileEntry.Action == FileEditAction.Create
            ? "For 'Create' actions, specify 'newContent' containing the smallest compile-complete source file. No prose or extra features."
            : useFullFileReplacement
                ? "Return the complete resulting small file in 'newContent'; omit 'searchReplaceEdits'."
                : isTest
                    ? "Return only compact 'searchReplaceEdits'. Copy each small search anchor (2-5 lines) verbatim from the current target and make it match once; omit 'newContent'. For test files, insert or update ONLY the specific test methods using a unique 2-5 line anchor (never a bare closing brace alone) and do NOT reproduce unchanged tests or the test class."
                    : "Return only compact 'searchReplaceEdits'. Copy each small search anchor (2-5 lines) verbatim from the current target and make it match once; omit 'newContent'.";

        var schema = fileEntry.Action == FileEditAction.Create || useFullFileReplacement
            ? $$"""{"filePath":"{{fileEntry.FilePath}}","action":"{{fileEntry.Action}}","newContent":"complete resulting file"}"""
            : $$"""{"filePath":"{{fileEntry.FilePath}}","action":"Modify","searchReplaceEdits":[{"search":"exact anchor","replace":"replacement"}]}""";

        return $$"""
            You are a software developer agent repairing a malformed or invalid single-file edit response for '{{fileEntry.FilePath}}'.
            Return ONLY the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            {{actionSpecificRule}}
            {{schema}}
            """;
    }

    public static string BuildSingleFileRepairUserPrompt(
        string parseError,
        string previousResponse,
        ManifestFileEntry fileEntry,
        string? currentTargetContent,
        IReadOnlyDictionary<string, string>? relevantGeneratedDependencies = null,
        IReadOnlyDictionary<string, string>? lockedContracts = null,
        EditApplicabilityResult? applicabilityFailure = null,
        bool useFullFileReplacement = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Target File: {fileEntry.FilePath}");
        sb.AppendLine($"Action: {fileEntry.Action}");
        if (!string.IsNullOrWhiteSpace(fileEntry.Purpose))
        {
            sb.AppendLine($"Purpose: {fileEntry.Purpose}");
        }
        sb.AppendLine();
        sb.AppendLine("=== Validation / Applicability Failure ===");
        sb.AppendLine(parseError);
        sb.AppendLine();

        if (parseError.Contains("Unresolved symbol") || parseError.Contains("missing in this file") || parseError.Contains("does not exist in the repository"))
        {
            sb.AppendLine("=== ARCHITECTURAL DEPENDENCY REPAIR GUIDANCE ===");
            sb.AppendLine("1. Queries, Commands, and DTOs: Data contracts (records/classes in Features/*/Queries or Commands) must ONLY contain query parameters and filter data. They must NOT inject or reference service dependencies (e.g. IMapper, IProductRepository, DbContext). Remove any unwanted service parameters.");
            sb.AppendLine("2. Handlers: Service dependencies (e.g. IMapper, IProductRepository) belong in the Handler constructor (e.g. ListProductsQueryHandler), not the Query contract.");
            sb.AppendLine("3. If an unresolved symbol was added unnecessarily to a Query or Command, REMOVE it to follow existing repository patterns.");
            sb.AppendLine("4. If a referenced package symbol is legitimately required in a Handler, ensure the proper using directive (e.g. 'using AutoMapper;') is included at the top of the file.");
            sb.AppendLine();
        }

        if (applicabilityFailure != null && !applicabilityFailure.Success)
        {
            sb.AppendLine("=== Applicability Failure Evidence ===");
            sb.AppendLine($"- Failed Edit Block: {applicabilityFailure.FailedEditIndex} of {applicabilityFailure.TotalEdits}");
            var reasonDesc = applicabilityFailure.MatchCount == 0
                ? "zero matches (the search text was not found in the current target file)"
                : $"multiple matches ({applicabilityFailure.MatchCount} occurrences found, must match exactly once)";
            sb.AppendLine($"- Failure Reason: {reasonDesc}");
            if (!string.IsNullOrEmpty(applicabilityFailure.FailedSearch))
            {
                sb.AppendLine("- Exact Failed SEARCH Text:");
                sb.AppendLine(applicabilityFailure.FailedSearch);
            }
            if (!string.IsNullOrEmpty(applicabilityFailure.SurroundingContext))
            {
                sb.AppendLine("- Surrounding Target Source Context:");
                sb.AppendLine(applicabilityFailure.SurroundingContext);
            }
            sb.AppendLine();
        }

        if (fileEntry.Action == FileEditAction.Modify)
        {
            sb.AppendLine(useFullFileReplacement
                ? "Edit Strategy: hash-guarded small-file replacement. Return complete resulting content once in newContent."
                : "Edit Strategy: surgical patch. Use the smallest verbatim search anchors that each match once.");

            if (!string.IsNullOrWhiteSpace(currentTargetContent))
            {
                var isTest = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);
                var boundedContent = BuildBoundedTargetSourceWindow(currentTargetContent, applicabilityFailure, isTest);
                sb.AppendLine("=== Current Content of Target File ===");
                sb.AppendLine(boundedContent);
                sb.AppendLine("=== End Current Content ===");
                sb.AppendLine();
            }
        }

        if (lockedContracts != null && lockedContracts.Count > 0)
        {
            sb.AppendLine("=== Authoritative Upstream Contracts (LOCKED) ===");
            foreach (var (contractPath, contractSig) in lockedContracts)
            {
                sb.AppendLine($"--- Locked Contract: {contractPath} ---");
                sb.AppendLine(contractSig);
                sb.AppendLine("--- End Locked Contract ---");
            }
            sb.AppendLine();
        }

        if (relevantGeneratedDependencies != null && relevantGeneratedDependencies.Count > 0)
        {
            sb.AppendLine("=== In-Memory Generated Dependency Snippets ===");
            foreach (var (depPath, depContent) in relevantGeneratedDependencies)
            {
                sb.AppendLine($"--- Generated Dependency: {depPath} ---");
                sb.AppendLine(depContent);
                sb.AppendLine("--- End Generated Dependency ---");
            }
            sb.AppendLine();
        }

        var boundedPrevious = previousResponse ?? string.Empty;
        if (boundedPrevious.Length > 1500)
        {
            boundedPrevious = boundedPrevious.Substring(0, 1500) + "\n...[truncated]";
        }

        sb.AppendLine("=== Invalid Previous Edit Response ===");
        sb.AppendLine(boundedPrevious);
        sb.AppendLine("=== End Invalid Previous Edit Response ===");
        sb.AppendLine();
        sb.AppendLine(useFullFileReplacement
            ? $"Output ONLY the corrected small-file replacement JSON for '{fileEntry.FilePath}'."
            : $"Output ONLY the corrected surgical edit JSON for '{fileEntry.FilePath}'.");

        return sb.ToString();
    }

    public static string BuildSingleFileRepairUserPrompt(string parseError, string previousResponse, ManifestFileEntry fileEntry) =>
        BuildSingleFileRepairUserPrompt(parseError, previousResponse, fileEntry, currentTargetContent: null, relevantGeneratedDependencies: null, lockedContracts: null, applicabilityFailure: null);

    public static string BuildFocusedDiagnosticRepairSystemPrompt(string filePath, bool isFinalDiagnosticAttempt = false)
    {
        var mission = isFinalDiagnosticAttempt
            ? $"You are performing a FINAL diagnostic-directed repair for a single existing file: '{filePath}'. Use only the exact current compiler diagnostic, the exact file, the diagnostic line/window, and the supplied local contract evidence. Do not treat this as a generic fix-the-task request."
            : $"You are performing lightweight focused verification repair for a single existing file: '{filePath}'.";

        return $$"""
            {{mission}}

            CRITICAL RULES:
            1. Respond ONLY with the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            2. This is an existing-file Modify. NEVER return 'newContent' or reproduce the entire file.
            3. Return ONLY compact 'searchReplaceEdits'. Prefer 1 edit; emit a few only when the same diagnostic requires them.
            4. Each search anchor must be an exact existing 2-5 line unique excerpt from the current target. Never use a bare closing brace alone as the anchor.
            5. Change only the diagnostic line or the immediately related method. Do not reproduce unrelated methods or code not implicated by the supplied diagnostics.
            6. Preserve all code not implicated by diagnostics.

            Required JSON shape:
            {"filePath":"{{filePath}}","action":"Modify","searchReplaceEdits":[{"search":"exact 2-5 line unique anchor","replace":"replacement"}]}
            """;
    }

    public static string BuildFocusedDiagnosticRepairUserPrompt(
        string filePath,
        string currentContent,
        FocusedRepairRequest request,
        IReadOnlyDictionary<string, string>? peerContext = null)
    {
        var sb = new System.Text.StringBuilder();
        if (request.IsFinalDiagnosticAttempt)
        {
            sb.AppendLine("FINAL DIAGNOSTIC-DIRECTED ATTEMPT");
            sb.AppendLine("Use only the exact current compiler diagnostic, the exact file, the diagnostic line/window, and the supplied local contract evidence.");
            sb.AppendLine("Do not treat this as a generic fix-the-task request.");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"Task Title: {request.TaskTitle}");
            if (!string.IsNullOrWhiteSpace(request.AcceptanceCriteria))
            {
                sb.AppendLine($"Acceptance Criteria: {request.AcceptanceCriteria}");
            }
            sb.AppendLine();
        }
        sb.AppendLine($"Target File: {filePath}");
        sb.AppendLine("Action: Modify");
        sb.AppendLine();
        sb.AppendLine("=== Authoritative Verification Diagnostic Evidence ===");
        sb.AppendLine(request.DiagnosticEvidence);
        sb.AppendLine("=== End Verification Diagnostic Evidence ===");
        sb.AppendLine();

        if (request.DiagnosticLocations != null && request.DiagnosticLocations.Count > 0)
        {
            sb.AppendLine("=== Diagnostic Failure Locations ===");
            foreach (var loc in request.DiagnosticLocations)
            {
                sb.AppendLine($"- {loc}");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(request.LanguageContext))
        {
            sb.AppendLine("=== Language / Architectural Context ===");
            sb.AppendLine(request.LanguageContext);
            sb.AppendLine("=== End Context ===");
            sb.AppendLine();
        }

        if (peerContext != null && peerContext.Count > 0)
        {
            sb.AppendLine("=== Authoritative Peer Contract Context (execution-local generated files) ===");
            sb.AppendLine("Use these current generated signatures/method names as the contract to preserve. Do not rewrite these files in this call.");
            foreach (var (peerPath, peerContent) in peerContext)
            {
                sb.AppendLine($"--- Peer File: {peerPath} ---");
                sb.AppendLine(peerContent);
                sb.AppendLine("--- End Peer File ---");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Edit Strategy: surgical SEARCH/REPLACE only. Use an exact existing unique 2-5 line anchor. Never use a bare closing brace alone. Never return full newContent.");
        sb.AppendLine();
        sb.AppendLine("=== Current Content of Target File ===");
        sb.AppendLine(currentContent);
        sb.AppendLine("=== End Current Content ===");
        sb.AppendLine();
        sb.AppendLine($"Output ONLY the corrected surgical searchReplaceEdits JSON for '{filePath}'. Do not reproduce the entire file or unrelated methods.");

        return sb.ToString();
    }

    public static string BuildAnchorRefreshRepairSystemPrompt(string filePath)
    {
        return $$"""
            You are refreshing a SEARCH/REPLACE repair for a single existing file: '{{filePath}}'.
            The previous SEARCH no longer matches the CURRENT file text.

            CRITICAL RULES:
            1. Respond ONLY with the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            2. Use ONLY the freshly supplied current file text. Do not reuse a stale search anchor.
            3. Return ONLY compact 'searchReplaceEdits'. Prefer 1 edit.
            4. Each search anchor must be an exact existing 2-5 line unique excerpt from the current target. Never use a bare closing brace alone.
            5. Do not return 'newContent' or replace the entire file.
            6. Change only the exact current compiler diagnostic line or the immediately related statement.

            Required JSON shape:
            {"filePath":"{{filePath}}","action":"Modify","searchReplaceEdits":[{"search":"exact current unique anchor","replace":"replacement"}]}
            """;
    }

    public static string BuildAnchorRefreshRepairUserPrompt(
        string filePath,
        string currentContent,
        FocusedRepairRequest request)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ANCHOR REFRESH REPAIR");
        sb.AppendLine("The previous SEARCH/REPLACE was structurally valid but SEARCH no longer matches the current file.");
        sb.AppendLine("Reread the current text below and emit an exact SEARCH/REPLACE against this current text only.");
        sb.AppendLine("Do not guess anchors. Do not replace the entire file.");
        sb.AppendLine();
        sb.AppendLine($"Target File: {filePath}");
        sb.AppendLine("Action: Modify");
        sb.AppendLine();
        sb.AppendLine("=== Exact Current Compiler Diagnostic ===");
        sb.AppendLine(request.DiagnosticEvidence);
        sb.AppendLine("=== End Compiler Diagnostic ===");
        sb.AppendLine();
        if (request.DiagnosticLocations != null && request.DiagnosticLocations.Count > 0)
        {
            sb.AppendLine("=== Diagnostic Failure Locations ===");
            foreach (var loc in request.DiagnosticLocations)
            {
                sb.AppendLine($"- {loc}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("=== Fresh Current Content of Target File ===");
        sb.AppendLine(currentContent);
        sb.AppendLine("=== End Current Content ===");
        sb.AppendLine();
        sb.AppendLine($"Output ONLY exact searchReplaceEdits JSON for '{filePath}' against the current text.");
        return sb.ToString();
    }

    public static string BuildMicroDiagnosticRepairSystemPrompt(string filePath)
    {
        return $$"""
            You are performing a MICRO diagnostic-directed repair for a single existing file: '{{filePath}}'.
            The previous focused diagnostic repair exhausted its output budget.

            CRITICAL RULES:
            1. Respond ONLY with the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            2. Return ONLY one compact 'searchReplaceEdits' block. Omit 'newContent'.
            3. Use only the exact compiler diagnostic and the tightly bounded current window.
            4. Each search anchor must be an exact existing 2-5 line unique excerpt. Never use a bare closing brace alone.
            5. Change only the diagnostic statement. Do not add peer files, comments, or extra features.

            Required JSON shape:
            {"filePath":"{{filePath}}","action":"Modify","searchReplaceEdits":[{"search":"exact 2-5 line unique anchor","replace":"replacement"}]}
            """;
    }

    public static string BuildMicroDiagnosticRepairUserPrompt(
        string filePath,
        string currentContent,
        FocusedRepairRequest request)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("MICRO DIAGNOSTIC REPAIR");
        sb.AppendLine("The previous focused diagnostic repair hit TokenLimitExceeded.");
        sb.AppendLine("Emit one exact SEARCH/REPLACE for the current diagnostic only.");
        sb.AppendLine("Do not include peer files or broad repository context.");
        sb.AppendLine();
        sb.AppendLine($"Target File: {filePath}");
        sb.AppendLine("Action: Modify");
        sb.AppendLine();
        sb.AppendLine("=== Exact Current Compiler Diagnostic ===");
        sb.AppendLine(request.DiagnosticEvidence);
        sb.AppendLine("=== End Compiler Diagnostic ===");
        sb.AppendLine();
        if (request.DiagnosticLocations != null && request.DiagnosticLocations.Count > 0)
        {
            sb.AppendLine("=== Diagnostic Failure Locations ===");
            foreach (var loc in request.DiagnosticLocations)
            {
                sb.AppendLine($"- {loc}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("=== Tightly Bounded Current Source Window ===");
        sb.AppendLine(currentContent);
        sb.AppendLine("=== End Current Source Window ===");
        sb.AppendLine();
        sb.AppendLine($"Output ONLY one searchReplaceEdits JSON object for '{filePath}'.");
        return sb.ToString();
    }

    public static Dictionary<string, string> CollectFocusedRepairPeerContext(
        string workspacePath,
        string targetFilePath,
        FocusedRepairRequest request,
        string currentContent,
        int maxPeerFiles = 4,
        int maxCharsPerFile = 2500)
    {
        var peers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return peers;
        }

        var candidates = new List<string>();
        if (request.RepairFiles != null)
        {
            candidates.AddRange(request.RepairFiles);
        }

        if (request.DiagnosticLocations != null)
        {
            foreach (var location in request.DiagnosticLocations)
            {
                var path = ExtractPathFromDiagnosticLocation(location);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    candidates.Add(path);
                }
            }
        }

        candidates.AddRange(ExtractReferencedSourcePaths(currentContent, targetFilePath));
        candidates.AddRange(ExtractReferencedSourcePaths(request.DiagnosticEvidence ?? string.Empty, targetFilePath));

        foreach (var candidate in candidates)
        {
            if (peers.Count >= maxPeerFiles)
            {
                break;
            }

            TryAddFocusedRepairPeer(
                peers,
                workspacePath,
                targetFilePath,
                candidate,
                maxCharsPerFile);
        }

        return peers;
    }

    public static void ValidateFocusedRepairSearchAnchors(FileEditSpec spec, string filePath)
    {
        if (spec.NewContent != null)
        {
            throw new FormatException($"Focused repair for '{filePath}' must not return full-file newContent.");
        }

        if (spec.SearchReplaceEdits == null || spec.SearchReplaceEdits.Count == 0)
        {
            throw new FormatException($"Focused repair for '{filePath}' requires compact searchReplaceEdits.");
        }

        if (spec.SearchReplaceEdits.Count > 6)
        {
            throw new FormatException($"Focused repair for '{filePath}' emitted too many edit blocks ({spec.SearchReplaceEdits.Count}); keep edits to the diagnostic lines.");
        }

        foreach (var edit in spec.SearchReplaceEdits)
        {
            if (IsBareClosingBraceAnchor(edit.Search))
            {
                throw new FormatException($"Focused repair for '{filePath}' must not use a bare closing brace as the search anchor.");
            }
        }
    }

    public static bool IsBareClosingBraceAnchor(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return false;
        }

        var trimmed = search.Trim();
        return trimmed is "}" or "};" or "}," or "})" or "});" or "};," ||
               Regex.IsMatch(trimmed, @"^\}[;,)\}]*\s*$");
    }

    public static string NormalizeFocusedRepairPath(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
    }

    public static string? ExtractPathFromDiagnosticLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var trimmed = location.Trim().Trim('"', '\'');
        var match = Regex.Match(
            trimmed,
            @"((?:[A-Za-z]:)?[^:(]+?\.(?:ts|tsx|js|jsx|mjs|cjs|cs))\s*(?:[:\(]\d)",
            RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return NormalizeFocusedRepairPath(match.Groups[1].Value);
        }

        match = Regex.Match(
            trimmed,
            @"((?:src|lib|app|tests|test)/[A-Za-z0-9_./\\-]+\.(?:ts|tsx|js|jsx|mjs|cjs|cs))\b",
            RegexOptions.IgnoreCase);
        return match.Success ? NormalizeFocusedRepairPath(match.Groups[1].Value) : null;
    }

    public static IReadOnlyList<string> ExtractReferencedSourcePaths(string content, string targetFilePath)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return results;
        }

        var targetDir = NormalizeFocusedRepairPath(Path.GetDirectoryName(targetFilePath) ?? string.Empty);
        foreach (Match match in Regex.Matches(
                     content,
                     @"(?:from|require\s*\()\s*['""](\.\.?/[^'""]+)['""]"))
        {
            var combined = CombineWorkspaceRelativePath(targetDir, match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(combined))
            {
                results.Add(combined);
            }
        }

        foreach (Match match in Regex.Matches(
                     content,
                     @"(?<![\w./\\])((?:src|lib|app|tests|test)/[A-Za-z0-9_./\\-]+\.(?:ts|tsx|js|jsx|mjs|cjs|cs))\b",
                     RegexOptions.IgnoreCase))
        {
            results.Add(NormalizeFocusedRepairPath(match.Groups[1].Value));
        }

        return results;
    }

    public static string BoundPeerContractExcerpt(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content) || maxChars <= 0)
        {
            return string.Empty;
        }

        var normalized = content.Replace("\r\n", "\n");
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        var selected = new List<string>();
        var used = 0;
        foreach (var line in normalized.Split('\n'))
        {
            if (!LooksLikePeerContractLine(line))
            {
                continue;
            }

            var addition = selected.Count == 0 ? line : "\n" + line;
            if (used + addition.Length > maxChars)
            {
                break;
            }

            selected.Add(line);
            used += addition.Length;
        }

        if (selected.Count > 0)
        {
            return string.Join('\n', selected);
        }

        return normalized[..maxChars];
    }

    private static void TryAddFocusedRepairPeer(
        Dictionary<string, string> peers,
        string workspacePath,
        string targetFilePath,
        string candidate,
        int maxCharsPerFile)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var normalizedTarget = NormalizeFocusedRepairPath(targetFilePath);
        foreach (var expanded in ExpandPeerPathCandidates(candidate))
        {
            if (string.IsNullOrWhiteSpace(expanded) ||
                expanded.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                IsUnsafeFocusedRepairPeerPath(expanded) ||
                peers.ContainsKey(expanded))
            {
                continue;
            }

            string resolved;
            try
            {
                resolved = WorktreeEditApplier.ValidateAndResolvePath(workspacePath, expanded);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(resolved))
            {
                continue;
            }

            try
            {
                var bytes = File.ReadAllBytes(resolved);
                var content = WorktreeEditApplier.DecodeUtf8Text(bytes, out _);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                peers[expanded] = BoundPeerContractExcerpt(content, maxCharsPerFile);
                return;
            }
            catch
            {
                // Skip unreadable peers; the target file remains the only required context.
            }
        }
    }

    private static IEnumerable<string> ExpandPeerPathCandidates(string candidate)
    {
        var normalized = NormalizeFocusedRepairPath(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        yield return normalized;
        if (Path.HasExtension(normalized))
        {
            yield break;
        }

        foreach (var extension in new[] { ".ts", ".tsx", ".js", ".jsx", ".cs" })
        {
            yield return normalized + extension;
        }
    }

    private static bool IsUnsafeFocusedRepairPeerPath(string path)
    {
        var normalized = NormalizeFocusedRepairPath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
                segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var extension = Path.GetExtension(normalized);
        return !string.IsNullOrEmpty(extension) &&
               extension is not (".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".cs");
    }

    private static string CombineWorkspaceRelativePath(string directory, string relativeSpec)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(directory))
        {
            parts.AddRange(NormalizeFocusedRepairPath(directory).Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in NormalizeFocusedRepairPath(relativeSpec).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(segment);
        }

        return string.Join('/', parts);
    }

    private static bool LooksLikePeerContractLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("export ", StringComparison.Ordinal) ||
               trimmed.StartsWith("import ", StringComparison.Ordinal) ||
               trimmed.StartsWith("public ", StringComparison.Ordinal) ||
               trimmed.StartsWith("internal ", StringComparison.Ordinal) ||
               trimmed.StartsWith("interface ", StringComparison.Ordinal) ||
               trimmed.StartsWith("type ", StringComparison.Ordinal) ||
               trimmed.StartsWith("class ", StringComparison.Ordinal) ||
               trimmed.StartsWith("router.", StringComparison.Ordinal) ||
               trimmed.Contains(" function ", StringComparison.Ordinal) ||
               Regex.IsMatch(trimmed, @"^(?:async\s+)?[A-Za-z_][A-Za-z0-9_]*\s*\(");
    }

    public static string BuildSingleFileUserPrompt(
        DeveloperAgentRequest request,
        ManifestFileEntry fileEntry,
        IReadOnlyDictionary<string, string> contextFiles,
        IReadOnlyDictionary<string, FileEditSpec>? completedEdits,
        IReadOnlyList<DiscoveredProjectNode> projectGraph,
        IReadOnlyDictionary<string, string>? lockedContracts = null,
        string? referencePattern = null,
        bool useFullFileReplacement = false,
        IReadOnlyDictionary<string, string>? virtualWorkspace = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Task Title: {request.TaskTitle}");
        sb.AppendLine($"Task Description: {request.TaskDescription}");
        if (!string.IsNullOrWhiteSpace(request.AcceptanceCriteria))
        {
            sb.AppendLine($"Acceptance Criteria: {request.AcceptanceCriteria}");
        }
        sb.AppendLine();
        sb.AppendLine($"Target File: {fileEntry.FilePath}");
        sb.AppendLine($"Action: {fileEntry.Action}");
        if (!string.IsNullOrWhiteSpace(fileEntry.Purpose))
        {
            sb.AppendLine($"Purpose: {fileEntry.Purpose}");
        }
        sb.AppendLine();

        // Minimal Target Project Context (find which project this file belongs to)
        var targetProject = projectGraph?.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.ProjectDirectory) &&
            fileEntry.FilePath.Replace('\\', '/').StartsWith(p.ProjectDirectory.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase));

        if (targetProject != null)
        {
            sb.AppendLine($"Target Project: {targetProject.ProjectName}");
            if (targetProject.ProjectReferences != null && targetProject.ProjectReferences.Count > 0)
            {
                sb.AppendLine("Project References:");
                foreach (var pref in targetProject.ProjectReferences)
                {
                    sb.AppendLine($"- {pref}");
                }
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(referencePattern))
        {
            // Inject reference pattern ONLY for Create / new-file targets or empty files.
            // Existing non-empty Modify targets already contain their repository conventions.
            bool isCreateOrEmpty = fileEntry.Action == FileEditAction.Create;
            if (!isCreateOrEmpty && contextFiles.TryGetValue(fileEntry.FilePath, out var existingTarget))
            {
                isCreateOrEmpty = string.IsNullOrWhiteSpace(existingTarget) || existingTarget.Trim().Length <= 50;
            }

            if (isCreateOrEmpty)
            {
                sb.AppendLine("=== Reference Architecture Pattern (MANDATORY) ===");
                sb.AppendLine("Follow the structure and conventions of this existing codebase pattern:");
                sb.AppendLine(referencePattern);
                sb.AppendLine();
            }
        }

        var isTest = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);

        // Relevant plan excerpt: extract plan steps relevant to this file or provide concise excerpt
        if (!string.IsNullOrWhiteSpace(request.ProposedPlan))
        {
            var relevantPlan = ExtractRelevantPlanSteps(request.ProposedPlan, fileEntry.FilePath);
            sb.AppendLine("=== Plan Excerpt ===");
            sb.AppendLine(relevantPlan);
            sb.AppendLine();
        }

        if (fileEntry.Action == FileEditAction.Create)
        {
            sb.AppendLine("Create Strategy: emit the smallest compile-complete source file in newContent. No prose, no duplicated explanations, no unnecessary comments, no speculative extra functionality.");
            sb.AppendLine();
        }
        else if (fileEntry.Action == FileEditAction.Modify)
        {
            var editStrategy = useFullFileReplacement
                ? "Edit Strategy: hash-guarded small-file replacement. Return complete resulting content in newContent."
                : isTest
                    ? "Edit Strategy: surgical test patch. Add only the new or modified test method(s) using a concise 2-5 line unique search anchor from the target file (such as the tail of the preceding test method with surrounding structural lines, or the target test signature; never use a bare closing brace alone). NEVER repeat existing unchanged tests, fixtures, or the full test class."
                    : "Edit Strategy: surgical SEARCH/REPLACE patch. Return only the changed regions with exact unique anchors. Do not reproduce unchanged file content.";

            sb.AppendLine(editStrategy);
            sb.AppendLine("=== Current Content of Target File ===");
            if (contextFiles.TryGetValue(fileEntry.FilePath, out var currentContent))
            {
                var boundedContent = BuildBoundedTargetSourceWindow(currentContent, applicabilityFailure: null, isTest);
                sb.AppendLine(boundedContent);
            }
            else
            {
                sb.AppendLine(useFullFileReplacement
                    ? "// File exists in workspace; return its complete resulting content."
                    : "// File exists in workspace; provide exact search/replace edits.");
            }
            sb.AppendLine("=== End Current Content ===");
            sb.AppendLine();
        }

        if (lockedContracts != null && lockedContracts.Count > 0)
        {
            sb.AppendLine("=== Authoritative Upstream Contracts (LOCKED) ===");
            sb.AppendLine("The following upstream signatures are strictly locked. You MUST adhere precisely to these constructor parameter counts, types, method names, and return types:");
            foreach (var (contractPath, contractSig) in lockedContracts)
            {
                sb.AppendLine($"--- Locked Contract: {contractPath} ---");
                sb.AppendLine(contractSig);
                sb.AppendLine("--- End Locked Contract ---");
            }
            sb.AppendLine();
        }

        var directlyReferenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include directly referenced dependency files: use authoritative virtual workspace if generated, else fallback to contextFiles
        if (fileEntry.Dependencies != null && fileEntry.Dependencies.Count > 0)
        {
            sb.AppendLine("=== Directly Referenced Dependency Snippets ===");
            foreach (var depPath in fileEntry.Dependencies)
            {
                if (virtualWorkspace != null && virtualWorkspace.TryGetValue(depPath, out var genDepContent) && !string.IsNullOrWhiteSpace(genDepContent))
                {
                    directlyReferenced.Add(depPath);
                    sb.AppendLine($"--- Dependency File: {depPath} ---");
                    sb.AppendLine(genDepContent);
                    sb.AppendLine("--- End Dependency File ---");
                }
                else if (contextFiles.TryGetValue(depPath, out var depContent))
                {
                    directlyReferenced.Add(depPath);
                    sb.AppendLine($"--- Dependency File: {depPath} ---");
                    sb.AppendLine(depContent);
                    sb.AppendLine("--- End Dependency File ---");
                }
            }
            sb.AppendLine();
        }

        // Include relevant in-memory generated dependency specs from earlier completed waves (excluding any already directly referenced)
        var relevantGenerated = GetRelevantGeneratedEdits(fileEntry, completedEdits, virtualWorkspace);
        AppendPrerequisiteWorkspaceContent(fileEntry, contextFiles, completedEdits, virtualWorkspace, relevantGenerated);
        var nonRedundantGenerated = relevantGenerated
            .Where(kvp => !directlyReferenced.Contains(kvp.Key))
            .ToList();

        if (nonRedundantGenerated.Count > 0)
        {
            sb.AppendLine("=== In-Memory Generated Dependency Snippets ===");
            foreach (var (depPath, depContent) in nonRedundantGenerated)
            {
                sb.AppendLine($"--- Generated Dependency: {depPath} ---");
                sb.AppendLine(depContent);
                sb.AppendLine("--- End Generated Dependency ---");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string BuildSingleFileUserPrompt(
        DeveloperAgentRequest request,
        ManifestFileEntry fileEntry,
        IReadOnlyDictionary<string, string> contextFiles,
        IReadOnlyList<DiscoveredProjectNode> projectGraph)
    {
        return BuildSingleFileUserPrompt(request, fileEntry, contextFiles, completedEdits: null, projectGraph, lockedContracts: null, referencePattern: null, useFullFileReplacement: false, virtualWorkspace: null);
    }

    public static string BuildCompactSingleFileUserPrompt(
        DeveloperAgentRequest request,
        ManifestFileEntry fileEntry,
        string? targetContent,
        IReadOnlyDictionary<string, string>? lockedContracts = null,
        bool useFullFileReplacement = false,
        IReadOnlyDictionary<string, string>? virtualWorkspace = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== COMPACT RETRY (TOKEN LIMIT DISCIPLINE) ===");
        sb.AppendLine(useFullFileReplacement
            ? "Emit only the JSON small-file replacement payload."
            : "The previous response was too long. Emit only the minimal JSON searchReplaceEdits payload. Do not reproduce unchanged file content.");
        sb.AppendLine();
        sb.AppendLine($"Task Title: {request.TaskTitle}");
        sb.AppendLine($"Task Description: {request.TaskDescription}");
        if (!string.IsNullOrWhiteSpace(request.AcceptanceCriteria))
        {
            sb.AppendLine($"Acceptance Criteria: {request.AcceptanceCriteria}");
        }
        sb.AppendLine();
        sb.AppendLine($"Target File: {fileEntry.FilePath}");
        sb.AppendLine($"Action: {fileEntry.Action}");
        sb.AppendLine();

        if (fileEntry.Action == FileEditAction.Modify)
        {
            var isTest = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);
            if (isTest && !useFullFileReplacement)
            {
                sb.AppendLine("CRITICAL TEST MODIFY DISCIPLINE: Emit ONLY the minimal searchReplaceEdit inserting the new test method(s) using a unique 2-5 line anchor from the target (never a bare closing brace alone). Do NOT output unchanged tests or the full class.");
                sb.AppendLine();
            }

            sb.AppendLine("=== Current Content of Target File ===");
            if (!string.IsNullOrWhiteSpace(targetContent))
            {
                var boundedContent = BuildBoundedTargetSourceWindow(targetContent, applicabilityFailure: null, isTest);
                sb.AppendLine(boundedContent);
            }
            else
            {
                sb.AppendLine(useFullFileReplacement
                    ? "// Target file exists in workspace; return its complete resulting content."
                    : "// Target file exists in workspace; provide exact search/replace edits.");
            }
            sb.AppendLine("=== End Current Content ===");
            sb.AppendLine();
        }

        // Filter locked contracts to only directly relevant ones
        if (lockedContracts != null && lockedContracts.Count > 0)
        {
            var relevantContracts = FilterRelevantContracts(fileEntry, lockedContracts, targetContent, request);
            if (relevantContracts.Count > 0)
            {
                sb.AppendLine("=== Required Upstream Contracts (LOCKED) ===");
                foreach (var (contractPath, contractSig) in relevantContracts)
                {
                    sb.AppendLine($"--- Locked Contract: {contractPath} ---");
                    sb.AppendLine(contractSig);
                    sb.AppendLine("--- End Locked Contract ---");
                }
                sb.AppendLine();
            }
        }

        if (virtualWorkspace != null && virtualWorkspace.Count > 0)
        {
            sb.AppendLine("=== In-Memory Generated Dependency Snippets ===");
            foreach (var (depPath, depContent) in virtualWorkspace.Take(5))
            {
                if (string.Equals(depPath, fileEntry.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sb.AppendLine($"--- Generated Dependency: {depPath} ---");
                sb.AppendLine(depContent.Length > 1200 ? depContent[..1200] + "\n...[truncated]" : depContent);
                sb.AppendLine("--- End Generated Dependency ---");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Output ONLY the compact JSON object for '{fileEntry.FilePath}'.");
        return sb.ToString();
    }

    public static string BuildMinimalCreateRetryUserPrompt(
        DeveloperAgentRequest request,
        ManifestFileEntry fileEntry,
        IReadOnlyDictionary<string, string>? lockedContracts = null,
        IReadOnlyDictionary<string, string>? virtualWorkspace = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== MINIMAL CREATE RETRY ===");
        sb.AppendLine("The previous Create response exhausted the output budget. Emit the smallest compile-complete newContent.");
        sb.AppendLine("No prose, no comments, no unused helpers, no speculative extra functionality.");
        sb.AppendLine();
        sb.AppendLine($"Task Title: {request.TaskTitle}");
        sb.AppendLine($"Task Description: {request.TaskDescription}");
        if (!string.IsNullOrWhiteSpace(request.AcceptanceCriteria))
        {
            sb.AppendLine($"Acceptance Criteria: {request.AcceptanceCriteria}");
        }
        sb.AppendLine();
        sb.AppendLine($"Target File: {fileEntry.FilePath}");
        sb.AppendLine("Action: Create");
        if (!string.IsNullOrWhiteSpace(fileEntry.Purpose))
        {
            sb.AppendLine($"Purpose: {fileEntry.Purpose}");
        }
        sb.AppendLine();

        if (lockedContracts != null && lockedContracts.Count > 0)
        {
            var relevantContracts = FilterRelevantContracts(fileEntry, lockedContracts, targetContent: null, request);
            if (relevantContracts.Count > 0)
            {
                sb.AppendLine("=== Required Upstream Contracts (LOCKED) ===");
                foreach (var (contractPath, contractSig) in relevantContracts)
                {
                    sb.AppendLine($"--- Locked Contract: {contractPath} ---");
                    sb.AppendLine(contractSig);
                    sb.AppendLine("--- End Locked Contract ---");
                }
                sb.AppendLine();
            }
        }

        if (virtualWorkspace != null && virtualWorkspace.Count > 0)
        {
            sb.AppendLine("=== In-Memory Generated Dependency Snippets ===");
            foreach (var (depPath, depContent) in virtualWorkspace.Take(5))
            {
                if (string.Equals(depPath, fileEntry.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sb.AppendLine($"--- Generated Dependency: {depPath} ---");
                sb.AppendLine(depContent.Length > 1200 ? depContent[..1200] + "\n...[truncated]" : depContent);
                sb.AppendLine("--- End Generated Dependency ---");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Output ONLY the smallest compile-complete newContent JSON for '{fileEntry.FilePath}'.");
        return sb.ToString();
    }

    public static string BuildMicroModifyRetryUserPrompt(
        DeveloperAgentRequest request,
        ManifestFileEntry fileEntry,
        string? targetContent,
        IReadOnlyDictionary<string, string>? lockedContracts = null,
        IReadOnlyDictionary<string, string>? virtualWorkspace = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== MICRO MODIFY RETRY ===");
        sb.AppendLine("Previous Modify attempts were truncated. Change only the exact required statement using one SEARCH/REPLACE.");
        sb.AppendLine();
        sb.AppendLine($"Task Title: {request.TaskTitle}");
        sb.AppendLine($"Task Description: {request.TaskDescription}");
        if (!string.IsNullOrWhiteSpace(request.AcceptanceCriteria))
        {
            sb.AppendLine($"Acceptance Criteria: {request.AcceptanceCriteria}");
        }
        sb.AppendLine();
        sb.AppendLine($"Target File: {fileEntry.FilePath}");
        sb.AppendLine("Action: Modify");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(targetContent))
        {
            var isTest = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);
            var boundedContent = BuildBoundedTargetSourceWindow(targetContent, applicabilityFailure: null, isTest);
            sb.AppendLine("=== Bounded Target Window ===");
            sb.AppendLine(boundedContent);
            sb.AppendLine("=== End Bounded Target Window ===");
            sb.AppendLine();
        }

        if (lockedContracts != null && lockedContracts.Count > 0)
        {
            var relevantContracts = FilterRelevantContracts(fileEntry, lockedContracts, targetContent, request);
            if (relevantContracts.Count > 0)
            {
                sb.AppendLine("=== Required Upstream Contracts (LOCKED) ===");
                foreach (var (contractPath, contractSig) in relevantContracts)
                {
                    sb.AppendLine($"--- Locked Contract: {contractPath} ---");
                    sb.AppendLine(contractSig);
                    sb.AppendLine("--- End Locked Contract ---");
                }
                sb.AppendLine();
            }
        }

        if (virtualWorkspace != null)
        {
            foreach (var (depPath, depContent) in virtualWorkspace.Take(3))
            {
                if (string.Equals(depPath, fileEntry.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sb.AppendLine($"--- Generated Dependency: {depPath} ---");
                sb.AppendLine(depContent.Length > 800 ? depContent[..800] + "\n...[truncated]" : depContent);
                sb.AppendLine("--- End Generated Dependency ---");
            }
        }

        sb.AppendLine($"Output ONLY one micro searchReplaceEdit JSON for '{fileEntry.FilePath}'.");
        return sb.ToString();
    }

    public static Dictionary<string, string> FilterRelevantContracts(
        ManifestFileEntry fileEntry,
        IReadOnlyDictionary<string, string> lockedContracts,
        string? targetContent = null,
        DeveloperAgentRequest? request = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lockedContracts == null || lockedContracts.Count == 0) return result;

        // If locked contracts set is small (<= 5), retain all of them unconditionally
        // to guarantee zero contract dropping for standard multi-file waves.
        if (lockedContracts.Count <= 5)
        {
            foreach (var (k, v) in lockedContracts)
            {
                result[k] = v;
            }
            return result;
        }

        var targetFileName = Path.GetFileNameWithoutExtension(fileEntry.FilePath);
        var targetBase = targetFileName
            .Replace("Handler", "")
            .Replace("Tests", "")
            .Replace("Test", "")
            .Replace("Controller", "")
            .Replace("Repository", "")
            .Replace("Service", "")
            .Trim();

        var combinedSearchText = string.Concat(
            targetContent ?? string.Empty, " ",
            fileEntry.Purpose ?? string.Empty, " ",
            request?.TaskTitle ?? string.Empty, " ",
            request?.TaskDescription ?? string.Empty, " ",
            request?.AcceptanceCriteria ?? string.Empty);

        foreach (var (contractPath, contractSig) in lockedContracts)
        {
            // 1. Explicit dependency in manifest
            if (fileEntry.Dependencies != null && fileEntry.Dependencies.Contains(contractPath, StringComparer.OrdinalIgnoreCase))
            {
                result[contractPath] = contractSig;
                continue;
            }

            var contractFileName = Path.GetFileNameWithoutExtension(contractPath);

            // 2. Target file or prompt explicitly references the contract file name or type name
            if (!string.IsNullOrWhiteSpace(contractFileName) &&
                combinedSearchText.Contains(contractFileName, StringComparison.OrdinalIgnoreCase))
            {
                result[contractPath] = contractSig;
                continue;
            }

            // 3. Name/feature correspondence
            if (!string.IsNullOrWhiteSpace(targetBase) && targetBase.Length > 2 &&
                contractFileName.Contains(targetBase, StringComparison.OrdinalIgnoreCase))
            {
                result[contractPath] = contractSig;
                continue;
            }

            // 4. Test candidate files receive production contracts
            if (ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath) && !ProjectGraphHelper.IsTestFileCandidate(contractPath))
            {
                result[contractPath] = contractSig;
            }
        }

        return result;
    }

    private static string ExtractRelevantPlanSteps(string proposedPlan, string targetFilePath)
    {
        if (string.IsNullOrWhiteSpace(proposedPlan)) return string.Empty;
        var targetFileName = Path.GetFileName(targetFilePath);
        var lines = proposedPlan.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var relevantLines = lines.Where(l =>
            l.Contains(targetFilePath, StringComparison.OrdinalIgnoreCase) ||
            l.Contains(targetFileName, StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("Step ", StringComparison.OrdinalIgnoreCase)).ToList();

        if (relevantLines.Count > 0 && relevantLines.Count < lines.Length)
        {
            return string.Join('\n', relevantLines).Trim();
        }

        return proposedPlan.Trim();
    }

    private static void AppendPrerequisiteWorkspaceContent(
        ManifestFileEntry fileEntry,
        IReadOnlyDictionary<string, string> contextFiles,
        IReadOnlyDictionary<string, FileEditSpec>? completedEdits,
        IReadOnlyDictionary<string, string>? virtualWorkspace,
        IDictionary<string, string> relevantGenerated)
    {
        if (virtualWorkspace == null || virtualWorkspace.Count == 0 || completedEdits == null)
        {
            return;
        }

        foreach (var (path, content) in virtualWorkspace)
        {
            if (string.IsNullOrWhiteSpace(content) ||
                string.Equals(path, fileEntry.FilePath, StringComparison.OrdinalIgnoreCase) ||
                relevantGenerated.ContainsKey(path) ||
                !completedEdits.ContainsKey(path))
            {
                continue;
            }

            var producer = new ManifestFileEntry(path, FileEditAction.Create);
            if (GenerationDependencyAnalyzer.DependsOn(fileEntry, producer, existingFileContents: contextFiles))
            {
                relevantGenerated[path] = content.Length <= 3000
                    ? content
                    : (RoslynContractExtractor.ExtractPublicContracts(path, content) ?? content);
            }
        }
    }

    public static Dictionary<string, string> GetRelevantGeneratedEdits(
        ManifestFileEntry fileEntry,
        IReadOnlyDictionary<string, FileEditSpec>? completedEdits,
        IReadOnlyDictionary<string, string>? virtualWorkspace = null)
    {
        var relevant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (completedEdits == null || completedEdits.Count == 0) return relevant;

        var targetFileName = Path.GetFileNameWithoutExtension(fileEntry.FilePath);
        var targetBaseName = targetFileName
            .Replace("Handler", "")
            .Replace("Tests", "")
            .Replace("Test", "")
            .Replace("Controller", "")
            .Replace("Repository", "")
            .Trim();

        bool isTestTarget = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);

        foreach (var (path, spec) in completedEdits)
        {
            if (string.Equals(path, fileEntry.FilePath, StringComparison.OrdinalIgnoreCase)) continue;

            bool isRelevant = false;

            // 1. Explicit dependency
            if (fileEntry.Dependencies != null && fileEntry.Dependencies.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                isRelevant = true;
            }

            // 2. Feature / name correspondence (e.g. GetTaskSummaryQuery for GetTaskSummaryQueryHandler)
            var depFileName = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(targetBaseName) && targetBaseName.Length > 2 && depFileName.Contains(targetBaseName, StringComparison.OrdinalIgnoreCase))
            {
                isRelevant = true;
            }

            // 3. Tests receive production code generated in earlier waves
            if (isTestTarget && !ProjectGraphHelper.IsTestFileCandidate(path))
            {
                isRelevant = true;
            }

            // 4. Interface -> Implementation
            if (depFileName.StartsWith("I") && depFileName.Length > 2 && targetFileName.EndsWith(depFileName.Substring(1), StringComparison.OrdinalIgnoreCase))
            {
                isRelevant = true;
            }

            if (isRelevant)
            {
                // Prefer authoritative virtual workspace content
                if (virtualWorkspace != null && virtualWorkspace.TryGetValue(path, out var generatedContent) && !string.IsNullOrWhiteSpace(generatedContent))
                {
                    if (isTestTarget)
                    {
                        // For test targets: provide behavioral implementation details
                        // For ordinary files (<= 4000 chars), provide full resulting code
                        // For large files (> 4000 chars), provide public contract + bounded behavioral implementation excerpt
                        if (generatedContent.Length <= 4000)
                        {
                            relevant[path] = generatedContent;
                        }
                        else
                        {
                            relevant[path] = ExtractBehavioralTestContext(path, generatedContent, spec);
                        }
                    }
                    else if (generatedContent.Length <= 3000)
                    {
                        relevant[path] = generatedContent;
                    }
                    else
                    {
                        var contract = RoslynContractExtractor.ExtractPublicContracts(path, generatedContent);
                        relevant[path] = !string.IsNullOrWhiteSpace(contract) ? contract : generatedContent;
                    }
                }
                else
                {
                    // Fallback to completedEdits specs if virtualWorkspace is not supplied
                    if (spec.Action == FileEditAction.Create && !string.IsNullOrWhiteSpace(spec.NewContent))
                    {
                        if (isTestTarget)
                        {
                            if (spec.NewContent.Length <= 4000)
                            {
                                relevant[path] = spec.NewContent;
                            }
                            else
                            {
                                relevant[path] = ExtractBehavioralTestContext(path, spec.NewContent, spec);
                            }
                        }
                        else if (spec.NewContent.Length > 2000)
                        {
                            var contract = RoslynContractExtractor.ExtractPublicContracts(path, spec.NewContent);
                            relevant[path] = !string.IsNullOrWhiteSpace(contract) ? contract : spec.NewContent;
                        }
                        else
                        {
                            relevant[path] = spec.NewContent;
                        }
                    }
                    else if (spec.Action == FileEditAction.Modify && !string.IsNullOrWhiteSpace(spec.NewContent))
                    {
                        if (isTestTarget)
                        {
                            if (spec.NewContent.Length <= 4000)
                            {
                                relevant[path] = spec.NewContent;
                            }
                            else
                            {
                                relevant[path] = ExtractBehavioralTestContext(path, spec.NewContent, spec);
                            }
                        }
                        else
                        {
                            var contract = RoslynContractExtractor.ExtractPublicContracts(path, spec.NewContent);
                            relevant[path] = !string.IsNullOrWhiteSpace(contract) ? contract : spec.NewContent;
                        }
                    }
                    else if (spec.Action == FileEditAction.Modify && spec.SearchReplaceEdits != null && spec.SearchReplaceEdits.Count > 0)
                    {
                        var summary = string.Join("\n", spec.SearchReplaceEdits.Select(e => $"Replace:\n{e.Search}\nWith:\n{e.Replace}"));
                        relevant[path] = summary;
                    }
                }
            }
        }

        return relevant;
    }

    public static string ExtractBehavioralTestContext(
        string filePath,
        string fullContent,
        FileEditSpec? editSpec = null,
        int maxTotalChars = 3500)
    {
        if (string.IsNullOrWhiteSpace(fullContent)) return string.Empty;
        if (fullContent.Length <= maxTotalChars) return fullContent;

        var sb = new System.Text.StringBuilder();
        var contract = RoslynContractExtractor.ExtractPublicContracts(filePath, fullContent);
        if (!string.IsNullOrWhiteSpace(contract))
        {
            sb.AppendLine("=== Authoritative Public Contract ===");
            sb.AppendLine(contract);
            sb.AppendLine();
        }

        sb.AppendLine("=== Relevant Implementation Behavior ===");

        // If editSpec has SearchReplaceEdits, include the modified regions containing behavioral logic
        if (editSpec?.SearchReplaceEdits is { Count: > 0 })
        {
            foreach (var edit in editSpec.SearchReplaceEdits)
            {
                if (!string.IsNullOrWhiteSpace(edit.Replace))
                {
                    sb.AppendLine($"// Modified region in {Path.GetFileName(filePath)}:");
                    sb.AppendLine(edit.Replace.Trim());
                    sb.AppendLine();
                }
            }
        }
        else
        {
            // Extract method bodies deterministically using Roslyn syntax tree
            try
            {
                var tree = CSharpSyntaxTree.ParseText(fullContent);
                var root = tree.GetCompilationUnitRoot();
                var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Body != null || m.ExpressionBody != null)
                    .ToList();

                int charsUsed = sb.Length;
                foreach (var method in methods)
                {
                    var methodStr = method.ToFullString().Trim();
                    if (charsUsed + methodStr.Length > maxTotalChars && charsUsed > 500)
                    {
                        sb.AppendLine("// ... [remaining method implementations omitted for brevity]");
                        break;
                    }
                    sb.AppendLine(methodStr);
                    sb.AppendLine();
                    charsUsed += methodStr.Length;
                }
            }
            catch
            {
                var boundedExcerpt = fullContent.Substring(0, Math.Min(fullContent.Length, 2500));
                sb.AppendLine(boundedExcerpt);
            }
        }

        return sb.ToString().Trim();
    }

    public static int GetSemanticLayerScore(string filePath)
    {
        var p = filePath.Replace('\\', '/').ToLowerInvariant();
        var fileName = Path.GetFileName(p);

        // 1. DTOs, Enums, Models, Results, Common Value Objects (Layer 5)
        if (p.Contains("/enums/") || p.Contains("/dtos/") || p.Contains("/models/") || p.Contains("/results/") ||
            fileName.EndsWith("dto.cs") || fileName.EndsWith("enum.cs") || fileName.EndsWith("result.cs") || fileName.EndsWith("response.cs"))
        {
            return 5;
        }

        // 2. Interfaces / Ports / Contracts (Layer 10)
        if (p.Contains("/interfaces/") || p.Contains("/ports/") || p.Contains("/contracts/") ||
            (fileName.StartsWith("i") && fileName.Length > 2 && char.IsLetter(fileName[1]) && !p.Contains("/controllers/")))
        {
            return 10;
        }

        // 3. Domain Entities / Aggregates (Layer 20)
        if (p.Contains("/domain/") || p.Contains("/entities/") || p.Contains("/aggregates/"))
        {
            return 20;
        }

        // 4. Command / Query / Request definitions (NOT handlers) (Layer 25)
        if (fileName.EndsWith("query.cs") || fileName.EndsWith("command.cs") || fileName.EndsWith("request.cs"))
        {
            return 25;
        }

        // 5. Command / Query Handlers / Application Services (Layer 30)
        if (p.Contains("/application/") || p.Contains("/handlers/") || p.Contains("/commands/") || p.Contains("/queries/") ||
            p.Contains("/services/") || fileName.EndsWith("handler.cs") || fileName.EndsWith("service.cs"))
        {
            return 30;
        }

        // 6. Infrastructure implementations / Repositories / Persistence (Layer 40)
        if (p.Contains("/infrastructure/") || p.Contains("/repositories/") || p.Contains("/persistence/") ||
            p.Contains("/ef/") || fileName.EndsWith("repository.cs"))
        {
            return 40;
        }

        // 7. API Controllers / Endpoints (Layer 50)
        if (p.Contains("/api/") || p.Contains("/controllers/") || p.Contains("/endpoints/") ||
            fileName.EndsWith("controller.cs") || fileName.EndsWith("endpoint.cs"))
        {
            return 50;
        }

        // 8. Unit / Integration Tests (Layer 60)
        if (p.Contains("/tests/") || fileName.EndsWith("tests.cs") || fileName.EndsWith("test.cs"))
        {
            return 60;
        }

        return 35;
    }

    public static IReadOnlyList<ManifestFileEntry> SortManifestEntries(
        IReadOnlyList<ManifestFileEntry> files,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph = null)
    {
        if (files == null || files.Count <= 1) return files ?? Array.Empty<ManifestFileEntry>();

        return files
            .OrderBy(f => GetSemanticLayerScore(f.FilePath))
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<List<ManifestFileEntry>> BuildExecutionWaves(
        IReadOnlyList<ManifestFileEntry> files,
        IReadOnlyList<DiscoveredProjectNode>? projectGraph = null)
    {
        if (files == null || files.Count == 0) return new List<List<ManifestFileEntry>>();

        var sorted = SortManifestEntries(files, projectGraph);

        var layerGroups = sorted
            .GroupBy(f => GetSemanticLayerScore(f.FilePath))
            .OrderBy(g => g.Key)
            .ToList();

        var waves = new List<List<ManifestFileEntry>>();

        foreach (var group in layerGroups)
        {
            var groupFiles = group.ToList();
            if (groupFiles.Count <= 1)
            {
                waves.Add(groupFiles);
                continue;
            }

            // Check if files in this layer have internal dependency indications
            bool hasInternalDeps = groupFiles.Any(f =>
                (f.Dependencies != null && f.Dependencies.Any(d => groupFiles.Any(other => string.Equals(other.FilePath, d, StringComparison.OrdinalIgnoreCase)))) ||
                groupFiles.Any(other => !string.Equals(other.FilePath, f.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    IsIntraGroupDependent(f, other)));

            if (!hasInternalDeps)
            {
                waves.Add(groupFiles);
            }
            else
            {
                // Partition conservatively into sequential sub-waves
                foreach (var singleFile in groupFiles)
                {
                    waves.Add(new List<ManifestFileEntry> { singleFile });
                }
            }
        }

        return waves;
    }

    private static bool IsIntraGroupDependent(ManifestFileEntry candidateA, ManifestFileEntry candidateB)
    {
        var nameA = Path.GetFileNameWithoutExtension(candidateA.FilePath);
        var nameB = Path.GetFileNameWithoutExtension(candidateB.FilePath);

        if (nameA.Length > 3 && nameB.Length > 3)
        {
            if (nameA.StartsWith(nameB, StringComparison.OrdinalIgnoreCase) ||
                nameB.StartsWith(nameA, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static FileEditSpec ParseSingleFileEditSpec(string rawContent, ManifestFileEntry expectedEntry)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            throw new FormatException("Raw single file edit content is empty.");
        }

        var candidates = ExtractJsonCandidates(rawContent);
        Exception? lastException = null;

        foreach (var candidate in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    // Check if it's wrapped in { "files": [ ... ] }
                    if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                    {
                        var files = JsonSerializer.Deserialize<IReadOnlyList<FileEditSpec>>(filesEl.GetRawText(), JsonOpts);
                        if (files != null && files.Count > 0)
                        {
                            var match = files.FirstOrDefault(f => string.Equals(f.FilePath, expectedEntry.FilePath, StringComparison.OrdinalIgnoreCase)) ?? files[0];
                            return match;
                        }
                    }

                    // Direct FileEditSpec object
                    if (root.TryGetProperty("filePath", out _) || root.TryGetProperty("action", out _) ||
                        root.TryGetProperty("newContent", out _) || root.TryGetProperty("searchReplaceEdits", out _))
                    {
                        var spec = JsonSerializer.Deserialize<FileEditSpec>(candidate, JsonOpts);
                        if (spec != null)
                        {
                            var normalizedPath = string.IsNullOrWhiteSpace(spec.FilePath) ? expectedEntry.FilePath : spec.FilePath;
                            return new FileEditSpec(normalizedPath, spec.Action == default ? expectedEntry.Action : spec.Action, spec.NewContent, spec.SearchReplaceEdits);
                        }
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    var files = JsonSerializer.Deserialize<IReadOnlyList<FileEditSpec>>(candidate, JsonOpts);
                    if (files != null && files.Count > 0)
                    {
                        return files[0];
                    }
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new FormatException($"Failed to parse valid edit spec for '{expectedEntry.FilePath}'.");
    }

    public static void ValidateSingleFileEditSpec(
        FileEditSpec spec,
        ManifestFileEntry expectedEntry,
        string? targetContent = null,
        bool useFullFileReplacement = false)
    {
        if (spec == null)
        {
            throw new FormatException($"Edit spec is null for '{expectedEntry.FilePath}'.");
        }

        if (spec.Action == FileEditAction.Create)
        {
            if (spec.NewContent == null)
            {
                throw new FormatException($"Create action for '{expectedEntry.FilePath}' requires 'newContent'.");
            }
        }
        else if (spec.Action == FileEditAction.Modify)
        {
            if (useFullFileReplacement)
            {
                var usesFullFileReplacement = spec.NewContent != null;
                var hasSearchReplaceEdits = spec.SearchReplaceEdits is { Count: > 0 };
                if (usesFullFileReplacement == hasSearchReplaceEdits)
                {
                    throw new FormatException($"Small-file Modify action for '{expectedEntry.FilePath}' requires exactly one edit representation.");
                }

                if (usesFullFileReplacement)
                {
                    return;
                }
            }

            if (spec.NewContent != null)
            {
                throw new FormatException($"Modify action for '{expectedEntry.FilePath}' must use surgical 'searchReplaceEdits'.");
            }

            if (spec.SearchReplaceEdits == null || spec.SearchReplaceEdits.Count == 0)
            {
                throw new FormatException($"Modify action for '{expectedEntry.FilePath}' requires at least one edit in 'searchReplaceEdits'.");
            }

            if (spec.SearchReplaceEdits.Count > 30)
            {
                throw new FormatException($"Modify action for '{expectedEntry.FilePath}' contains an excessive number of edit blocks ({spec.SearchReplaceEdits.Count}).");
            }

            var seenSearches = new HashSet<string>(StringComparer.Ordinal);
            long totalSearchChars = 0;
            long totalReplaceChars = 0;

            for (int i = 0; i < spec.SearchReplaceEdits.Count; i++)
            {
                var edit = spec.SearchReplaceEdits[i];
                if (string.IsNullOrEmpty(edit.Search))
                {
                    throw new FormatException($"Modify action for '{expectedEntry.FilePath}' contains empty search string at edit index {i}.");
                }
                if (edit.Replace == null)
                {
                    throw new FormatException($"Modify action for '{expectedEntry.FilePath}' contains null replace string at edit index {i}.");
                }

                if (!seenSearches.Add(edit.Search))
                {
                    throw new FormatException($"Modify action for '{expectedEntry.FilePath}' contains duplicate SearchReplaceEdit blocks for search pattern at edit index {i}.");
                }

                totalSearchChars += edit.Search.Length;
                totalReplaceChars += edit.Replace.Length;
            }

            if (!string.IsNullOrEmpty(targetContent))
            {
                var targetLength = targetContent.Length;

                if (targetLength > 200 && spec.SearchReplaceEdits.Count == 1)
                {
                    var singleSearch = spec.SearchReplaceEdits[0].Search;
                    if (singleSearch.Length >= (int)(targetLength * 0.90))
                    {
                        throw new FormatException(
                            $"Modify action for '{expectedEntry.FilePath}' effectively reproduces the entire file ({singleSearch.Length}/{targetLength} chars) in a single search block instead of a focused patch.");
                    }
                }

                var maxAllowedAggregate = Math.Max(targetLength * 3, 25000);
                if (totalSearchChars + totalReplaceChars > maxAllowedAggregate)
                {
                    throw new FormatException(
                        $"Modify action for '{expectedEntry.FilePath}' emitted pathological output size ({totalSearchChars + totalReplaceChars} chars) exceeding safety bound relative to target source ({targetLength} chars).");
                }
            }
        }
    }

    public static StructuredEditPlan ParseStructuredPlan(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            throw new FormatException("Raw edit content is empty.");
        }

        var candidates = ExtractJsonCandidates(rawContent);
        Exception? lastException = null;

        foreach (var candidate in candidates)
        {
            try
            {
                var plan = TryDeserializeCandidate(candidate);
                if (plan != null)
                {
                    ValidateStructuredPlan(plan);
                    return plan;
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new FormatException("Failed to extract valid structured edit plan from content.");
    }

    private static List<string> ExtractJsonCandidates(string rawContent)
    {
        var candidates = new List<string>();

        var matches = MarkdownCodeFenceRegex.Matches(rawContent);
        foreach (Match match in matches)
        {
            if (match.Success && match.Groups.Count > 1)
            {
                var fenceContent = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(fenceContent) && !candidates.Contains(fenceContent))
                {
                    candidates.Add(fenceContent);
                }
            }
        }

        var firstBrace = rawContent.IndexOf('{');
        var lastBrace = rawContent.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            var braceCandidate = rawContent.Substring(firstBrace, lastBrace - firstBrace + 1).Trim();
            if (!candidates.Contains(braceCandidate))
            {
                candidates.Add(braceCandidate);
            }
        }

        var firstBracket = rawContent.IndexOf('[');
        var lastBracket = rawContent.LastIndexOf(']');
        if (firstBracket >= 0 && lastBracket > firstBracket)
        {
            var bracketCandidate = rawContent.Substring(firstBracket, lastBracket - firstBracket + 1).Trim();
            if (!candidates.Contains(bracketCandidate))
            {
                candidates.Add(bracketCandidate);
            }
        }

        var trimmed = StripFences(rawContent.Trim());
        if (!string.IsNullOrWhiteSpace(trimmed) && !candidates.Contains(trimmed))
        {
            candidates.Add(trimmed);
        }

        return candidates;
    }

    private static string StripFences(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(7);
        }
        else if (s.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(3);
        }

        if (s.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(0, s.Length - 3);
        }

        return s.Trim();
    }

    private static StructuredEditPlan? TryDeserializeCandidate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("files", out _) || root.TryGetProperty("Files", out _))
            {
                return JsonSerializer.Deserialize<StructuredEditPlan>(json, JsonOpts);
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var files = JsonSerializer.Deserialize<IReadOnlyList<FileEditSpec>>(prop.Value.GetRawText(), JsonOpts);
                    if (files != null && files.Count > 0)
                    {
                        return new StructuredEditPlan(files);
                    }
                }
            }

            return JsonSerializer.Deserialize<StructuredEditPlan>(json, JsonOpts);
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            var files = JsonSerializer.Deserialize<IReadOnlyList<FileEditSpec>>(json, JsonOpts);
            if (files != null)
            {
                return new StructuredEditPlan(files);
            }
        }

        return null;
    }

    public static void ValidateStructuredPlan(StructuredEditPlan plan)
    {
        if (plan == null)
        {
            throw new FormatException("Structured edit plan is null.");
        }

        if (plan.Files == null || plan.Files.Count == 0)
        {
            throw new FormatException("Structured edit plan must contain at least one file edit in 'files'.");
        }

        foreach (var file in plan.Files)
        {
            if (string.IsNullOrWhiteSpace(file.FilePath))
            {
                throw new FormatException("FileEditSpec contains an empty or missing 'filePath'.");
            }

            if (file.Action == FileEditAction.Create)
            {
                if (file.NewContent == null)
                {
                    throw new FormatException($"Create action for file '{file.FilePath}' requires 'newContent'.");
                }
            }
            else if (file.Action == FileEditAction.Modify)
            {
                var usesFullFileReplacement = file.NewContent != null;
                var hasSearchReplaceEdits = file.SearchReplaceEdits is { Count: > 0 };
                if (usesFullFileReplacement == hasSearchReplaceEdits)
                {
                    throw new FormatException($"Modify action for file '{file.FilePath}' requires exactly one edit representation.");
                }

                if (!hasSearchReplaceEdits)
                {
                    continue;
                }

                foreach (var edit in file.SearchReplaceEdits!)
                {
                    if (string.IsNullOrEmpty(edit.Search))
                    {
                        throw new FormatException($"Modify action for file '{file.FilePath}' contains an empty 'search' target.");
                    }
                    if (edit.Replace == null)
                    {
                        throw new FormatException($"Modify action for file '{file.FilePath}' contains null 'replace' content.");
                    }
                }
            }
            else
            {
                throw new FormatException($"File '{file.FilePath}' has an invalid action '{file.Action}'.");
            }
        }
    }

    public static string BuildSystemPrompt() => BuildManifestSystemPrompt();
    public static string BuildRepairSystemPrompt() => BuildManifestRepairSystemPrompt();
    public static string BuildRepairUserPrompt(string parseError, string previousResponse) => BuildManifestRepairUserPrompt(parseError, previousResponse);
    public static string BuildUserPrompt(DeveloperAgentRequest request, IReadOnlyDictionary<string, string> contextFiles, IReadOnlyList<DiscoveredProjectNode> projectGraph) =>
        BuildManifestUserPrompt(request, contextFiles, projectGraph);

    private static string BuildProviderFailureMessage(AiResponse response, string filePath)
    {
        var provider = !string.IsNullOrWhiteSpace(response.Provider) ? response.Provider : "AI provider";
        var statusText = response.StatusCode.HasValue ? $"HTTP {response.StatusCode.Value}" : null;

        if (response.FailureKind == AiFailureKind.TransientServiceUnavailable)
        {
            var codePart = statusText != null ? $" ({statusText})" : "";
            return $"AI provider {provider} remained unavailable{codePart} while generating '{filePath}' after bounded retries.";
        }
        if (response.FailureKind == AiFailureKind.RateLimited)
        {
            var codePart = statusText != null ? $" ({statusText})" : "";
            return $"AI provider {provider} was rate limited{codePart} while generating '{filePath}' after bounded retries.";
        }
        if (response.FailureKind == AiFailureKind.TimeoutOrConnection)
        {
            return $"AI provider {provider} connection timed out while generating '{filePath}' after bounded retries.";
        }
        if (response.FailureKind == AiFailureKind.Permanent)
        {
            var codePart = statusText != null ? $" ({statusText})" : "";
            return $"AI provider {provider} failed with permanent error{codePart} while generating '{filePath}'.";
        }

        var rawError = !string.IsNullOrWhiteSpace(response.ErrorMessage)
            ? response.ErrorMessage
            : $"AI provider {provider} request failed";

        return rawError.Contains(filePath, StringComparison.OrdinalIgnoreCase)
            ? rawError
            : $"{rawError.TrimEnd('.')} while generating '{filePath}'.";
    }

    private static string SanitizeForDiagnostics(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sanitized = input.Length > 300 ? input.Substring(0, 300) + "..." : input;
        return sanitized.Replace("\r", " ").Replace("\n", " ");
    }

    private sealed class ConcurrencyCallCounter
    {
        private int _count;
        private int _compactRetryCount;
        private int _applicabilityRepairCount;
        private readonly int _maxLimit;

        public ConcurrencyCallCounter(int maxLimit)
        {
            _maxLimit = maxLimit;
        }

        public bool TryIncrement(out int callNumber)
        {
            var next = Interlocked.Increment(ref _count);
            callNumber = next;
            return next <= _maxLimit;
        }

        public int CurrentCount => Volatile.Read(ref _count);
        public int CompactRetryCount => Volatile.Read(ref _compactRetryCount);
        public int ApplicabilityRepairCount => Volatile.Read(ref _applicabilityRepairCount);

        public void RecordCompactRetry() => Interlocked.Increment(ref _compactRetryCount);
        public void RecordApplicabilityRepair() => Interlocked.Increment(ref _applicabilityRepairCount);
    }

    private sealed class ReadyFileComparer : IComparer<ManifestFileEntry>
    {
        public int Compare(ManifestFileEntry? x, ManifestFileEntry? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x == null)
            {
                return -1;
            }

            if (y == null)
            {
                return 1;
            }

            var layer = GetSemanticLayerScore(x.FilePath).CompareTo(GetSemanticLayerScore(y.FilePath));
            if (layer != 0)
            {
                return layer;
            }

            return string.Compare(x.FilePath, y.FilePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class GenerationRunState
    {
        private int _active;
        private int _peak;
        private long _sumProviderMs;
        private int _retryCount;
        private readonly ConcurrentBag<(string Path, long TotalMs)> _fileDurations = new();

        public GenerationRunState(int configuredConcurrency)
        {
            ConfiguredConcurrency = configuredConcurrency;
        }

        public int ConfiguredConcurrency { get; }
        public int PeakConcurrent => Volatile.Read(ref _peak);
        public long SumProviderDurationMs => Interlocked.Read(ref _sumProviderMs);
        public int RetryCount => Volatile.Read(ref _retryCount);

        public int BeginFile()
        {
            var current = Interlocked.Increment(ref _active);
            while (true)
            {
                var observedPeak = Volatile.Read(ref _peak);
                if (current <= observedPeak ||
                    Interlocked.CompareExchange(ref _peak, current, observedPeak) == observedPeak)
                {
                    break;
                }
            }

            return current;
        }

        public void EndFile() => Interlocked.Decrement(ref _active);

        public void AddProviderDuration(long milliseconds) =>
            Interlocked.Add(ref _sumProviderMs, Math.Max(0, milliseconds));

        public void RecordRetry() => Interlocked.Increment(ref _retryCount);

        public void RecordFileDuration(string filePath, long totalMs) =>
            _fileDurations.Add((filePath, totalMs));

        public IReadOnlyList<string> FilesExceeding60s() =>
            _fileDurations
                .Where(item => item.TotalMs >= 60_000)
                .Select(item => Path.GetFileName(item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

        public IReadOnlyList<string> LongestGenerationCalls() =>
            _fileDurations
                .OrderByDescending(item => item.TotalMs)
                .Take(3)
                .Select(item => $"{item.Path}:{item.TotalMs}")
                .ToList();
    }

    private static ExecutionActivityMetadata BuildGenerationSummaryMetadata(
        ConcurrencyCallCounter callCounter,
        Stopwatch generationStopwatch,
        GenerationRunState runTelemetry) => new(
            EventKind: "GenerationSummary",
            LogicalProviderCallCount: callCounter.CurrentCount,
            CompactRetryCount: callCounter.CompactRetryCount,
            ApplicabilityRepairCount: callCounter.ApplicabilityRepairCount,
            TotalGenerationTimeMs: generationStopwatch.ElapsedMilliseconds,
            StageDurationMs: generationStopwatch.ElapsedMilliseconds,
            ConfiguredConcurrency: runTelemetry.ConfiguredConcurrency,
            PeakConcurrentGenerationCount: runTelemetry.PeakConcurrent,
            SumProviderDurationMs: runTelemetry.SumProviderDurationMs,
            GenerationCallCount: callCounter.CurrentCount,
            RetryOccurred: runTelemetry.RetryCount > 0,
            FilesExceeding60s: runTelemetry.FilesExceeding60s(),
            LongestGenerationCalls: runTelemetry.LongestGenerationCalls());
}
