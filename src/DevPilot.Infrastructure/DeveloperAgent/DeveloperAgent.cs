using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
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
            if (targetContent != null && WorktreeEditApplier.IsSmallTextFile(targetContent))
            {
                var estimatedFullFileTokens = (targetContent.Length + 2) / 3 + 512;
                var roundedBudget = ((estimatedFullFileTokens + 511) / 512) * 512;
                return Math.Min(Math.Clamp(roundedBudget, 2048, 4096), _maxOutputTokens);
            }

            // SEARCH/REPLACE output is expected to contain only the changed blocks,
            // so its budget must not scale with the size or role of the target file.
            return Math.Min(Math.Min(_budgetModifyPatch, 8192), _maxOutputTokens);
        }

        var score = GetSemanticLayerScore(filePath);
        var budget = score switch
        {
            5 => _budgetDtoOrModel,
            10 => _budgetInterfaceOrContract,
            20 => _budgetDtoOrModel,
            25 => _budgetQueryOrCommand,
            30 => _budgetHandlerOrService,
            40 => _budgetHandlerOrService,
            50 => _budgetController,
            60 => _budgetTestFile,
            _ => _budgetFallback
        };

        return Math.Min(budget, _maxOutputTokens);
    }

    public int DetermineCompactRetryBudget(int initialBudget, string? targetContent, ManifestFileEntry fileEntry, bool isRepair = false)
    {
        if (fileEntry.Action == FileEditAction.Modify)
        {
            // A Modify retry remains a compact edit response. It never needs a full-file/test-file
            // budget, and an applicability repair does not own an additional token retry.
            if (isRepair)
            {
                return initialBudget;
            }

            return Math.Min(Math.Max(initialBudget * 2, 4096), Math.Min(8192, _maxCompactRetryOutputTokens));
        }

        int targetLines = targetContent != null ? targetContent.Split('\n').Length : 0;
        bool isLargeFile = targetLines > 100 || (targetContent?.Length ?? 0) > 4000;
        bool isTestFile = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);

        int candidateBudget;
        if (isLargeFile || isTestFile)
        {
            candidateBudget = (isTestFile && isLargeFile) || targetLines > 150 || (targetContent?.Length ?? 0) > 6000
                ? _maxCompactRetryOutputTokens
                : (isRepair ? 16384 : 12288);
        }
        else
        {
            candidateBudget = Math.Max(initialBudget * 2, 8192);
        }

        return Math.Min(Math.Max(candidateBudget, initialBudget), _maxCompactRetryOutputTokens);
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
                new ExecutionActivityMetadata(
                    EventKind: "GenerationSummary",
                    LogicalProviderCallCount: callCounter.CurrentCount,
                    CompactRetryCount: callCounter.CompactRetryCount,
                    ApplicabilityRepairCount: callCounter.ApplicabilityRepairCount,
                    TotalGenerationTimeMs: generationStopwatch.ElapsedMilliseconds,
                    StageDurationMs: generationStopwatch.ElapsedMilliseconds)).ConfigureAwait(false);
            return DeveloperAgentResult.Fail(ex.Message, model: capturedModels.FirstOrDefault() ?? request.Model);
        }

        generationStopwatch.Stop();
        await SafeRecordActivityAsync(
            request.ExecutionId,
            "Generation technical summary.",
            cancellationToken,
            ExecutionActivityStatus.Completed,
            new ExecutionActivityMetadata(
                EventKind: "GenerationSummary",
                LogicalProviderCallCount: callCounter.CurrentCount,
                CompactRetryCount: callCounter.CompactRetryCount,
                ApplicabilityRepairCount: callCounter.ApplicabilityRepairCount,
                TotalGenerationTimeMs: generationStopwatch.ElapsedMilliseconds,
                StageDurationMs: generationStopwatch.ElapsedMilliseconds)).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<ManifestFileEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in sortedFiles)
        {
            inDegree[file.FilePath] = 0;
            dependents[file.FilePath] = new List<ManifestFileEntry>();
        }

        for (int i = 0; i < sortedFiles.Count; i++)
        {
            var target = sortedFiles[i];
            var targetScore = GetSemanticLayerScore(target.FilePath);

            for (int j = 0; j < sortedFiles.Count; j++)
            {
                if (i == j) continue;
                var candidate = sortedFiles[j];
                var candidateScore = GetSemanticLayerScore(candidate.FilePath);

                bool dependsOn = false;
                if (target.Dependencies != null && target.Dependencies.Contains(candidate.FilePath, StringComparer.OrdinalIgnoreCase))
                {
                    dependsOn = true;
                }
                else if (targetScore > candidateScore)
                {
                    var targetBase = Path.GetFileNameWithoutExtension(target.FilePath)
                        .Replace("Handler", "").Replace("Tests", "").Replace("Test", "").Replace("Controller", "").Trim();
                    var candBase = Path.GetFileNameWithoutExtension(candidate.FilePath);

                    if (string.IsNullOrEmpty(targetBase) || candBase.Contains(targetBase, StringComparison.OrdinalIgnoreCase) ||
                        ProjectGraphHelper.IsTestFileCandidate(target.FilePath) ||
                        targetScore >= 50)
                    {
                        dependsOn = true;
                    }
                }

                if (dependsOn)
                {
                    inDegree[target.FilePath]++;
                    dependents[candidate.FilePath].Add(target);
                }
            }
        }

        var readyQueue = new System.Collections.Concurrent.ConcurrentQueue<ManifestFileEntry>();
        foreach (var file in sortedFiles)
        {
            if (inDegree[file.FilePath] == 0)
            {
                readyQueue.Enqueue(file);
            }
        }

        int remainingFiles = sortedFiles.Count;
        var inDegreeLock = new object();
        int activeConcurrency = _maxConcurrentFileGenerations;
        using var concurrencySemaphore = new SemaphoreSlim(activeConcurrency, activeConcurrency);
        var activeTasks = new List<Task>();

        while (remainingFiles > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (readyQueue.TryDequeue(out var nextFile))
            {
                await concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        var editSpec = await GenerateSingleFileEditAsync(
                            nextFile,
                            fileIndexMap[nextFile.FilePath],
                            totalFiles,
                            request,
                            contextFiles,
                            collectedEdits,
                            virtualWorkspace,
                            lockedContracts,
                            projectGraph,
                            callCounter,
                            capturedModels,
                            cancellationToken).ConfigureAwait(false);

                        collectedEdits[nextFile.FilePath] = editSpec;

                        lock (inDegreeLock)
                        {
                            Interlocked.Decrement(ref remainingFiles);
                            foreach (var dep in dependents[nextFile.FilePath])
                            {
                                inDegree[dep.FilePath]--;
                                if (inDegree[dep.FilePath] == 0)
                                {
                                    readyQueue.Enqueue(dep);
                                }
                            }
                        }
                    }
                    finally
                    {
                        concurrencySemaphore.Release();
                    }
                }, cancellationToken);

                activeTasks.Add(task);
            }
            else
            {
                if (activeTasks.Count > 0)
                {
                    var completedTask = await Task.WhenAny(activeTasks).ConfigureAwait(false);
                    activeTasks.Remove(completedTask);
                    await completedTask.ConfigureAwait(false);
                }
                else if (remainingFiles > 0)
                {
                    // Deadlock / Cycle break safety
                    lock (inDegreeLock)
                    {
                        var lowest = sortedFiles
                            .Where(f => !collectedEdits.ContainsKey(f.FilePath))
                            .OrderBy(f => inDegree[f.FilePath])
                            .FirstOrDefault();

                        if (lowest != null)
                        {
                            inDegree[lowest.FilePath] = 0;
                            readyQueue.Enqueue(lowest);
                        }
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

        var useFullFileReplacement = fileEntry.Action == FileEditAction.Modify &&
                                     targetContent != null &&
                                     WorktreeEditApplier.IsSmallTextFile(targetContent);
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

        var fileAiRequest = new AiRequest
        {
            Model = request.Model ?? string.Empty,
            SystemPrompt = singleFileSystemPrompt,
            UserPrompt = singleFileUserPrompt,
            MaxTokens = initialBudget
        };

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
        await RecordProviderCallActivityAsync(
            request.ExecutionId,
            fileEntry.FilePath,
            "Generation",
            fileAiRequest,
            fileResponse,
            fileSw.Elapsed,
            cancellationToken).ConfigureAwait(false);

        // Bounded Token Budget Escalation & Compact Retry (max 1 attempt if finish_reason == length)
        if (fileResponse.FailureKind == AiFailureKind.TokenLimitExceeded ||
            string.Equals(fileResponse.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            int compactBudget = DetermineCompactRetryBudget(initialBudget, targetContent, fileEntry, isRepair: false);
            if (compactBudget > initialBudget && callCounter.TryIncrement(out var escCallNumber))
            {
                recoveryUsed = true;
                callCounter.RecordCompactRetry();
                await SafeRecordActivityAsync(
                    request.ExecutionId,
                    $"Performing compact generation retry for {fileName} (budget {initialBudget} -> {compactBudget}) due to token length limit.",
                    cancellationToken).ConfigureAwait(false);

                var compactUserPrompt = BuildCompactSingleFileUserPrompt(
                    request, fileEntry, targetContent, lockedContracts, useFullFileReplacement);
                var compactSystemPrompt = BuildCompactSingleFileSystemPrompt(fileEntry, useFullFileReplacement);

                var compactRequest = new AiRequest
                {
                    Model = request.Model ?? string.Empty,
                    SystemPrompt = compactSystemPrompt,
                    UserPrompt = compactUserPrompt,
                    MaxTokens = compactBudget
                };

                try
                {
                    var escSw = Stopwatch.StartNew();
                    var compactResponse = await _aiProvider.SendAsync(compactRequest, cancellationToken).ConfigureAwait(false);
                    escSw.Stop();

                    if (!string.IsNullOrWhiteSpace(compactResponse.Model))
                    {
                        capturedModels.Add(compactResponse.Model);
                    }

                    LogGenerationAudit(fileEntry.FilePath, "CompactRetry", escCallNumber, compactRequest, compactResponse, escSw.Elapsed, isCompactRetry: true);
                    await RecordProviderCallActivityAsync(
                        request.ExecutionId,
                        fileEntry.FilePath,
                        "CompactGenerationRetry",
                        compactRequest,
                        compactResponse,
                        escSw.Elapsed,
                        cancellationToken).ConfigureAwait(false);

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
                useFullFileReplacement = WorktreeEditApplier.IsSmallTextFile(targetContent);

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
            var repairRequest = new AiRequest
            {
                Model = request.Model ?? string.Empty,
                SystemPrompt = BuildSingleFileRepairSystemPrompt(fileEntry, useFullFileReplacement),
                UserPrompt = repairUserPrompt,
                MaxTokens = repairBudget
            };

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
            await RecordProviderCallActivityAsync(
                request.ExecutionId,
                fileEntry.FilePath,
                "ApplicabilityRepair",
                repairRequest,
                repairResponse,
                repairSw.Elapsed,
                cancellationToken).ConfigureAwait(false);

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
        var durationSec = (int)Math.Max(1, Math.Round(fileGenerationStopwatch.Elapsed.TotalSeconds));
        await SafeRecordActivityAsync(
            request.ExecutionId,
            $"Generated edit {fileIndex}/{totalFiles} · {fileName} · {durationSec}s",
            cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
        var metadata = new ExecutionActivityMetadata(
            EventKind: "ProviderCall",
            LogicalProviderCallCount: 1,
            ProviderCallKind: callKind,
            ProviderAttemptCount: response.AttemptCount,
            RequestedOutputTokens: request.MaxTokens,
            InputTokens: response.InputTokens,
            OutputTokens: response.OutputTokens,
            StageDurationMs: (long)duration.TotalMilliseconds,
            TargetFile: targetFile);

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

    public static string BuildSingleFileSystemPrompt(
        ManifestFileEntry fileEntry,
        bool useFullFileReplacement = false)
    {
        var actionSpecificRule = fileEntry.Action == FileEditAction.Create
            ? "For 'Create' actions, specify 'newContent' containing the complete, valid file content."
            : useFullFileReplacement
                ? "This is a small-file Modify. Return the complete resulting file once in 'newContent'; omit 'searchReplaceEdits'."
                : "This is a large-file Modify. Return only compact 'searchReplaceEdits'; each small exact search anchor must match once. Omit 'newContent'.";

        var isTest = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);
        var testGuidance = isTest
            ? "\n6. MINIMAL TESTS: Implement only the focused tests required by the acceptance criteria and follow existing test conventions."
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
        var actionSpecificRule = fileEntry.Action == FileEditAction.Create
            ? "Provide complete file content in 'newContent'."
            : useFullFileReplacement
                ? "Provide the complete resulting small file once in 'newContent'; omit 'searchReplaceEdits'."
                : "Output was previously truncated because too much code was emitted. Return ONLY minimal 2-5 line 'searchReplaceEdits' targeting specific modified statements. NEVER repeat unchanged methods. Omit 'newContent'.";

        var schema = fileEntry.Action == FileEditAction.Create || useFullFileReplacement
            ? $$"""{"filePath":"{{fileEntry.FilePath}}","action":"{{fileEntry.Action}}","newContent":"complete resulting file"}"""
            : $$"""{"filePath":"{{fileEntry.FilePath}}","action":"Modify","searchReplaceEdits":[{"search":"exact anchor","replace":"replacement"}]}""";

        return $$"""
            Output ONLY the smallest valid JSON object. No markdown, prose, reasoning, or extra fields.
            {{actionSpecificRule}}
            {{schema}}
            """;
    }

    public static string BuildSingleFileRepairSystemPrompt(
        ManifestFileEntry fileEntry,
        bool useFullFileReplacement = false)
    {
        var actionSpecificRule = fileEntry.Action == FileEditAction.Create
            ? "For 'Create' actions, specify 'newContent' containing the complete, valid file content."
            : useFullFileReplacement
                ? "Return the complete resulting small file in 'newContent'; omit 'searchReplaceEdits'."
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

        if (fileEntry.Action == FileEditAction.Modify)
        {
            sb.AppendLine(useFullFileReplacement
                ? "Edit Strategy: hash-guarded small-file replacement. Return complete resulting content in newContent."
                : "Edit Strategy: surgical patch. Return only minimal searchReplaceEdits.");
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
        bool useFullFileReplacement = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== COMPACT RETRY (TOKEN LIMIT DISCIPLINE) ===");
        sb.AppendLine(useFullFileReplacement
            ? "Emit only the JSON small-file replacement payload."
            : "Emit only the minimal JSON searchReplaceEdits payload.");
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
            sb.AppendLine("=== Current Content of Target File ===");
            if (!string.IsNullOrWhiteSpace(targetContent))
            {
                var isTest = ProjectGraphHelper.IsTestFileCandidate(fileEntry.FilePath);
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

        sb.AppendLine($"Output ONLY the compact JSON object for '{fileEntry.FilePath}'.");
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
                throw new FormatException($"Large-file Modify action for '{expectedEntry.FilePath}' must use surgical 'searchReplaceEdits'.");
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
}
