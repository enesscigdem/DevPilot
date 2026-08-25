using System.Diagnostics;
using System.Text;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Orchestrates the generic execution lifecycle inside an isolated Git worktree. Repository and
/// language-specific verification knowledge is supplied through repository-check ports.
/// </summary>
public sealed class GitWorkspaceExecutionProcessor : IExecutionProcessor
{
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IExecutionRepository _executionRepository;
    private readonly IImpactAnalysisRepository _impactAnalysisRepository;
    private readonly IDeveloperAgent _developerAgent;
    private readonly IRepositoryCheckRunner _repositoryCheckRunner;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly IExecutionChangeFingerprintCalculator? _changeFingerprintCalculator;
    private readonly IRepositoryRepairContextProvider? _repairContextProvider;
    private readonly IBaselineVerificationService? _baselineVerificationService;
    private readonly ILogger<GitWorkspaceExecutionProcessor> _logger;
    private readonly int _maxCompileRepairRounds;
    private readonly int _maxTestRepairRounds;

    public GitWorkspaceExecutionProcessor(
        IExecutionWorkspaceManager workspaceManager,
        IExecutionRepository executionRepository,
        IImpactAnalysisRepository impactAnalysisRepository,
        IDeveloperAgent developerAgent,
        IRepositoryCheckRunner repositoryCheckRunner,
        IExecutionActivityRecorder activityRecorder,
        ILogger<GitWorkspaceExecutionProcessor> logger,
        IConfiguration? configuration = null,
        IExecutionChangeFingerprintCalculator? changeFingerprintCalculator = null,
        IRepositoryRepairContextProvider? repairContextProvider = null,
        IBaselineVerificationService? baselineVerificationService = null)
    {
        _workspaceManager = workspaceManager;
        _executionRepository = executionRepository;
        _impactAnalysisRepository = impactAnalysisRepository;
        _developerAgent = developerAgent;
        _repositoryCheckRunner = repositoryCheckRunner;
        _activityRecorder = activityRecorder;
        _changeFingerprintCalculator = changeFingerprintCalculator;
        _repairContextProvider = repairContextProvider;
        _baselineVerificationService = baselineVerificationService;
        _logger = logger;

        _maxCompileRepairRounds = TryGetNonNegativeSetting(
            configuration,
            "ExecutionReliability:MaxCompileRepairRounds",
            "DeveloperAgent:MaxCompileRepairRounds",
            3);
        _maxTestRepairRounds = TryGetNonNegativeSetting(
            configuration,
            "ExecutionReliability:MaxTestRepairRounds",
            "DeveloperAgent:MaxTestRepairRounds",
            2);
    }

    public async Task ProcessAsync(
        ExecutionProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting repository-native execution {ExecutionId} for task {TaskId}.",
            context.ExecutionId,
            context.TaskId);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Workspace,
            ExecutionActivityStatus.Started,
            "Workspace preparation started.",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var prepResult = await _workspaceManager.PrepareWorkspaceAsync(
            context.ExecutionId,
            context.TaskId,
            context.WorkspaceLocalPath,
            sourceBranch: null,
            cancellationToken).ConfigureAwait(false);

        if (!prepResult.Success)
        {
            var error = $"Execution workspace preparation failed: {prepResult.ErrorMessage}";
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Workspace,
                ExecutionActivityStatus.Failed,
                error,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(error);
        }

