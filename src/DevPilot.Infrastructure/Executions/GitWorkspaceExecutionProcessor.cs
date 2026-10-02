using System.Diagnostics;
using System.Text;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Dtos;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.Executions.Services;
using DevPilot.Application.RepositoryClone;
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
    private readonly IRepositoryFreshnessService? _freshnessService;
    private readonly IExecutionGitDiffReader? _gitDiffReader;
    private readonly IReviewFeedbackAgent? _reviewFeedbackAgent;
    private readonly ILogger<GitWorkspaceExecutionProcessor> _logger;
    private readonly int _maxCompileRepairRounds;
    private readonly int _maxTestRepairRounds;
    private readonly int _maxFlakeReruns;

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
        IBaselineVerificationService? baselineVerificationService = null,
        ExecutionReliabilityOptions? reliabilityOptions = null,
        IRepositoryFreshnessService? freshnessService = null,
        IExecutionGitDiffReader? gitDiffReader = null,
        IReviewFeedbackAgent? reviewFeedbackAgent = null)
    {
        _reviewFeedbackAgent = reviewFeedbackAgent;
        _freshnessService = freshnessService;
        _gitDiffReader = gitDiffReader;
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

        // Single authoritative reliability source shared with the DeveloperAgent.
        var reliability = reliabilityOptions ?? ExecutionReliabilityOptionsFactory.Create(configuration);
        _maxCompileRepairRounds = reliability.MaxCompileRepairRounds;
        _maxTestRepairRounds = reliability.MaxTestRepairRounds;
        // Bare processors (no configuration and no options, e.g. unit tests) stay deterministic: no reruns.
        _maxFlakeReruns = reliabilityOptions == null && configuration == null ? 0 : reliability.MaxFlakeReruns;
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
            context.IsRevision ? "Requested changes started on the existing worktree." : context.IsVerifyOnly ? "Re-verification started on the existing worktree." : "Workspace preparation started.",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        RepositoryFreshnessResult? freshness = null;
        ExecutionWorkspaceResult prepResult;

        if (context.VerifyOnlyWorkspace is { } existing)
        {
            // Re-verification must never touch the base clone or recreate the worktree: the generated
            // changes live in it and would be lost.
            prepResult = ResolveExistingWorkspace(existing);
        }
        else
        {
            // Fetch origin and fast-forward the managed clone when provably safe, so the execution branches from a
            // fresh base. Never fails or blocks the execution; the outcome is recorded as evidence below.
            freshness = await TryRefreshBaseAsync(context, cancellationToken).ConfigureAwait(false);

            prepResult = await _workspaceManager.PrepareWorkspaceAsync(
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
        }

        var fileSets = new ExecutionFileSets();
        var flakeBudget = new FlakeBudget(_maxFlakeReruns);

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
                context.IsVerifyOnly ? "Existing worktree reused." : "Workspace prepared.",
                new ExecutionActivityMetadata(BranchName: prepResult.BranchName),
                cancellationToken).ConfigureAwait(false);

            if (freshness != null)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Workspace,
                    ExecutionActivityStatus.Completed,
                    $"Base freshness: {freshness.Message}",
                    new ExecutionActivityMetadata(
                        BranchName: prepResult.BranchName,
                        EventKind: "BaseFreshness",
                        BaseCommitSha: prepResult.BaseCommitSha,
                        BaseBranchName: freshness.BranchName ?? context.BaseBranch,
                        RemoteBaseCommitSha: freshness.RemoteCommitSha,
                        BaseBehindCount: freshness.BehindCount,
                        BaseFreshness: freshness.Status.ToString()),
                    cancellationToken).ConfigureAwait(false);
            }

            // A re-verified worktree already holds the generated changes, so a clean tree is not expected.
            var preAiVerification = await _workspaceManager.VerifyWorkspaceStateAsync(
                prepResult.WorkspacePath,
                prepResult.BranchName,
                requireClean: !context.IsVerifyOnly,
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

            if (context.IsVerifyOnly)
            {
                // The worktree already holds a project this execution generated, so it may not have a lockfile yet.
                // Repositories that do have one still install through the deterministic lockfile command.
                requiredChecks = requiredChecks
                    .Select(check => check.Source == RepositoryCheckSource.PackageJsonScript
                        ? check with { AllowLockfileFreeInstall = true }
                        : check)
                    .ToList();
            }

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

            string? actualModel;
            if (context.IsRevision && context.ChangeRequest!.FeedbackAlreadyApplied)
            {
                // Resumed revision: the feedback is already in the worktree, so no model call applies it again; the
                // build, test and repair that were interrupted run on what is there.
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.DeveloperAgent,
                    ExecutionActivityStatus.Started,
                    $"Resuming revision {context.ChangeRequest.RevisionNumber} on the existing worktree; reviewer feedback is not applied again.",
                    new ExecutionActivityMetadata(EventKind: "RevisionResumed"),
                    cancellationToken).ConfigureAwait(false);
                actualModel = await LoadVerifyOnlyChangesAsync(context, prepResult, fileSets, cancellationToken).ConfigureAwait(false);
                if (fileSets.ActuallyModifiedFiles.Count == 0)
                {
                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Execution,
                        ExecutionActivityStatus.Completed,
                        "Resumed revision stopped: the worktree has no changes left to verify.",
                        new ExecutionActivityMetadata(EventKind: "ReadyForReview", VerificationOutcome: "VerificationUnavailable"),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else if (context.IsRevision)
            {
                var revision = await ApplyReviewFeedbackAsync(context, prepResult, fileSets, cancellationToken).ConfigureAwait(false);
                actualModel = revision.Model;
                if (!revision.ShouldContinue)
                {
                    return;
                }
            }
            else if (context.IsVerifyOnly)
            {
                actualModel = await LoadVerifyOnlyChangesAsync(context, prepResult, fileSets, cancellationToken).ConfigureAwait(false);
                if (fileSets.ActuallyModifiedFiles.Count == 0)
                {
                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Execution,
                        ExecutionActivityStatus.Completed,
                        "Re-verification stopped: the worktree has no changes left to verify.",
                        new ExecutionActivityMetadata(
                            EventKind: "ReadyForReview",
                            VerificationOutcome: "VerificationUnavailable"),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                var generation = await RunDeveloperAgentAsync(context, prepResult, analysis, fileSets, cancellationToken).ConfigureAwait(false);
                actualModel = generation.Model;
                if (!generation.ShouldContinue)
                {
                    return;
                }
            }

            // The base had nothing to verify (e.g. a README-only repo), but the generated files may now define
            // build/test scripts. Rediscover against the worktree that actually holds the changes. If that still
            // finds nothing, the unverified outcome below is kept unchanged.
            if (requiredChecks.Count == 0)
            {
                var rediscovered = await _repositoryCheckRunner.DiscoverAsync(
                    new RepositoryPreflightRequest(prepResult.WorkspacePath, prepResult.BranchName),
                    cancellationToken).ConfigureAwait(false);

                var rediscoveredChecks = rediscovered.Checks
                    .Where(check => check.Required)
                    .OrderBy(check => check.Order)
                    .ThenBy(check => check.Id, StringComparer.Ordinal)
                    .Select(check => check.Source == RepositoryCheckSource.PackageJsonScript
                        ? check with { AllowLockfileFreeInstall = true }
                        : check)
                    .ToList();

                if (rediscoveredChecks.Count > 0)
                {
                    profile = rediscovered;
                    requiredChecks = rediscoveredChecks;

                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Build,
                        ExecutionActivityStatus.Completed,
                        "Repository verification checks discovered after applying generated changes.",
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
            }

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
                    fileSets,
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
                    fileSets,
                    flakeBudget,
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
                        fileSets.ActuallyModifiedFiles,
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

    /// <summary>Reads the current content of the repair targets that are test files (best effort).</summary>
    private static Dictionary<string, string> SnapshotTestFiles(string workspacePath, IEnumerable<string> repairFiles)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in repairFiles)
        {
            if (!ProjectGraphHelper.IsTestFileCandidate(file))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(workspacePath, file));
                if (File.Exists(fullPath))
                {
                    snapshot[file] = File.ReadAllText(fullPath);
                }
            }
            catch (Exception)
            {
                // Best effort: an unreadable file simply cannot be compared.
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Guards the "repair must not weaken tests" rule in code (not just in the prompt): if a test repair removed
    /// assertions, added skip/ignore markers or deleted tests, the run is flagged and ends in NeedsReview.
    /// </summary>
    private async Task FlagTestWeakeningAsync(
        ExecutionProcessingContext context,
        RepositoryCheck check,
        string workspacePath,
        IReadOnlyDictionary<string, string> before,
        int repairRound,
        CancellationToken cancellationToken)
    {
        foreach (var (file, beforeContent) in before)
        {
            string afterContent;
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(workspacePath, file));
                afterContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
            }
            catch (Exception)
            {
                continue;
            }

            var result = TestWeakeningDetector.Analyze(beforeContent, afterContent);
            if (!result.IsSuspected)
            {
                continue;
            }

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                ExecutionActivityStatus.Completed,
                $"Test repair may have weakened tests ({result.Describe(file)}); flagged for review.",
                CheckMetadata(
                    check,
                    "TestWeakeningSuspected",
                    repairKind: "Test",
                    repairRound: repairRound,
                    repairFiles: new[] { file }) with { TestWeakeningSuspected = true },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RepositoryFreshnessResult?> TryRefreshBaseAsync(
        ExecutionProcessingContext context,
        CancellationToken cancellationToken)
    {
        if (_freshnessService == null ||
            string.IsNullOrWhiteSpace(context.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(context.RepositoryName))
        {
            return null;
        }

        try
        {
            return await _freshnessService.RefreshAsync(
                new RepositoryFreshnessRequest(
                    context.WorkspaceLocalPath,
                    context.RepositoryOwner,
                    context.RepositoryName,
                    context.BaseBranch),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Repository freshness refresh failed for execution {ExecutionId}; continuing.", context.ExecutionId);
            return null;
        }
    }

    private async Task<bool> RunPrerequisiteCheckAsync(
        ExecutionProcessingContext context,
        ExecutionWorkspaceResult prepResult,
        TaskImpactAnalysis analysis,
        string? actualModel,
        RepositoryCheck check,
        ExecutionFileSets fileSets,
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
        var baselineUnverified = false;
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
                        buildPassed: false,
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
                // Inconclusive baseline: keep going with bounded repair from the authoritative raw diagnostics,
                // but never report the result as fully Verified.
                baselineUnverified = true;
                await RecordBaselineUnverifiedAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    check,
                    result,
                    baseComparison,
                    prepResult.BaseCommitSha,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var repairRound = 0;
        string? previousFailureFingerprint = null;
        // Files already attempted for the current failure fingerprint (reset whenever diagnostics change).
        var attemptedForFingerprint = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!result.Success && repairRound < _maxCompileRepairRounds)
        {
            repairRound++;
            var actionableFailures = baseComparison != null && (baseComparison.Classification == BaselineFailureClassification.NewRegression || baseComparison.Classification == BaselineFailureClassification.ChangedRegression)
                ? baseComparison.NewRegressions.Concat(baseComparison.ChangedFailures).ToList()
                : null;

            var evidence = (actionableFailures != null && actionableFailures.Count > 0)
                ? ExecutionDiagnosticEvidence.CreateActionableCompilerEvidence(actionableFailures, result.StdOut, result.StdErr, result.ErrorMessage)
                : ExecutionDiagnosticEvidence.ParseVerificationFailure(result.StdOut, result.StdErr, result.ErrorMessage);

            var sameFailure = string.Equals(previousFailureFingerprint, evidence.FailureFingerprint, StringComparison.Ordinal);
            if (!sameFailure)
            {
                attemptedForFingerprint.Clear();
            }

            var touchedSources = ExecutionDiagnosticEvidence.LoadTouchedSourceSnapshots(
                prepResult.WorkspacePath,
                fileSets.ActuallyModifiedFiles);
            var selection = ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
                evidence,
                fileSets.ActuallyModifiedFiles,
                attemptedForCurrentFailureSet: attemptedForFingerprint,
                touchedSources);

            // Same diagnostics after a repair: rotate to the next exact candidate; stop only when none is left.
            if (sameFailure && string.IsNullOrWhiteSpace(selection.FilePath))
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

            if (!string.IsNullOrWhiteSpace(selection.FilePath))
            {
                attemptedForFingerprint.Add(selection.FilePath);
            }

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

            var languageContext = _repairContextProvider is null
                ? null
                : await RunRepairPreparationStepAsync<string>(
                    context.ExecutionId,
                    check,
                    repairRound,
                    "collecting language context",
                    ct => Task.Run(() => _repairContextProvider.GetCompileRepairContext(check, prepResult.WorkspacePath, repairFiles), ct),
                    cancellationToken).ConfigureAwait(false);
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

            var beforeFingerprint = await RunRepairPreparationStepAsync<string>(
                context.ExecutionId,
                check,
                repairRound,
                "fingerprinting the worktree",
                ct => GetChangeFingerprintAsync(prepResult.WorkspacePath, ct),
                cancellationToken).ConfigureAwait(false);
            var repairStopwatch = Stopwatch.StartNew();
            DeveloperAgentResult repairResult;
            try
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Started,
                    $"Repair preparation: asking the model to repair {Path.GetFileName(repairFiles[0])}.",
                    CheckMetadata(check, "RepairPreparation", repairKind: "Compile", repairRound: repairRound, progressResult: "model-request"),
                    cancellationToken).ConfigureAwait(false);
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
                fileSets.AddActuallyModified(file);
            }

            var afterFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var noDiff = beforeFingerprint != null && afterFingerprint != null &&
                         string.Equals(beforeFingerprint, afterFingerprint, StringComparison.Ordinal);

            // A no-op repair of this file does not prove the failure is unfixable: try the next exact candidate first.
            if (noDiff && repairRound < _maxCompileRepairRounds &&
                !string.IsNullOrWhiteSpace(ExecutionDiagnosticEvidence.SelectNextCompilerRepairTarget(
                    evidence,
                    fileSets.ActuallyModifiedFiles,
                    attemptedForFingerprint,
                    touchedSources).FilePath))
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Completed,
                    "Focused repair produced no worktree change; trying the next candidate file.",
                    CheckMetadata(
                        check,
                        "FixingBuildIssue",
                        result,
                        repairKind: "Compile",
                        repairRound: repairRound,
                        repairFiles: repairFiles,
                        failureFingerprint: evidence.FailureFingerprint,
                        beforeFingerprint: beforeFingerprint,
                        afterFingerprint: afterFingerprint,
                        progressResult: "NoDiff",
                        stageDurationMs: repairStopwatch.ElapsedMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                previousFailureFingerprint = evidence.FailureFingerprint;
                continue;
            }

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
                    preExistingFailureCount: baseComparison?.PreExistingCount ?? 0,
                    baselineUnverified: baselineUnverified ? true : null),
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
                verificationOutcome: baselineUnverified ? "PartiallyVerified" : "Verified",
                stageDurationMs: stopwatch.ElapsedMilliseconds,
                baselineUnverified: baselineUnverified ? true : null),
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
        ExecutionFileSets fileSets,
        FlakeBudget flakeBudget,
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

        result = await ConfirmFailureIsStableAsync(context, check, fullRequest, result, flakeBudget, cancellationToken).ConfigureAwait(false);
        if (result.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure)
        {
            await RecordInfrastructureFailureAsync(context.ExecutionId, ExecutionStage.Test, check, result, cancellationToken).ConfigureAwait(false);
            return false;
        }

        BaselineFailureComparison? baseComparison = null;
        var baselineUnverified = false;
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
                        testPassed: false,
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
                baselineUnverified = true;
                await RecordBaselineUnverifiedAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    check,
                    result,
                    baseComparison,
                    prepResult.BaseCommitSha,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken).ConfigureAwait(false);
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
                fileSets.VerificationEligiblePlannedFiles,
                prepResult.WorkspacePath);
            var repairFiles = selection.FilePaths.ToList();
            var sanitizedTestEvidence = ExecutionDiagnosticEvidence.SanitizeTestEvidenceForActivity(evidence);

            // The baseline proves this failure is new, so it was introduced by this task's edits even when the
            // stack trace points at untouched files (shared fixtures, static state, auth/config interference).
            // The touched files are then the only suspects; prefer test files over production files.
            if (repairFiles.Count == 0 && actionableFailures is { Count: > 0 })
            {
                var touched = fileSets.ActuallyModifiedFiles.ToList();
                var touchedTests = touched.Where(ProjectGraphHelper.IsTestFileCandidate).ToList();
                repairFiles = (touchedTests.Count > 0 ? touchedTests : touched).Take(3).ToList();
                if (repairFiles.Count > 0)
                {
                    selection = selection with { Reason = "BaselineRegressionTouchedFilesFallback" };
                }
            }

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
                TouchedFiles: fileSets.ActuallyModifiedFiles.ToList(),
                TestName: evidence.TestName);

            var beforeFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var testFilesBeforeRepair = SnapshotTestFiles(prepResult.WorkspacePath, repairFiles);
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
            await FlagTestWeakeningAsync(
                context,
                check,
                prepResult.WorkspacePath,
                testFilesBeforeRepair,
                repairRound,
                cancellationToken).ConfigureAwait(false);
            var afterFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var noDiff = beforeFingerprint != null && afterFingerprint != null &&
                         string.Equals(beforeFingerprint, afterFingerprint, StringComparison.Ordinal);

            IReadOnlyList<string> supersededNoChange = Array.Empty<string>();
            if (noDiff)
            {
                foreach (var file in repairedThisRound)
                {
                    if (!fileSets.ResolvedNoChangeFiles.Contains(file))
                    {
                        fileSets.AddActuallyModified(file);
                    }
                }
            }
            else
            {
                supersededNoChange = fileSets.PromoteRepairedFiles(repairedThisRound);
            }

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

            if (supersededNoChange.Count > 0)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Completed,
                    "NoChange overridden by authoritative verification evidence.",
                    new ExecutionActivityMetadata(
                        EventKind: "NoChangeOverridden",
                        RepairKind: "Test",
                        RepairRound: repairRound,
                        RepairFiles: supersededNoChange,
                        ModifiedFileCount: fileSets.ActuallyModifiedFiles.Count,
                        ResolvedNoChangeCount: fileSets.ResolvedNoChangeFiles.Count),
                    cancellationToken).ConfigureAwait(false);
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
                    fileSets,
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

            result = await ConfirmFailureIsStableAsync(context, check, fullRequest, result, flakeBudget, cancellationToken).ConfigureAwait(false);
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
                    preExistingFailureCount: baseComparison?.PreExistingCount ?? 0,
                    baselineUnverified: baselineUnverified ? true : null),
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
                verificationOutcome: baselineUnverified ? "PartiallyVerified" : "Verified",
                stageDurationMs: stopwatch.ElapsedMilliseconds,
                baselineUnverified: baselineUnverified ? true : null),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Confirms a failed full test run is stable before it is sent to repair, spending at most
    /// <see cref="ExecutionReliabilityOptions.MaxFlakeReruns"/> confirmations per execution (shared via <paramref name="budget"/>).
    /// Prefers a deterministic targeted rerun of the single failing test: if it fails again the failure is stable and the
    /// original full-run result is kept. If it passes (order/parallelism-dependent) or no reliable test name exists, one
    /// full rerun decides. A passing rerun wins; otherwise the latest failing result is returned.
    /// </summary>
    private async Task<RepositoryCheckResult> ConfirmFailureIsStableAsync(
        ExecutionProcessingContext context,
        RepositoryCheck check,
        RepositoryCheckExecutionRequest fullRequest,
        RepositoryCheckResult result,
        FlakeBudget budget,
        CancellationToken cancellationToken)
    {
        if (result.Success ||
            result.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure ||
            budget.Remaining <= 0)
        {
            return result;
        }

        budget.Remaining--;

        var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(result.StdOut, result.StdErr, result.ErrorMessage);
        if (check.SupportsTargetedTest && evidence.HasReliableTestName)
        {
            var targeted = await _repositoryCheckRunner
                .ExecuteAsync(fullRequest with { TestFilter = evidence.TestName }, cancellationToken)
                .ConfigureAwait(false);
            if (targeted.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure)
            {
                return result;
            }

            if (!targeted.Success)
            {
                _logger.LogInformation(
                    "Failing test reproduced on targeted rerun for execution {ExecutionId}; treating as a stable failure.",
                    context.ExecutionId);
                return result;
            }
        }

        var rerun = await _repositoryCheckRunner.ExecuteAsync(fullRequest, cancellationToken).ConfigureAwait(false);
        if (rerun.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure)
        {
            return result;
        }

        if (rerun.Success)
        {
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                ExecutionActivityStatus.Completed,
                "Flaky failure detected: tests passed on confirmation rerun.",
                CheckMetadata(check, "VerifyingRepository", rerun),
                cancellationToken).ConfigureAwait(false);
        }

        return rerun;
    }

    /// <summary>
    /// Records that the baseline comparison was inconclusive and repair continues from raw diagnostics.
    /// Recorded as a non-terminal (Started) activity so it never masquerades as a final verdict; the
    /// <see cref="ExecutionActivityMetadata.BaselineUnverified"/> flag downgrades the final outcome.
    /// </summary>
    private Task RecordBaselineUnverifiedAsync(
        Guid executionId,
        ExecutionStage stage,
        RepositoryCheck check,
        RepositoryCheckResult result,
        BaselineFailureComparison comparison,
        string? baseCommitSha,
        long stageDurationMs,
        CancellationToken cancellationToken) =>
        SafeRecordActivityAsync(
            executionId,
            stage,
            ExecutionActivityStatus.Started,
            $"Baseline comparison inconclusive ({comparison.Summary}); continuing bounded repair from raw diagnostics. Result will be reported as baseline-unverified.",
            CheckMetadata(
                check,
                "BaselineUnverified",
                result,
                baselineClassification: "Unknown",
                baseCommitSha: baseCommitSha,
                baselineCacheHit: comparison.CacheHit,
                stageDurationMs: stageDurationMs,
                baselineUnverified: true),
            cancellationToken);

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
        ExecutionFileSets fileSets,
        IReadOnlyList<string> repairedThisRound,
        int testRepairRound,
        CancellationToken cancellationToken)
    {
        var evidence = ExecutionDiagnosticEvidence.ParseVerificationFailure(
            prerequisiteResult.StdOut,
            prerequisiteResult.StdErr,
            prerequisiteResult.ErrorMessage);
        var implicated = ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(evidence, fileSets.ActuallyModifiedFiles).ToList();
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
            fileSets.AddActuallyModified(file);
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
        string? testName = null,
        bool? baselineUnverified = null) => new(
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
            TestName: testName,
            BaselineUnverified: baselineUnverified);

    /// <summary>Longest a local step before a repair may take; the model call has its own limits.</summary>
    private static readonly TimeSpan RepairPreparationStepTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Runs one local step that precedes a repair model call (context collection, change fingerprint) with a time limit,
    /// cancellation, and activity records that name the step, so a slow step is visible instead of looking like a hang.
    /// A step that exceeds its limit is abandoned and yields <c>default</c>; the repair continues without its result.
    /// </summary>
    private async Task<T?> RunRepairPreparationStepAsync<T>(
        Guid executionId,
        RepositoryCheck check,
        int repairRound,
        string step,
        Func<CancellationToken, Task<T?>> work,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await SafeRecordActivityAsync(
            executionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Started,
            $"Repair preparation: {step}.",
            CheckMetadata(check, "RepairPreparation", repairKind: "Compile", repairRound: repairRound, progressResult: step),
            cancellationToken).ConfigureAwait(false);

        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stepCts.CancelAfter(RepairPreparationStepTimeout);
        try
        {
            var value = await work(stepCts.Token).WaitAsync(stepCts.Token).ConfigureAwait(false);
            await SafeRecordActivityAsync(
                executionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Completed,
                $"Repair preparation: {step} finished in {stopwatch.Elapsed.TotalSeconds:0.#}s.",
                CheckMetadata(check, "RepairPreparation", repairKind: "Compile", repairRound: repairRound, progressResult: step, stageDurationMs: stopwatch.ElapsedMilliseconds),
                cancellationToken).ConfigureAwait(false);
            return value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await SafeRecordActivityAsync(
                executionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Failed,
                $"Repair preparation: {step} did not finish within {(int)RepairPreparationStepTimeout.TotalSeconds}s; continuing without it.",
                CheckMetadata(check, "RepairPreparation", repairKind: "Compile", repairRound: repairRound, progressResult: $"{step}:Timeout", stageDurationMs: stopwatch.ElapsedMilliseconds),
                cancellationToken).ConfigureAwait(false);
            return default;
        }
    }

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

    private sealed class FlakeBudget
    {
        public FlakeBudget(int remaining) => Remaining = remaining;

        public int Remaining { get; set; }
    }

    private sealed class ExecutionFileSets
    {
        public HashSet<string> ActuallyModifiedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> VerificationEligiblePlannedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ResolvedNoChangeFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddActuallyModified(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            ActuallyModifiedFiles.Add(file);
            VerificationEligiblePlannedFiles.Add(file);
        }

        public void AddResolvedNoChange(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || ActuallyModifiedFiles.Contains(file))
            {
                return;
            }

            ResolvedNoChangeFiles.Add(file);
            VerificationEligiblePlannedFiles.Add(file);
        }

        public IReadOnlyList<string> PromoteRepairedFiles(IEnumerable<string> files)
        {
            var superseded = new List<string>();
            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                if (ResolvedNoChangeFiles.Remove(file))
                {
                    superseded.Add(file);
                }

                AddActuallyModified(file);
            }

            return superseded;
        }
    }

    /// <summary>
    /// Generates and applies the planned edits. Returns ShouldContinue=false when the execution already
    /// reached a terminal activity (for example a plan that resolved to no change at all).
    /// </summary>
    private async Task<(bool ShouldContinue, string? Model)> RunDeveloperAgentAsync(
        ExecutionProcessingContext context,
        ExecutionWorkspaceResult prepResult,
        TaskImpactAnalysis analysis,
        ExecutionFileSets fileSets,
        CancellationToken cancellationToken)
    {
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
            // Not analysis.Model: that is the planning model. The model that really answers is recorded per provider call.
            new ExecutionActivityMetadata(EventKind: "GeneratingChange"),
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
                return (false, actualModel);
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
            fileSets.AddActuallyModified(file);
        }

        foreach (var file in agentResult.ResolvedNoChangeFiles ?? Array.Empty<string>())
        {
            fileSets.AddResolvedNoChange(file);
        }

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.DeveloperAgent,
            ExecutionActivityStatus.Completed,
            "Developer Agent completed.",
            new ExecutionActivityMetadata(
                ModifiedFileCount: fileSets.ActuallyModifiedFiles.Count,
                Model: actualModel,
                EventKind: "GeneratingChange",
                ResolvedNoChangeCount: fileSets.ResolvedNoChangeFiles.Count > 0
                    ? fileSets.ResolvedNoChangeFiles.Count
                    : null),
            cancellationToken).ConfigureAwait(false);

        return (true, actualModel);
    }

    /// <summary>
    /// Gives the reviewer's feedback and the code the execution already produced to the AI, which edits the same
    /// worktree. ShouldContinue=false when the AI changed nothing (the earlier verification result still stands).
    /// A failure throws, like the initial generation, so the caller can report it.
    /// </summary>
    private async Task<(bool ShouldContinue, string? Model)> ApplyReviewFeedbackAsync(
        ExecutionProcessingContext context,
        ExecutionWorkspaceResult prepResult,
        ExecutionFileSets fileSets,
        CancellationToken cancellationToken)
    {
        var changeRequest = context.ChangeRequest!;
        if (_reviewFeedbackAgent == null)
        {
            throw new InvalidOperationException("Requesting changes needs a review feedback agent, but none is configured.");
        }

        if (_gitDiffReader == null)
        {
            throw new InvalidOperationException("Requesting changes needs a git diff reader.");
        }

        // The files this execution produced: everything already committed on the branch plus what is still uncommitted.
        if (!string.IsNullOrWhiteSpace(changeRequest.CommittedBaseCommitSha))
        {
            var committed = await _gitDiffReader
                .ReadCommittedDiffAsync(prepResult.WorkspacePath, changeRequest.CommittedBaseCommitSha, "HEAD", cancellationToken)
                .ConfigureAwait(false);
            if (committed.Success && committed.ChangedFiles != null)
            {
                foreach (var file in committed.ChangedFiles)
                {
                    fileSets.AddActuallyModified(file.Path.Replace('\\', '/'));
                }
            }
        }

        var uncommitted = await _gitDiffReader
            .ReadWorkspaceDiffAsync(prepResult.WorkspacePath, prepResult.BranchName, cancellationToken)
            .ConfigureAwait(false);
        if (!uncommitted.Success)
        {
            throw new InvalidOperationException(
                $"The requested fix could not read the worktree changes: {uncommitted.ErrorMessage}");
        }

        foreach (var file in uncommitted.ChangedFiles ?? Array.Empty<ExecutionReviewFileDto>())
        {
            fileSets.AddActuallyModified(file.Path.Replace('\\', '/'));
        }

        var execution = await _executionRepository
            .GetByIdAsync(context.ExecutionId, cancellationToken)
            .ConfigureAwait(false);
        var model = !string.IsNullOrWhiteSpace(execution?.Model) ? execution.Model : execution?.PinnedAiModelName;

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.DeveloperAgent,
            ExecutionActivityStatus.Started,
            $"Applying reviewer feedback (revision {changeRequest.RevisionNumber}).",
            new ExecutionActivityMetadata(
                Model: model,
                EventKind: "ApplyingReviewFeedback",
                ConsideredFiles: fileSets.ActuallyModifiedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList()),
            cancellationToken).ConfigureAwait(false);

        var result = await _reviewFeedbackAgent.ApplyReviewFeedbackAsync(
            new ReviewFeedbackRequest(
                context.TaskId,
                context.ExecutionId,
                context.TaskTitle,
                context.TaskDescription,
                context.AcceptanceCriteria,
                changeRequest.Feedback,
                prepResult.WorkspacePath,
                prepResult.BranchName,
                fileSets.ActuallyModifiedFiles.ToList(),
                model,
                changeRequest.RevisionNumber),
            cancellationToken).ConfigureAwait(false);

        var actualModel = result.Model ?? model;
        if (!result.Success)
        {
            // Reported as a Review-stage failure on purpose: the code is unchanged, so the verification verdict
            // of the previous run (which the evaluator derives from Build/Test/DeveloperAgent activity) must not flip.
            var error = result.ErrorMessage ?? "The reviewer feedback could not be applied.";
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Review,
                ExecutionActivityStatus.Failed,
                $"Requested fix could not be applied: {error}",
                new ExecutionActivityMetadata(Model: actualModel, EventKind: "ReviewFeedbackFailed", UnresolvedNote: error.Length > 600 ? error[..600] : error),
                cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(error);
        }

        if (result.ModifiedFiles == null || result.ModifiedFiles.Count == 0)
        {
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Review,
                ExecutionActivityStatus.Completed,
                "The AI made no code change for the requested fix; the earlier verification result still applies.",
                new ExecutionActivityMetadata(
                    Model: actualModel,
                    EventKind: "ReviewFeedbackNoChange",
                    ChangeSummary: result.Summary,
                    UnresolvedNote: result.Unresolved,
                    ResolvedNoChangeCount: result.ResolvedNoChangeFiles?.Count),
                cancellationToken).ConfigureAwait(false);
            return (false, actualModel);
        }

        foreach (var file in result.ModifiedFiles)
        {
            fileSets.AddActuallyModified(file);
        }

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.DeveloperAgent,
            ExecutionActivityStatus.Completed,
            "Reviewer feedback applied.",
            new ExecutionActivityMetadata(
                ModifiedFileCount: result.ModifiedFiles.Count,
                Model: actualModel,
                EventKind: "ReviewFeedbackApplied",
                ChangedFiles: result.ModifiedFiles.ToList(),
                ChangeSummary: result.Summary,
                UnresolvedNote: result.Unresolved),
            cancellationToken).ConfigureAwait(false);

        return (true, actualModel);
    }

    /// <summary>
    /// Accepts the execution's stored worktree. Preparing a new one is forbidden here: the manager refuses to
    /// reuse an existing path, and creating another worktree would drop the generated changes.
    /// </summary>
    private static ExecutionWorkspaceResult ResolveExistingWorkspace(ExecutionVerifyOnlyWorkspace existing)
    {
        if (string.IsNullOrWhiteSpace(existing.WorkspacePath) || !Directory.Exists(existing.WorkspacePath))
        {
            return new ExecutionWorkspaceResult(
                existing.WorkspacePath ?? string.Empty,
                existing.BranchName ?? string.Empty,
                false,
                "The execution worktree no longer exists, so the generated changes cannot be verified.");
        }

        if (string.IsNullOrWhiteSpace(existing.BranchName))
        {
            return new ExecutionWorkspaceResult(
                existing.WorkspacePath,
                string.Empty,
                false,
                "The execution has no branch name, so the worktree cannot be verified.");
        }

        return new ExecutionWorkspaceResult(
            existing.WorkspacePath,
            existing.BranchName,
            true,
            BaseCommitSha: existing.BaseCommitSha);
    }

    /// <summary>
    /// Loads the files already changed in the worktree. Returns the model recorded on the execution.
    /// </summary>
    private async Task<string?> LoadVerifyOnlyChangesAsync(
        ExecutionProcessingContext context,
        ExecutionWorkspaceResult prepResult,
        ExecutionFileSets fileSets,
        CancellationToken cancellationToken)
    {
        if (_gitDiffReader == null)
        {
            throw new InvalidOperationException("Re-verification requires a git diff reader.");
        }

        var diff = await _gitDiffReader
            .ReadWorkspaceDiffAsync(prepResult.WorkspacePath, prepResult.BranchName, cancellationToken)
            .ConfigureAwait(false);

        if (!diff.Success)
        {
            throw new InvalidOperationException(
                $"Re-verification could not read the worktree changes: {diff.ErrorMessage}");
        }

        if (diff.ChangedFiles != null)
        {
            foreach (var file in diff.ChangedFiles)
            {
                if (!string.IsNullOrWhiteSpace(file.Path))
                {
                    fileSets.AddActuallyModified(file.Path.Replace('\\', '/'));
                }
            }
        }

        var execution = await _executionRepository
            .GetByIdAsync(context.ExecutionId, cancellationToken)
            .ConfigureAwait(false);
        var actualModel = !string.IsNullOrWhiteSpace(execution?.Model)
            ? execution.Model
            : execution?.PinnedAiModelName;

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.DeveloperAgent,
            ExecutionActivityStatus.Completed,
            "Existing worktree changes reused; code was not regenerated.",
            new ExecutionActivityMetadata(
                ModifiedFileCount: fileSets.ActuallyModifiedFiles.Count,
                Model: actualModel,
                EventKind: "ReverifyExistingChanges"),
            cancellationToken).ConfigureAwait(false);

        return actualModel;
    }
}
