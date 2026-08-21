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
        IRepositoryRepairContextProvider? repairContextProvider = null)
    {
        _workspaceManager = workspaceManager;
        _executionRepository = executionRepository;
        _impactAnalysisRepository = impactAnalysisRepository;
        _developerAgent = developerAgent;
        _repositoryCheckRunner = repositoryCheckRunner;
        _activityRecorder = activityRecorder;
        _changeFingerprintCalculator = changeFingerprintCalculator;
        _repairContextProvider = repairContextProvider;
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
                cancellationToken: cancellationToken).ConfigureAwait(false);
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

        if (profile.State != RepositoryVerificationState.Configured || requiredChecks.Count == 0)
        {
            var category = profile.State == RepositoryVerificationState.InfrastructureFailure
                ? RepositoryCheckFailureCategory.InfrastructureFailure.ToString()
                : "Unconfigured";
            var error = profile.State == RepositoryVerificationState.InfrastructureFailure
                ? $"Repository verification preflight failed: {profile.Message ?? "Infrastructure failure."}"
                : $"Repository verification is unconfigured: {profile.Message ?? "No trustworthy check was discovered."}";

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Execution,
                ExecutionActivityStatus.Failed,
                error,
                new ExecutionActivityMetadata(
                    EventKind: "StoppedWithEvidence",
                    DiscoveredCheckCount: requiredChecks.Count,
                    DiscoveredChecks: requiredChecks.Select(check => check.Id).ToList(),
                    DiscoveredCheckEvidence: requiredChecks
                        .Where(check => !string.IsNullOrWhiteSpace(check.DiscoveryEvidence))
                        .Select(check => $"{check.Id}: {check.DiscoveryEvidence}")
                        .ToList(),
                    DetectedEcosystems: profile.Ecosystems,
                    VerificationFailureCategory: category,
                    DeterministicCheck: true,
                    VerificationUnresolved: profile.HasUnresolvedVerification),
                cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(error);
        }

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
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
            .Select(file => new ImpactedFileDetail(file.FilePath, file.ChangeType.ToString(), file.Reason))
            .ToList() ?? new List<ImpactedFileDetail>();

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
            analysis.Model);

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
                actualModel != null ? new ExecutionActivityMetadata(Model: actualModel) : null,
                cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(error);
        }

        if (agentResult.ModifiedFiles == null || agentResult.ModifiedFiles.Count == 0)
        {
            const string error = "Developer Agent failed: Developer Agent returned success but produced zero modified files.";
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.DeveloperAgent,
                ExecutionActivityStatus.Failed,
                error,
                actualModel != null ? new ExecutionActivityMetadata(Model: actualModel) : null,
                cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(error);
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

        var modifiedFiles = new HashSet<string>(agentResult.ModifiedFiles, StringComparer.OrdinalIgnoreCase);
        var prerequisiteChecks = requiredChecks.Where(check => check.Kind != RepositoryCheckKind.Test).ToList();
        var testChecks = requiredChecks.Where(check => check.Kind == RepositoryCheckKind.Test).ToList();

        foreach (var check in prerequisiteChecks)
        {
            await RunPrerequisiteCheckAsync(
                context,
                prepResult,
                analysis,
                actualModel,
                check,
                modifiedFiles,
                cancellationToken).ConfigureAwait(false);
        }

        var confirmedBuild = prerequisiteChecks.Any(check => check.Kind == RepositoryCheckKind.Build);
        for (var index = 0; index < testChecks.Count; index++)
        {
            var check = testChecks[index];
            await RunTestCheckAsync(
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
        }

        if (testChecks.Count == 0)
        {
            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Completed,
                "Repository checks passed.",
                new ExecutionActivityMetadata(
                    BuildPassed: prerequisiteChecks.Any(check => check.Kind == RepositoryCheckKind.Build) ? true : null,
                    EventKind: "ReadyForReview"),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunPrerequisiteCheckAsync(
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
            throw new InvalidOperationException($"Repository check infrastructure failure: {result.ErrorMessage}");
        }

        var repairRound = 0;
        string? previousFailureFingerprint = null;

        while (!result.Success && repairRound < _maxCompileRepairRounds)
        {
            repairRound++;
            var evidence = ExecutionDiagnosticEvidence.ParseVerificationFailure(result.StdOut, result.StdErr, result.ErrorMessage);

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

            var repairFiles = ExecutionDiagnosticEvidence.SelectCompilerRepairFiles(evidence, modifiedFiles).ToList();
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
                        progressResult: "Uncorrelated"),
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
                    failureFingerprint: evidence.FailureFingerprint),
                cancellationToken).ConfigureAwait(false);

            if (check.Kind == RepositoryCheckKind.Build)
            {
                var repairSummary = new StringBuilder()
                    .AppendLine($"Build failed — {evidence.DiagnosticLines.Count} compiler error(s)");
                foreach (var diagnostic in evidence.DiagnosticLines.Take(5))
                {
                    repairSummary.AppendLine(diagnostic.Trim());
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
                        failureFingerprint: evidence.FailureFingerprint),
                    cancellationToken).ConfigureAwait(false);
            }

            var repairDescription = new StringBuilder()
                .AppendLine($"Fix the following authoritative repository check failure (repair round {repairRound}/{_maxCompileRepairRounds}):")
                .AppendLine(string.Join("\n", evidence.DiagnosticLines.Take(10)))
                .ToString();
            var languageContext = _repairContextProvider?.GetCompileRepairContext(check, prepResult.WorkspacePath, repairFiles);
            if (!string.IsNullOrWhiteSpace(languageContext))
            {
                repairDescription += $"\n=== Available Repository / Port Abstractions ===\n{languageContext}\nUse existing repository abstractions; do not invent unavailable types.";
            }

            var repairRequest = new DeveloperAgentRequest(
                context.TaskId,
                context.ExecutionId,
                $"Repair {check.DisplayName} failure for {context.TaskTitle} (round {repairRound})",
                repairDescription,
                "Resolve the authoritative repository check failure in the focused files without weakening existing tests or checks.",
                analysis.Summary ?? "Repository check repair",
                "Repair repository verification failure",
                repairFiles,
                prepResult.WorkspacePath,
                prepResult.BranchName,
                repairFiles.Select(file => new ImpactedFileDetail(file, "Modify", "Fix repository verification failure")).ToList(),
                actualModel);

            var beforeFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var repairStopwatch = Stopwatch.StartNew();
            DeveloperAgentResult repairResult;
            try
            {
                repairResult = await _developerAgent.GenerateAndApplyEditsAsync(repairRequest, cancellationToken).ConfigureAwait(false);
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
                throw new InvalidOperationException($"Repository check infrastructure failure: {result.ErrorMessage}");
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
                    buildPassed: check.Kind == RepositoryCheckKind.Build ? false : null),
                cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(error);
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
                stageDurationMs: stopwatch.ElapsedMilliseconds),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunTestCheckAsync(
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
            throw new InvalidOperationException($"Repository check infrastructure failure: {result.ErrorMessage}");
        }

        var repairRound = 0;
        string? previousFailureFingerprint = null;
        while (!result.Success && repairRound < _maxTestRepairRounds)
        {
            repairRound++;
            var evidence = ExecutionDiagnosticEvidence.ParseTestFailure(result.StdOut, result.StdErr, result.ErrorMessage);

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

            var repairFiles = ExecutionDiagnosticEvidence.SelectTestRepairFiles(evidence, modifiedFiles).ToList();
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
                        progressResult: "Uncorrelated"),
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
                    failureFingerprint: evidence.FailureFingerprint),
                cancellationToken).ConfigureAwait(false);

            var repairRequest = new DeveloperAgentRequest(
                context.TaskId,
                context.ExecutionId,
                $"Repair test failures for {context.TaskTitle} (round {repairRound})",
                $"Fix the authoritative failing test evidence (repair round {repairRound}/{_maxTestRepairRounds}):\n{string.Join("\n", evidence.RelevantLines)}\n\nDo not delete, skip, comment out, or weaken existing tests.",
                "Resolve the failing test without weakening existing test assertions.",
                analysis.Summary ?? "Test repair",
                "Repair test failure",
                repairFiles,
                prepResult.WorkspacePath,
                prepResult.BranchName,
                repairFiles.Select(file => new ImpactedFileDetail(file, "Modify", "Fix test failure")).ToList(),
                actualModel);

            var beforeFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var repairStopwatch = Stopwatch.StartNew();
            DeveloperAgentResult repairResult;
            try
            {
                repairResult = await _developerAgent.GenerateAndApplyEditsAsync(repairRequest, cancellationToken).ConfigureAwait(false);
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

            foreach (var file in repairResult.ModifiedFiles ?? Array.Empty<string>())
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

            foreach (var prerequisite in prerequisiteChecks)
            {
                var prerequisiteResult = await _repositoryCheckRunner.ExecuteAsync(
                    new RepositoryCheckExecutionRequest(prepResult.WorkspacePath, prepResult.BranchName, prerequisite),
                    cancellationToken).ConfigureAwait(false);

                if (prerequisiteResult.Success)
                {
                    continue;
                }

                var newEvidence = ExecutionDiagnosticEvidence.ParseVerificationFailure(
                    prerequisiteResult.StdOut,
                    prerequisiteResult.StdErr,
                    prerequisiteResult.ErrorMessage);
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
                        repairRound: repairRound,
                        failureFingerprint: newEvidence.FailureFingerprint,
                        progressResult: "NewBuildFailure"),
                    cancellationToken).ConfigureAwait(false);

                var prefix = prerequisiteResult.FailureCategory == RepositoryCheckFailureCategory.InfrastructureFailure
                    ? "Repository check infrastructure failure after test repair"
                    : "Build validation failed after test repair";
                throw new InvalidOperationException($"{prefix}: {prerequisiteResult.ErrorMessage ?? newEvidence.DiagnosticLines.FirstOrDefault() ?? "Repository check failed."}");
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
                throw new InvalidOperationException($"Repository check infrastructure failure: {result.ErrorMessage}");
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
                CheckMetadata(check, "StoppedWithEvidence", result, testPassed: false),
                cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(error);
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
                stageDurationMs: stopwatch.ElapsedMilliseconds),
            cancellationToken).ConfigureAwait(false);
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
            CheckMetadata(check, "StoppedWithEvidence", result),
            cancellationToken).ConfigureAwait(false);
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
        bool? testPassed = null) => new(
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
            RepositoryCheckEvidence: check.DiscoveryEvidence);

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