        var modifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await _executionRepository.UpdateWorkspaceDetailsAsync(
                context.ExecutionId,
                prepResult.WorkspacePath,
                prepResult.BranchName,
                cancellationToken).ConfigureAwait(false);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Workspace,
                ExecutionActivityStatus.Completed,
                "Workspace prepared.",
                new ExecutionActivityMetadata(BranchName: prepResult.BranchName),
                cancellationToken).ConfigureAwait(false);

            var preAiVerification = await _workspaceManager.VerifyWorkspaceStateAsync(
                prepResult.WorkspacePath,
                prepResult.BranchName,
                requireClean: true,
                cancellationToken).ConfigureAwait(false);

            if (!preAiVerification.IsValid)
            {
                var error = $"Developer Agent failed: Execution workspace verification failed prior to AI invocation. {preAiVerification.ErrorMessage}";
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.DeveloperAgent,
                    ExecutionActivityStatus.Failed,
                    error,
                    new ExecutionActivityMetadata(EventKind: "GeneratingChange"),
                    cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(error);
            }

            var profile = await _repositoryCheckRunner.DiscoverAsync(
                new RepositoryPreflightRequest(prepResult.WorkspacePath, prepResult.BranchName),
                cancellationToken).ConfigureAwait(false);

            var requiredChecks = profile.Checks
                .Where(check => check.Required)
                .OrderBy(check => check.Order)
                .ThenBy(check => check.Id, StringComparer.Ordinal)
                .ToList();

            if (requiredChecks.Count == 0)
            {
                var msg = profile.State == RepositoryVerificationState.InfrastructureFailure
                    ? $"Repository verification preflight had infrastructure failure: {profile.Message ?? "Infrastructure failure."} Proceeding with unverified generation."
                    : $"Repository verification is unconfigured: {profile.Message ?? "No trustworthy check was discovered."} Proceeding with unverified generation.";

                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Workspace,
                    ExecutionActivityStatus.Completed,
                    msg,
                    new ExecutionActivityMetadata(
                        EventKind: "RepositoryPreflight",
                        DiscoveredCheckCount: 0,
                        DiscoveredChecks: Array.Empty<string>(),
                        DetectedEcosystems: profile.Ecosystems,
                        VerificationFailureCategory: profile.State == RepositoryVerificationState.InfrastructureFailure ? "InfrastructureFailure" : "Unconfigured",
                        DeterministicCheck: true,
                        VerificationUnresolved: profile.HasUnresolvedVerification,
                        VerificationOutcome: profile.State == RepositoryVerificationState.InfrastructureFailure ? "VerificationInfrastructureError" : "VerificationUnavailable"),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Workspace,
                    ExecutionActivityStatus.Completed,
                    "Repository verification checks discovered.",
                    new ExecutionActivityMetadata(
                        EventKind: "RepositoryPreflight",
                        DiscoveredCheckCount: requiredChecks.Count,
                        DiscoveredChecks: requiredChecks.Select(check => check.Id).ToList(),
                        DiscoveredCheckEvidence: requiredChecks
                            .Where(check => !string.IsNullOrWhiteSpace(check.DiscoveryEvidence))
                            .Select(check => $"{check.Id}: {check.DiscoveryEvidence}")
                            .ToList(),
                        DetectedEcosystems: profile.Ecosystems,
                        DeterministicCheck: true,
                        VerificationUnresolved: profile.HasUnresolvedVerification),
                    cancellationToken).ConfigureAwait(false);
            }

            var analysis = await _impactAnalysisRepository
                .GetLatestByTaskIdAsync(context.TaskId, cancellationToken)
                .ConfigureAwait(false);

            if (analysis is null || analysis.Status != ImpactAnalysisStatus.Completed)
            {
                const string error = "Developer Agent failed: A completed TaskImpactAnalysis is required before running the Developer Agent.";
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.DeveloperAgent,
                    ExecutionActivityStatus.Failed,
                    error,
                    new ExecutionActivityMetadata(EventKind: "GeneratingChange"),
                    cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(error);
            }

            var summary = !string.IsNullOrWhiteSpace(analysis.StructuredResult?.Summary)
                ? analysis.StructuredResult.Summary
                : context.ImpactAnalysisSummary;
            var impactedFiles = analysis.StructuredResult?.ImpactedFiles?
                .Select(file => file.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList() ?? new List<string>();
            var impactedFileDetails = analysis.StructuredResult?.ImpactedFiles?
                .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
                .Select(file => new ImpactedFileDetail(
                    file.FilePath,
                    file.ChangeType.ToString(),
                    file.Reason,
                    file.EvidenceType,
                    file.IsUncertain))
                .ToList() ?? new List<ImpactedFileDetail>();

            var changeDimensions = analysis.StructuredResult?.Dimensions?
                .Select(d => $"{d.Area}: {d.Summary}")
                .Take(5)
                .ToList();

            var expectedChecks = analysis.StructuredResult?.ChangeBrief?.ExpectedChecks?
                .Select(c => c.DisplayName)
                .Take(5)
                .ToList();

            var criticalUnknowns = analysis.StructuredResult?.Unknowns?
                .Take(3)
                .ToList();

            var agentRequest = new DeveloperAgentRequest(
                context.TaskId,
                context.ExecutionId,
                context.TaskTitle,
                context.TaskDescription,
                context.AcceptanceCriteria,
                summary,
                BuildProposedPlanText(analysis),
                impactedFiles,
                prepResult.WorkspacePath,
                prepResult.BranchName,
                impactedFileDetails,
                analysis.Model,
                ChangeDimensions: changeDimensions,
                ExpectedChecks: expectedChecks,
                Unknowns: criticalUnknowns);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.DeveloperAgent,
                ExecutionActivityStatus.Started,
                "Developer Agent started.",
                new ExecutionActivityMetadata(Model: analysis.Model, EventKind: "GeneratingChange"),
                cancellationToken).ConfigureAwait(false);

            var agentResult = await _developerAgent.GenerateAndApplyEditsAsync(agentRequest, cancellationToken).ConfigureAwait(false);
            var actualModel = agentResult.Model ?? analysis.Model;
            if (!string.IsNullOrWhiteSpace(actualModel))
            {
                await _executionRepository.SetModelAsync(context.ExecutionId, actualModel, cancellationToken).ConfigureAwait(false);
            }

            if (!agentResult.Success)
            {
                var error = $"Developer Agent failed: {agentResult.ErrorMessage ?? "Developer Agent failed to generate or apply edits."}";
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.DeveloperAgent,
                    ExecutionActivityStatus.Failed,
                    error,
                    new ExecutionActivityMetadata(Model: actualModel, EventKind: "GeneratingChange"),
                    cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(error);
            }

            if (agentResult.ModifiedFiles == null || agentResult.ModifiedFiles.Count == 0)
            {
                if (agentResult.HasResolvedNoChange)
                {
                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.DeveloperAgent,
                        ExecutionActivityStatus.Completed,
                        "Developer Agent completed.",
                        new ExecutionActivityMetadata(
                            ModifiedFileCount: 0,
                            Model: actualModel,
                            EventKind: "GeneratingChange",
                            ResolvedNoChangeCount: agentResult.ResolvedNoChangeFiles!.Count),
                        cancellationToken).ConfigureAwait(false);

                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Execution,
                        ExecutionActivityStatus.Completed,
                        "No code changes were required by the generated plan.",
                        new ExecutionActivityMetadata(
                            Model: actualModel,
                            EventKind: "ReadyForReview",
                            VerificationOutcome: "NeedsReview",
                            ResolvedNoChangeCount: agentResult.ResolvedNoChangeFiles.Count),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                const string error = "Developer Agent failed: Developer Agent returned success but produced zero modified files.";
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.DeveloperAgent,
                    ExecutionActivityStatus.Failed,
                    error,
                    new ExecutionActivityMetadata(Model: actualModel, EventKind: "GeneratingChange"),
                    cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(error);
            }

            foreach (var file in agentResult.ModifiedFiles)
            {
                modifiedFiles.Add(file);
            }

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.DeveloperAgent,
                ExecutionActivityStatus.Completed,
                "Developer Agent completed.",
                new ExecutionActivityMetadata(
                    ModifiedFileCount: agentResult.ModifiedFiles.Count,
                    Model: actualModel,
                    EventKind: "GeneratingChange"),
                cancellationToken).ConfigureAwait(false);

            var prerequisiteChecks = requiredChecks.Where(check => check.Kind != RepositoryCheckKind.Test).ToList();
            var testChecks = requiredChecks.Where(check => check.Kind == RepositoryCheckKind.Test).ToList();

            if (requiredChecks.Count == 0)
            {
                var outcome = profile.State == RepositoryVerificationState.InfrastructureFailure
                    ? "VerificationInfrastructureError"
                    : "VerificationUnavailable";
                var verificationSummary = profile.State == RepositoryVerificationState.InfrastructureFailure
                    ? "Verification infrastructure error: preflight discovery encountered infrastructure failure; diff is preserved for review."
                    : "Verification unavailable: no trustworthy verification checks discovered for repository.";

                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Execution,
                    ExecutionActivityStatus.Completed,
                    verificationSummary,
                    new ExecutionActivityMetadata(
                        EventKind: "ReadyForReview",
                        VerificationOutcome: outcome),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var allPrerequisitesPassed = true;
            foreach (var check in prerequisiteChecks)
            {
                var passed = await RunPrerequisiteCheckAsync(
                    context,
                    prepResult,
                    analysis,
                    actualModel,
                    check,
                    modifiedFiles,
                    cancellationToken).ConfigureAwait(false);

                if (!passed)
                {
                    allPrerequisitesPassed = false;
                    break;
                }
            }

            if (!allPrerequisitesPassed)
            {
                return;
            }

            var confirmedBuild = prerequisiteChecks.Any(check => check.Kind == RepositoryCheckKind.Build);
            var allTestsPassed = true;
            for (var index = 0; index < testChecks.Count; index++)
            {
                var check = testChecks[index];
                var passed = await RunTestCheckAsync(
                    context,
                    prepResult,
                    analysis,
                    actualModel,
                    check,
                    prerequisiteChecks,
                    confirmedBuild,
                    index == testChecks.Count - 1,
                    modifiedFiles,
                    cancellationToken).ConfigureAwait(false);

                if (!passed)
                {
                    allTestsPassed = false;
                    break;
                }
            }

            if (!allTestsPassed)
            {
                return;
            }

            if (testChecks.Count == 0)
            {
                var hasBuild = prerequisiteChecks.Any(check => check.Kind == RepositoryCheckKind.Build);
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Completed,
                    hasBuild
                        ? "Repository build passed (partially verified: no test suite discovered)."
                        : "Repository checks passed.",
                    new ExecutionActivityMetadata(
                        BuildPassed: hasBuild ? true : null,
                        EventKind: "ReadyForReview",
                        VerificationOutcome: hasBuild ? "PartiallyVerified" : "Verified"),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // GUARANTEED CLEANUP: Purge verification side-effects (bin/obj/cache/test outputs)
            // before ANY terminal result (success, build failure, test failure, repair failure, no-progress, cancellation)
            // while strictly preserving authoritative files (initial edits + repair edits).
            if (prepResult != null && !string.IsNullOrWhiteSpace(prepResult.WorkspacePath))
            {
                try
                {
                    var purged = await VerificationSideEffectCleaner.PurgeSideEffectsAsync(
                        prepResult.WorkspacePath,
                        modifiedFiles,
                        CancellationToken.None).ConfigureAwait(false);

                    if (purged.Count > 0)
                    {
                        _logger.LogInformation(
                            "Purged {Count} verification side-effect artifact(s) from execution workspace {WorkspacePath}.",
                            purged.Count,
                            prepResult.WorkspacePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to purge verification side-effects in finally block for execution {ExecutionId}", context.ExecutionId);
                }
            }
        }
    }

    private async Task<bool> RunPrerequisiteCheckAsync(
        ExecutionProcessingContext context,
        ExecutionWorkspaceResult prepResult,
        TaskImpactAnalysis analysis,
        string? actualModel,
        RepositoryCheck check,
        HashSet<string> modifiedFiles,
        CancellationToken cancellationToken)
    {
        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Started,
            check.Kind == RepositoryCheckKind.Build ? "Build started." : $"{check.DisplayName} started.",
            CheckMetadata(check, eventKind: "VerifyingRepository"),
            cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var result = await _repositoryCheckRunner.ExecuteAsync(
            new RepositoryCheckExecutionRequest(prepResult.WorkspacePath, prepResult.BranchName, check),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (result.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure)
        {
            await RecordInfrastructureFailureAsync(context.ExecutionId, ExecutionStage.Build, check, result, cancellationToken).ConfigureAwait(false);
            return false;
        }

        BaselineFailureComparison? baseComparison = null;
        if (!result.Success && _baselineVerificationService != null && !string.IsNullOrWhiteSpace(prepResult.BaseCommitSha))
        {
            baseComparison = await _baselineVerificationService.EvaluateCompilerFailureAsync(
                prepResult.WorkspacePath,
                context.WorkspaceLocalPath,
                prepResult.BaseCommitSha,
                check,
                result,
                cancellationToken).ConfigureAwait(false);

            if (baseComparison.Classification == BaselineFailureClassification.PreExisting)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Completed,
                    $"No new regressions: {baseComparison.PreExistingCount} pre-existing repository failure(s) remain.",
                    CheckMetadata(
                        check,
                        "VerifyingRepository",
                        result,
                        buildPassed: true,
                        baselineClassification: "PreExisting",
                        verificationOutcome: "NoNewRegressions",
                        preExistingFailureCount: baseComparison.PreExistingCount,
                        newRegressionCount: 0,
                        baseCommitSha: prepResult.BaseCommitSha,
                        baselineCacheHit: baseComparison.CacheHit,
                        stageDurationMs: stopwatch.ElapsedMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (baseComparison.Classification == BaselineFailureClassification.Unknown)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Failed,
                    $"Needs review: baseline comparison was inconclusive ({baseComparison.Summary}).",
                    CheckMetadata(
                        check,
                        "StoppedWithEvidence",
                        result,
                        buildPassed: false,
                        baselineClassification: "Unknown",
                        verificationOutcome: "NeedsReview",
                        baseCommitSha: prepResult.BaseCommitSha,
                        baselineCacheHit: baseComparison.CacheHit,
                        stageDurationMs: stopwatch.ElapsedMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        var repairRound = 0;
        string? previousFailureFingerprint = null;

        while (!result.Success && repairRound < _maxCompileRepairRounds)
        {
            repairRound++;
            var actionableFailures = baseComparison != null && (baseComparison.Classification == BaselineFailureClassification.NewRegression || baseComparison.Classification == BaselineFailureClassification.ChangedRegression)
                ? baseComparison.NewRegressions.Concat(baseComparison.ChangedFailures).ToList()
                : null;

            var evidence = (actionableFailures != null && actionableFailures.Count > 0)
                ? ExecutionDiagnosticEvidence.CreateActionableCompilerEvidence(actionableFailures, result.StdOut, result.StdErr, result.ErrorMessage)
                : ExecutionDiagnosticEvidence.ParseVerificationFailure(result.StdOut, result.StdErr, result.ErrorMessage);

            if (string.Equals(previousFailureFingerprint, evidence.FailureFingerprint, StringComparison.Ordinal))
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Failed,
                    "Stopped with evidence: focused repair made no diagnostic progress.",
                    CheckMetadata(
                        check,
                        "StoppedWithEvidence",
                        result,
                        repairKind: "Compile",
                        repairRound: repairRound,
                        failureFingerprint: evidence.FailureFingerprint,
                        progressResult: "SameFailure"),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            var touchedSources = ExecutionDiagnosticEvidence.LoadTouchedSourceSnapshots(
                prepResult.WorkspacePath,
                modifiedFiles);
            var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
                evidence,
                modifiedFiles,
                attemptedForCurrentFailureSet: null,
                touchedSources);
            var repairFiles = string.IsNullOrWhiteSpace(selection.FilePath)
                ? new List<string>()
                : new List<string> { selection.FilePath };
            var scopedDiagnostics = repairFiles.Count == 1
                ? ExecutionDiagnosticEvidence.ScopeToFile(evidence, repairFiles[0])
                : null;
            var repairDiagnosticLines = scopedDiagnostics != null && scopedDiagnostics.DiagnosticLines.Count > 0
                ? scopedDiagnostics.DiagnosticLines
                : evidence.DiagnosticLines;
            var repairDiagnosticLocations = scopedDiagnostics != null && scopedDiagnostics.Locations.Count > 0
                ? scopedDiagnostics.Locations
                : evidence.Locations;
            var sanitizedDiagnosticLines = ExecutionDiagnosticEvidence.SanitizeDiagnosticLinesForActivity(
                repairDiagnosticLines,
                prepResult.WorkspacePath);

            if (repairFiles.Count == 0)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Failed,
                    "Stopped with evidence: verification diagnostics could not be correlated to a touched file.",
                    CheckMetadata(
                        check,
                        "StoppedWithEvidence",
                        result,
                        repairKind: "Compile",
                        repairRound: repairRound,
                        failureFingerprint: evidence.FailureFingerprint,
                        progressResult: "Uncorrelated",
                        diagnosticLines: sanitizedDiagnosticLines),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Started,
                check.Kind == RepositoryCheckKind.Build
                    ? "Compile repair started."
                    : $"Focused verification repair started (round {repairRound}/{_maxCompileRepairRounds}).",
                CheckMetadata(
                    check,
                    "FixingBuildIssue",
                    result,
                    repairKind: "Compile",
                    repairRound: repairRound,
                    repairFiles: repairFiles,
                    failureFingerprint: evidence.FailureFingerprint,
                    diagnosticLines: sanitizedDiagnosticLines,
                    repairSelectionReason: selection.Reason),
                cancellationToken).ConfigureAwait(false);

            if (check.Kind == RepositoryCheckKind.Build)
            {
                var repairSummary = new StringBuilder()
                    .AppendLine($"Build failed — {evidence.DiagnosticLines.Count} compiler error(s)");
                foreach (var diagnostic in sanitizedDiagnosticLines)
                {
                    repairSummary.AppendLine(diagnostic);
                }
                repairSummary.AppendLine($"Repair round {repairRound}/{_maxCompileRepairRounds}");
                repairSummary.AppendLine("Repairing:");
                foreach (var file in repairFiles)
                {
                    repairSummary.AppendLine($"- {Path.GetFileName(file)}");
                }

                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Started,
                    repairSummary.ToString().TrimEnd(),
                    CheckMetadata(
                        check,
                        "FixingBuildIssue",
                        result,
                        repairKind: "Compile",
                        repairRound: repairRound,
                        repairFiles: repairFiles,
                        failureFingerprint: evidence.FailureFingerprint,
                        diagnosticLines: sanitizedDiagnosticLines),
                    cancellationToken).ConfigureAwait(false);
            }

            var languageContext = _repairContextProvider?.GetCompileRepairContext(check, prepResult.WorkspacePath, repairFiles);
            var repairRequest = new FocusedRepairRequest(
                TaskId: context.TaskId,
                ExecutionId: context.ExecutionId,
                TaskTitle: context.TaskTitle,
                AcceptanceCriteria: "Resolve the authoritative repository check failure in the focused files without weakening existing tests or checks.",
                WorkspacePath: prepResult.WorkspacePath,
                BranchName: prepResult.BranchName,
                RepairFiles: repairFiles,
                DiagnosticEvidence: string.Join("\n", repairDiagnosticLines.Take(10)),
                DiagnosticLocations: repairDiagnosticLocations.Select(l => $"{l.FilePath}:{l.Line}:{l.Column}").ToList(),
                LanguageContext: languageContext,
                Model: actualModel);

            var beforeFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var repairStopwatch = Stopwatch.StartNew();
            DeveloperAgentResult repairResult;
            try
            {
                repairResult = await _developerAgent.ExecuteFocusedRepairAsync(repairRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                repairStopwatch.Stop();
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Failed,
                    $"Focused verification repair failed with exception: {ex.Message}",
                    CheckMetadata(check, "StoppedWithEvidence", result, repairKind: "Compile", repairRound: repairRound),
                    cancellationToken).ConfigureAwait(false);
                break;
            }
            repairStopwatch.Stop();

            if (!repairResult.Success)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Failed,
                    $"Focused verification repair failed: {repairResult.ErrorMessage}",
                    CheckMetadata(check, "StoppedWithEvidence", result, repairKind: "Compile", repairRound: repairRound),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            foreach (var file in repairResult.ModifiedFiles ?? Array.Empty<string>())
            {
                modifiedFiles.Add(file);
            }

            var afterFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var noDiff = beforeFingerprint != null && afterFingerprint != null &&
                         string.Equals(beforeFingerprint, afterFingerprint, StringComparison.Ordinal);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                noDiff ? ExecutionActivityStatus.Failed : ExecutionActivityStatus.Completed,
                noDiff
                    ? "Stopped with evidence: focused repair produced no worktree change."
                    : check.Kind == RepositoryCheckKind.Build
                        ? repairRound == 1 ? "Compile repair completed." : $"Compile repair completed (round {repairRound})."
                        : "Focused verification repair applied.",
                CheckMetadata(
                    check,
                    noDiff ? "StoppedWithEvidence" : "FixingBuildIssue",
                    result,
                    repairKind: "Compile",
                    repairRound: repairRound,
                    repairFiles: repairFiles,
                    failureFingerprint: evidence.FailureFingerprint,
                    beforeFingerprint: beforeFingerprint,
                    afterFingerprint: afterFingerprint,
                    progressResult: noDiff ? "NoDiff" : "Changed",
                    stageDurationMs: repairStopwatch.ElapsedMilliseconds),
                cancellationToken).ConfigureAwait(false);

            if (noDiff)
            {
                break;
            }

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Started,
                check.Kind == RepositoryCheckKind.Build
                    ? repairRound == 1 ? "Build retry started." : $"Build retry started (round {repairRound})."
                    : $"{check.DisplayName} retry started (round {repairRound}).",
                CheckMetadata(check, "VerifyingRepository", repairKind: "Compile", repairRound: repairRound),
                cancellationToken).ConfigureAwait(false);

            result = await _repositoryCheckRunner.ExecuteAsync(
                new RepositoryCheckExecutionRequest(prepResult.WorkspacePath, prepResult.BranchName, check),
                cancellationToken).ConfigureAwait(false);
            previousFailureFingerprint = evidence.FailureFingerprint;

            if (result.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure)
            {
                await RecordInfrastructureFailureAsync(context.ExecutionId, ExecutionStage.Build, check, result, cancellationToken).ConfigureAwait(false);
                return false;
            }

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                result.Success ? ExecutionActivityStatus.Completed : ExecutionActivityStatus.Failed,
                check.Kind == RepositoryCheckKind.Build
                    ? result.Success
                        ? "Build retry passed."
                        : repairRound == 1 ? "Build retry failed." : $"Build retry failed (round {repairRound})."
                    : result.Success
                        ? $"{check.DisplayName} retry passed."
                        : $"{check.DisplayName} retry failed (round {repairRound}).",
                CheckMetadata(check, "VerifyingRepository", result, repairKind: "Compile", repairRound: repairRound),
                cancellationToken).ConfigureAwait(false);
        }

        if (!result.Success)
        {
            var prefix = check.Kind == RepositoryCheckKind.Build ? "Build validation failed" : $"{check.DisplayName} validation failed";
            var error = $"{prefix}: {result.ErrorMessage ?? "Repository check failed."}";
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Failed,
                error,
                CheckMetadata(
                    check,
                    "StoppedWithEvidence",
                    result,
                    buildPassed: check.Kind == RepositoryCheckKind.Build ? false : null,
                    verificationOutcome: "NeedsReview",
                    newRegressionCount: baseComparison?.NewRegressionCount ?? 0,
                    preExistingFailureCount: baseComparison?.PreExistingCount ?? 0),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Completed,
            check.Kind == RepositoryCheckKind.Build ? "Build passed." : $"{check.DisplayName} passed.",
            CheckMetadata(
                check,
                "VerifyingRepository",
                result,
                buildPassed: check.Kind == RepositoryCheckKind.Build ? true : null,
                verificationOutcome: "Verified",
                stageDurationMs: stopwatch.ElapsedMilliseconds),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RunTestCheckAsync(
        ExecutionProcessingContext context,
        ExecutionWorkspaceResult prepResult,
        TaskImpactAnalysis analysis,
        string? actualModel,
        RepositoryCheck check,
        IReadOnlyList<RepositoryCheck> prerequisiteChecks,
        bool confirmedBuild,
        bool isFinalTest,
        HashSet<string> modifiedFiles,
        CancellationToken cancellationToken)
    {
        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Test,
            ExecutionActivityStatus.Started,
            "Test started.",
            CheckMetadata(check, "VerifyingRepository"),
            cancellationToken).ConfigureAwait(false);

        var useConfirmedBuild = confirmedBuild && check.SupportsSkipBuild;
        var fullRequest = new RepositoryCheckExecutionRequest(
            prepResult.WorkspacePath,
            prepResult.BranchName,
            check,
            SkipBuild: useConfirmedBuild);
        var stopwatch = Stopwatch.StartNew();
        var result = await _repositoryCheckRunner.ExecuteAsync(fullRequest, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (result.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure)
        {
            await RecordInfrastructureFailureAsync(context.ExecutionId, ExecutionStage.Test, check, result, cancellationToken).ConfigureAwait(false);
            return false;
        }

        BaselineFailureComparison? baseComparison = null;
        if (!result.Success && _baselineVerificationService != null && !string.IsNullOrWhiteSpace(prepResult.BaseCommitSha))
        {
            baseComparison = await _baselineVerificationService.EvaluateTestFailureAsync(
                prepResult.WorkspacePath,
                context.WorkspaceLocalPath,
                prepResult.BaseCommitSha,
                check,
                result,
                cancellationToken).ConfigureAwait(false);

            if (baseComparison.Classification == BaselineFailureClassification.PreExisting)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Completed,
                    isFinalTest
                        ? $"No new regressions: {baseComparison.PreExistingCount} pre-existing repository failure(s) remain."
                        : $"{check.DisplayName}: No new regressions ({baseComparison.PreExistingCount} pre-existing failure(s) remain).",
                    CheckMetadata(
                        check,
                        isFinalTest ? "ReadyForReview" : "VerifyingRepository",
                        result,
                        testPassed: true,
                        baselineClassification: "PreExisting",
                        verificationOutcome: "NoNewRegressions",
                        preExistingFailureCount: baseComparison.PreExistingCount,
                        newRegressionCount: 0,
                        baseCommitSha: prepResult.BaseCommitSha,
                        baselineCacheHit: baseComparison.CacheHit,
                        stageDurationMs: stopwatch.ElapsedMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (baseComparison.Classification == BaselineFailureClassification.Unknown)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Failed,
                    $"Needs review: baseline comparison was inconclusive ({baseComparison.Summary}).",
                    CheckMetadata(
                        check,
                        "StoppedWithEvidence",
                        result,
                        testPassed: false,
                        baselineClassification: "Unknown",
                        verificationOutcome: "NeedsReview",
                        baseCommitSha: prepResult.BaseCommitSha,
                        baselineCacheHit: baseComparison.CacheHit,
                        stageDurationMs: stopwatch.ElapsedMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        var repairRound = 0;
        string? previousFailureFingerprint = null;
        while (!result.Success && repairRound < _maxTestRepairRounds)
        {
            repairRound++;
            var actionableFailures = baseComparison != null && (baseComparison.Classification == BaselineFailureClassification.NewRegression || baseComparison.Classification == BaselineFailureClassification.ChangedRegression)
                ? baseComparison.NewRegressions.Concat(baseComparison.ChangedFailures).ToList()
                : null;

            var evidence = (actionableFailures != null && actionableFailures.Count > 0)
                ? ExecutionDiagnosticEvidence.CreateActionableTestEvidence(actionableFailures, result.StdOut, result.StdErr, result.ErrorMessage)
                : ExecutionDiagnosticEvidence.ParseTestFailure(result.StdOut, result.StdErr, result.ErrorMessage);

            if (string.Equals(previousFailureFingerprint, evidence.FailureFingerprint, StringComparison.Ordinal))
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Failed,
                    "Stopped with evidence: focused test repair made no diagnostic progress.",
                    CheckMetadata(
                        check,
                        "StoppedWithEvidence",
                        result,
                        repairKind: "Test",
                        repairRound: repairRound,
                        failureFingerprint: evidence.FailureFingerprint,
                        progressResult: "SameFailure"),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            var selection = ExecutionDiagnosticEvidence.SelectTestRepairTarget(
                evidence,
                modifiedFiles,
                prepResult.WorkspacePath);
            var repairFiles = selection.FilePaths.ToList();
            var sanitizedTestEvidence = ExecutionDiagnosticEvidence.SanitizeTestEvidenceForActivity(evidence);
            if (repairFiles.Count == 0)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Failed,
                    "Stopped with evidence: failing test could not be correlated to a touched file.",
                    CheckMetadata(
                        check,
                        "StoppedWithEvidence",
                        result,
                        repairKind: "Test",
                        repairRound: repairRound,
                        failureFingerprint: evidence.FailureFingerprint,
                        progressResult: "Uncorrelated",
                        verificationOutcome: "NeedsReview",
                        diagnosticLines: sanitizedTestEvidence,
                        repairSelectionReason: selection.Reason,
                        testName: evidence.TestName),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                ExecutionActivityStatus.Started,
                $"Test repair started (round {repairRound}/{_maxTestRepairRounds}).",
                CheckMetadata(
                    check,
                    "FixingFailingTest",
                    result,
                    repairKind: "Test",
                    repairRound: repairRound,
                    repairFiles: repairFiles,
                    failureFingerprint: evidence.FailureFingerprint,
                    diagnosticLines: sanitizedTestEvidence,
                    repairSelectionReason: selection.Reason,
                    testName: evidence.TestName),
                cancellationToken).ConfigureAwait(false);

            var repairRequest = new FocusedRepairRequest(
                TaskId: context.TaskId,
                ExecutionId: context.ExecutionId,
                TaskTitle: context.TaskTitle,
                AcceptanceCriteria: "Resolve the failing test without weakening existing test assertions.",
                WorkspacePath: prepResult.WorkspacePath,
                BranchName: prepResult.BranchName,
                RepairFiles: repairFiles,
                DiagnosticEvidence: string.Join("\n", evidence.RelevantLines),
                DiagnosticLocations: evidence.Locations.Select(l => $"{l.FilePath}:{l.Line}:{l.Column}").ToList(),
                LanguageContext: null,
                Model: actualModel,
                TouchedFiles: modifiedFiles.ToList(),
                TestName: evidence.TestName);

            var beforeFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var repairStopwatch = Stopwatch.StartNew();
            DeveloperAgentResult repairResult;
            try
            {
                repairResult = await _developerAgent.ExecuteFocusedRepairAsync(repairRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                repairStopwatch.Stop();
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Failed,
                    $"Test repair failed with exception: {ex.Message}",
                    CheckMetadata(check, "StoppedWithEvidence", result, repairKind: "Test", repairRound: repairRound),
                    cancellationToken).ConfigureAwait(false);
                break;
            }
            repairStopwatch.Stop();

            if (!repairResult.Success)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Failed,
                    $"Test repair failed: {repairResult.ErrorMessage}",
                    CheckMetadata(check, "StoppedWithEvidence", result, repairKind: "Test", repairRound: repairRound),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            var repairedThisRound = (repairResult.ModifiedFiles ?? Array.Empty<string>()).ToList();
            foreach (var file in repairedThisRound)
            {
                modifiedFiles.Add(file);
            }

            var afterFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var noDiff = beforeFingerprint != null && afterFingerprint != null &&
                         string.Equals(beforeFingerprint, afterFingerprint, StringComparison.Ordinal);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                noDiff ? ExecutionActivityStatus.Failed : ExecutionActivityStatus.Completed,
                noDiff
                    ? "Stopped with evidence: test repair produced no worktree change."
                    : $"Test repair completed (round {repairRound}).",
                CheckMetadata(
                    check,
                    noDiff ? "StoppedWithEvidence" : "FixingFailingTest",
                    result,
                    repairKind: "Test",
                    repairRound: repairRound,
                    repairFiles: repairFiles,
                    failureFingerprint: evidence.FailureFingerprint,
                    beforeFingerprint: beforeFingerprint,
                    afterFingerprint: afterFingerprint,
                    progressResult: noDiff ? "NoDiff" : "Changed",
                    stageDurationMs: repairStopwatch.ElapsedMilliseconds),
                cancellationToken).ConfigureAwait(false);

            if (noDiff)
            {
                break;
            }

            var prerequisiteFailed = false;
            foreach (var prerequisite in prerequisiteChecks)
            {
                var prerequisiteResult = await _repositoryCheckRunner.ExecuteAsync(
                    new RepositoryCheckExecutionRequest(prepResult.WorkspacePath, prepResult.BranchName, prerequisite),
                    cancellationToken).ConfigureAwait(false);

                if (prerequisiteResult.Success)
                {
                    continue;
                }

                prerequisiteFailed = true;
                var isInfra = prerequisiteResult.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure;
                if (isInfra)
                {
                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Build,
                        ExecutionActivityStatus.Failed,
                        "Repository check infrastructure failure after test repair.",
                        CheckMetadata(
                            prerequisite,
                            "StoppedWithEvidence",
                            prerequisiteResult,
                            repairKind: "Compile",
                            repairRound: repairRound,
                            progressResult: "NewBuildFailure",
                            verificationOutcome: "VerificationInfrastructureError"),
                        cancellationToken).ConfigureAwait(false);
                    return false;
                }

                var bridged = await TryCompilerConvergenceBridgeAfterTestRepairAsync(
                    context,
                    prepResult,
                    actualModel,
                    prerequisite,
                    prerequisiteResult,
                    modifiedFiles,
                    repairedThisRound,
                    repairRound,
                    cancellationToken).ConfigureAwait(false);
                if (!bridged)
                {
                    return false;
                }

                prerequisiteFailed = false;
                break;
            }

            if (prerequisiteFailed)
            {
                return false;
            }

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                ExecutionActivityStatus.Started,
                $"Test retry started (round {repairRound}).",
                CheckMetadata(check, "VerifyingRepository", repairKind: "Test", repairRound: repairRound),
                cancellationToken).ConfigureAwait(false);

            if (check.SupportsTargetedTest && evidence.HasReliableTestName)
            {
                var targetedRequest = fullRequest with { TestFilter = evidence.TestName };
                result = await _repositoryCheckRunner.ExecuteAsync(targetedRequest, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Test,
                        ExecutionActivityStatus.Completed,
                        "Targeted failing test passed; running full test suite.",
                        CheckMetadata(
                            check,
                            "VerifyingRepository",
                            result,
                            repairKind: "Test",
                            repairRound: repairRound,
                            failureFingerprint: evidence.FailureFingerprint,
                            progressResult: "TargetPassed"),
                        cancellationToken).ConfigureAwait(false);
                    result = await _repositoryCheckRunner.ExecuteAsync(fullRequest, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                result = await _repositoryCheckRunner.ExecuteAsync(fullRequest, cancellationToken).ConfigureAwait(false);
            }

            previousFailureFingerprint = evidence.FailureFingerprint;
            if (result.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure)
            {
                await RecordInfrastructureFailureAsync(context.ExecutionId, ExecutionStage.Test, check, result, cancellationToken).ConfigureAwait(false);
                return false;
            }

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                result.Success ? ExecutionActivityStatus.Completed : ExecutionActivityStatus.Failed,
                result.Success ? "Test retry passed." : $"Test retry failed (round {repairRound}).",
                CheckMetadata(check, "VerifyingRepository", result, repairKind: "Test", repairRound: repairRound),
                cancellationToken).ConfigureAwait(false);
        }

        if (!result.Success)
        {
            var error = $"Test validation failed: {result.ErrorMessage ?? "Repository test check failed."}";
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                ExecutionActivityStatus.Failed,
                error,
                CheckMetadata(
                    check,
                    "StoppedWithEvidence",
                    result,
                    testPassed: false,
                    verificationOutcome: "NeedsReview",
                    newRegressionCount: baseComparison?.NewRegressionCount ?? 0,
                    preExistingFailureCount: baseComparison?.PreExistingCount ?? 0),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Test,
            ExecutionActivityStatus.Completed,
            isFinalTest ? "Tests passed." : $"{check.DisplayName} passed.",
            CheckMetadata(
                check,
                isFinalTest ? "ReadyForReview" : "VerifyingRepository",
                result,
                testPassed: true,
                verificationOutcome: "Verified",
                stageDurationMs: stopwatch.ElapsedMilliseconds),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RecordInfrastructureFailureAsync(
        Guid executionId,
        ExecutionStage stage,
        RepositoryCheck check,
        RepositoryCheckResult result,
        CancellationToken cancellationToken)
    {
        await SafeRecordActivityAsync(
            executionId,
            stage,
            ExecutionActivityStatus.Failed,
            $"Repository check infrastructure failure: {result.ErrorMessage}",
            CheckMetadata(
                check,
                "StoppedWithEvidence",
                result,
                verificationOutcome: "VerificationInfrastructureError"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryCompilerConvergenceBridgeAfterTestRepairAsync(
        ExecutionProcessingContext context,
        ExecutionWorkspaceResult prepResult,
        string? actualModel,
        RepositoryCheck prerequisite,
        RepositoryCheckResult prerequisiteResult,
        HashSet<string> modifiedFiles,
        IReadOnlyList<string> repairedThisRound,
        int testRepairRound,
        CancellationToken cancellationToken)
    {
        var evidence = ExecutionDiagnosticEvidence.ParseVerificationFailure(
            prerequisiteResult.StdOut,
            prerequisiteResult.StdErr,
            prerequisiteResult.ErrorMessage);
        var implicated = ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(evidence, modifiedFiles).ToList();
        var repairedImplicated = implicated
            .Where(path => repairedThisRound.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? target = null;
        var reason = "Uncorrelated";
        if (repairedImplicated.Count == 1)
        {
            target = repairedImplicated[0];
            reason = "RepairedFileFromCompiler";
        }
        else if (implicated.Count == 1)
        {
            target = implicated[0];
            reason = "TouchedFileFromCompiler";
        }

        var sanitized = ExecutionDiagnosticEvidence.SanitizeDiagnosticLinesForActivity(
            evidence.DiagnosticLines,
            prepResult.WorkspacePath);

        if (string.IsNullOrWhiteSpace(target))
        {
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Failed,
                "Stopped with evidence: repository check failed after focused test repair.",
                CheckMetadata(
                    prerequisite,
                    "StoppedWithEvidence",
                    prerequisiteResult,
                    repairKind: "Compile",
                    repairRound: testRepairRound,
                    failureFingerprint: evidence.FailureFingerprint,
                    progressResult: "NewBuildFailure",
                    verificationOutcome: "NeedsReview",
                    diagnosticLines: sanitized,
                    repairSelectionReason: reason),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        var scoped = ExecutionDiagnosticEvidence.ScopeToFile(evidence, target);
        var diagnosticLines = scoped.DiagnosticLines.Count > 0 ? scoped.DiagnosticLines : evidence.DiagnosticLines;
        var diagnosticLocations = scoped.Locations.Count > 0 ? scoped.Locations : evidence.Locations;
        var languageContext = _repairContextProvider?.GetCompileRepairContext(
            prerequisite,
            prepResult.WorkspacePath,
            new[] { target });

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Started,
            "Compile repair started after test repair introduced a localized build failure.",
            CheckMetadata(
                prerequisite,
                "FixingBuildIssue",
                prerequisiteResult,
                repairKind: "Compile",
                repairRound: testRepairRound,
                repairFiles: new[] { target },
                failureFingerprint: evidence.FailureFingerprint,
                progressResult: "CompilerConvergenceBridge",
                diagnosticLines: sanitized,
                repairSelectionReason: reason),
            cancellationToken).ConfigureAwait(false);

        var beforeFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
        DeveloperAgentResult repairResult;
        try
        {
            repairResult = await _developerAgent.ExecuteFocusedRepairAsync(
                new FocusedRepairRequest(
                    TaskId: context.TaskId,
                    ExecutionId: context.ExecutionId,
                    TaskTitle: context.TaskTitle,
                    AcceptanceCriteria: "Resolve the compiler failure introduced by the previous focused test repair without weakening existing tests.",
                    WorkspacePath: prepResult.WorkspacePath,
                    BranchName: prepResult.BranchName,
                    RepairFiles: new[] { target },
                    DiagnosticEvidence: string.Join("\n", diagnosticLines.Take(10)),
                    DiagnosticLocations: diagnosticLocations.Select(l => $"{l.FilePath}:{l.Line}:{l.Column}").ToList(),
                    LanguageContext: languageContext,
                    Model: actualModel),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Failed,
                $"Compile convergence repair failed with exception: {ex.Message}",
                CheckMetadata(
                    prerequisite,
                    "StoppedWithEvidence",
                    prerequisiteResult,
                    repairKind: "Compile",
                    repairRound: testRepairRound,
                    progressResult: "NewBuildFailure",
                    verificationOutcome: "NeedsReview"),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!repairResult.Success)
        {
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Failed,
                $"Compile convergence repair failed: {repairResult.ErrorMessage}",
                CheckMetadata(
                    prerequisite,
                    "StoppedWithEvidence",
                    prerequisiteResult,
                    repairKind: "Compile",
                    repairRound: testRepairRound,
                    progressResult: "NewBuildFailure",
                    verificationOutcome: "NeedsReview",
                    diagnosticLines: sanitized,
                    repairSelectionReason: reason),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        foreach (var file in repairResult.ModifiedFiles ?? Array.Empty<string>())
        {
            modifiedFiles.Add(file);
        }

        var afterFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
        if (beforeFingerprint != null && afterFingerprint != null &&
            string.Equals(beforeFingerprint, afterFingerprint, StringComparison.Ordinal))
        {
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Failed,
                "Stopped with evidence: compile convergence repair produced no worktree change.",
                CheckMetadata(
                    prerequisite,
                    "StoppedWithEvidence",
                    prerequisiteResult,
                    repairKind: "Compile",
                    repairRound: testRepairRound,
                    repairFiles: new[] { target },
                    progressResult: "NoDiff",
                    verificationOutcome: "NeedsReview"),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        var rebuild = await _repositoryCheckRunner.ExecuteAsync(
            new RepositoryCheckExecutionRequest(prepResult.WorkspacePath, prepResult.BranchName, prerequisite),
            cancellationToken).ConfigureAwait(false);

        if (!rebuild.Success)
        {
            var rebuildEvidence = ExecutionDiagnosticEvidence.ParseVerificationFailure(
                rebuild.StdOut,
                rebuild.StdErr,
                rebuild.ErrorMessage);
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Failed,
                "Stopped with evidence: repository check failed after focused test repair.",
                CheckMetadata(
                    prerequisite,
                    "StoppedWithEvidence",
                    rebuild,
                    repairKind: "Compile",
                    repairRound: testRepairRound,
                    failureFingerprint: rebuildEvidence.FailureFingerprint,
                    progressResult: "NewBuildFailure",
                    verificationOutcome: "NeedsReview",
                    diagnosticLines: ExecutionDiagnosticEvidence.SanitizeDiagnosticLinesForActivity(
                        rebuildEvidence.DiagnosticLines,
                        prepResult.WorkspacePath),
                    repairSelectionReason: "BridgeRebuildFailed"),
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Completed,
            "Compile convergence repair restored the build after test repair.",
            CheckMetadata(
                prerequisite,
                "VerifyingRepository",
                rebuild,
                repairKind: "Compile",
                repairRound: testRepairRound,
                repairFiles: new[] { target },
                progressResult: "CompilerConvergenceBridge",
                buildPassed: true,
                repairSelectionReason: reason),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static ExecutionActivityMetadata CheckMetadata(
        RepositoryCheck check,
        string? eventKind = null,
        RepositoryCheckResult? result = null,
        string? repairKind = null,
        int? repairRound = null,
        IReadOnlyList<string>? repairFiles = null,
        string? failureFingerprint = null,
        string? beforeFingerprint = null,
        string? afterFingerprint = null,
        string? progressResult = null,
        long? stageDurationMs = null,
        bool? buildPassed = null,
        bool? testPassed = null,
        string? baselineClassification = null,
        string? verificationOutcome = null,
        int? preExistingFailureCount = null,
        int? newRegressionCount = null,
        string? baseCommitSha = null,
        bool? baselineCacheHit = null,
        IReadOnlyList<string>? diagnosticLines = null,
        string? repairSelectionReason = null,
        string? testName = null) => new(
            BuildPassed: buildPassed,
            TestPassed: testPassed,
            EventKind: eventKind,
            StageDurationMs: stageDurationMs ?? (result?.Duration is { } duration ? (long)duration.TotalMilliseconds : null),
            RepairKind: repairKind,
            RepairRound: repairRound,
            RepairFiles: repairFiles,
            FailureFingerprint: failureFingerprint,
            BeforeChangeFingerprint: beforeFingerprint,
            AfterChangeFingerprint: afterFingerprint,
            ProgressResult: progressResult,
            RepositoryCheckId: check.Id,
            RepositoryCheckKind: check.Kind.ToString(),
            RepositoryCheckSource: check.Source.ToString(),
            ProcessExitCode: result?.ExitCode,
            VerificationFailureCategory: result?.FailureCategory.ToString(),
            DeterministicCheck: true,
            RepositoryCheckEvidence: check.DiscoveryEvidence,
            BaselineClassification: baselineClassification,
            VerificationOutcome: verificationOutcome,
            PreExistingFailureCount: preExistingFailureCount,
            NewRegressionCount: newRegressionCount,
            BaseCommitSha: baseCommitSha,
            BaselineCacheHit: baselineCacheHit,
            DiagnosticLines: diagnosticLines,
            RepairSelectionReason: repairSelectionReason,
            TestName: testName);

    private async Task<string?> GetChangeFingerprintAsync(string workspacePath, CancellationToken cancellationToken)
    {
        if (_changeFingerprintCalculator == null)
        {
            return null;
        }

        var result = await _changeFingerprintCalculator.ComputeFingerprintAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Fingerprint : null;
    }

    private static string BuildProposedPlanText(TaskImpactAnalysis analysis)
    {
        if (analysis.StructuredResult?.ProposedPlan != null && analysis.StructuredResult.ProposedPlan.Count > 0)
        {
            var builder = new StringBuilder();
            foreach (var step in analysis.StructuredResult.ProposedPlan)
            {
                builder.AppendLine($"Step {step.Order}: {step.Title} - {step.Description}");
            }
            return builder.ToString().TrimEnd();
        }

        return !string.IsNullOrWhiteSpace(analysis.Summary) ? analysis.Summary : "No detailed proposed plan provided.";
    }

    private static int TryGetNonNegativeSetting(
        IConfiguration? configuration,
        string primaryKey,
        string legacyKey,
        int defaultValue) =>
        configuration != null &&
        int.TryParse(configuration[primaryKey] ?? configuration[legacyKey], out var value) &&
        value >= 0
            ? value
            : defaultValue;

    private async Task SafeRecordActivityAsync(
        Guid executionId,
        ExecutionStage stage,
        ExecutionActivityStatus status,
        string message,
        ExecutionActivityMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _activityRecorder.RecordActivityAsync(executionId, stage, status, message, metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error recording activity for execution {ExecutionId}.", executionId);
        }
    }
}
