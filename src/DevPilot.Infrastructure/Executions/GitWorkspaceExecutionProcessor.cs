using System.Diagnostics;
using System.Text;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Execution processor that orchestrates the real Developer Agent execution pipeline:
/// Prepare Workspace → Verify Clean → Run Developer Agent → Build Validation (with Compile Repair Loop) → Test Validation (with Test Repair Loop).
/// </summary>
public sealed class GitWorkspaceExecutionProcessor : IExecutionProcessor
{
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IExecutionRepository _executionRepository;
    private readonly IImpactAnalysisRepository _impactAnalysisRepository;
    private readonly IDeveloperAgent _developerAgent;
    private readonly IExecutionValidationRunner _validationRunner;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly IExecutionChangeFingerprintCalculator? _changeFingerprintCalculator;
    private readonly ILogger<GitWorkspaceExecutionProcessor> _logger;
    private readonly int _maxCompileRepairRounds;
    private readonly int _maxTestRepairRounds;

    public GitWorkspaceExecutionProcessor(
        IExecutionWorkspaceManager workspaceManager,
        IExecutionRepository executionRepository,
        IImpactAnalysisRepository impactAnalysisRepository,
        IDeveloperAgent developerAgent,
        IExecutionValidationRunner validationRunner,
        IExecutionActivityRecorder activityRecorder,
        ILogger<GitWorkspaceExecutionProcessor> logger,
        IConfiguration? configuration = null,
        IExecutionChangeFingerprintCalculator? changeFingerprintCalculator = null)
    {
        _workspaceManager = workspaceManager;
        _executionRepository = executionRepository;
        _impactAnalysisRepository = impactAnalysisRepository;
        _developerAgent = developerAgent;
        _validationRunner = validationRunner;
        _activityRecorder = activityRecorder;
        _changeFingerprintCalculator = changeFingerprintCalculator;
        _logger = logger;

        if (configuration != null &&
            int.TryParse(configuration["ExecutionReliability:MaxCompileRepairRounds"] ?? configuration["DeveloperAgent:MaxCompileRepairRounds"], out var compileRounds) &&
            compileRounds >= 0)
        {
            _maxCompileRepairRounds = compileRounds;
        }
        else
        {
            _maxCompileRepairRounds = 3;
        }

        if (configuration != null &&
            int.TryParse(configuration["ExecutionReliability:MaxTestRepairRounds"] ?? configuration["DeveloperAgent:MaxTestRepairRounds"], out var testRounds) &&
            testRounds >= 0)
        {
            _maxTestRepairRounds = testRounds;
        }
        else
        {
            _maxTestRepairRounds = 2;
        }
    }

    public async Task ProcessAsync(
        ExecutionProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: starting execution pipeline for execution {ExecutionId} (Task '{TaskTitle}' - {TaskId}).",
            context.ExecutionId,
            context.TaskTitle,
            context.TaskId);

        // ── Stage 1. Prepare isolated workspace & dedicated branch ──────────────────
        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Workspace,
            ExecutionActivityStatus.Started,
            "Workspace preparation started.",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var prepResult = await _workspaceManager.PrepareWorkspaceAsync(
            executionId: context.ExecutionId,
            taskId: context.TaskId,
            sourceRepositoryLocalPath: context.WorkspaceLocalPath,
            sourceBranch: null,
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!prepResult.Success)
        {
            var errorMessage = $"Execution workspace preparation failed: {prepResult.ErrorMessage}";
            _logger.LogError(
                "GitWorkspaceExecutionProcessor: preparation failed for execution {ExecutionId}. Error: {Error}",
                context.ExecutionId,
                errorMessage);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Workspace,
                ExecutionActivityStatus.Failed,
                errorMessage,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(errorMessage);
        }

        // ── Stage 2. Persist workspace path and branch name to TaskExecution record ─
        await _executionRepository
            .UpdateWorkspaceDetailsAsync(
                context.ExecutionId,
                prepResult.WorkspacePath,
                prepResult.BranchName,
                cancellationToken)
            .ConfigureAwait(false);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Workspace,
            ExecutionActivityStatus.Completed,
            "Workspace prepared.",
            new ExecutionActivityMetadata(BranchName: prepResult.BranchName),
            cancellationToken).ConfigureAwait(false);

        // ── Stage 3. Verify workspace state immediately before AI call (clean required)
        var preAiVerification = await _workspaceManager
            .VerifyWorkspaceStateAsync(
                prepResult.WorkspacePath,
                prepResult.BranchName,
                requireClean: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!preAiVerification.IsValid)
        {
            var errorMessage = $"Developer Agent failed: Execution workspace verification failed prior to AI invocation. {preAiVerification.ErrorMessage}";
            _logger.LogError(
                "GitWorkspaceExecutionProcessor: pre-AI verification failed for execution {ExecutionId}. Error: {Error}",
                context.ExecutionId,
                errorMessage);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.DeveloperAgent,
                ExecutionActivityStatus.Failed,
                errorMessage,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(errorMessage);
        }

        // ── Stage 4. Load completed persisted TaskImpactAnalysis ──────────────────
        var analysis = await _impactAnalysisRepository
            .GetLatestByTaskIdAsync(context.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (analysis is null || analysis.Status != ImpactAnalysisStatus.Completed)
        {
            const string analysisError = "Developer Agent failed: A completed TaskImpactAnalysis is required before running the Developer Agent.";
            _logger.LogError(
                "GitWorkspaceExecutionProcessor: {Error} ExecutionId={ExecutionId}, TaskId={TaskId}",
                analysisError,
                context.ExecutionId,
                context.TaskId);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.DeveloperAgent,
                ExecutionActivityStatus.Failed,
                analysisError,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(analysisError);
        }

        // ── Stage 5. Build DeveloperAgentRequest & Invoke Developer Agent ─────────
        var summary = !string.IsNullOrWhiteSpace(analysis.StructuredResult?.Summary)
            ? analysis.StructuredResult.Summary
            : context.ImpactAnalysisSummary;

        var proposedPlanText = BuildProposedPlanText(analysis);

        var impactedFiles = analysis.StructuredResult?.ImpactedFiles?
            .Select(f => f.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList() ?? new List<string>();

        var impactedFileDetails = analysis.StructuredResult?.ImpactedFiles?
            .Where(f => !string.IsNullOrWhiteSpace(f.FilePath))
            .Select(f => new ImpactedFileDetail(
                FilePath: f.FilePath,
                ChangeType: f.ChangeType.ToString(),
                Reason: f.Reason))
            .ToList() ?? new List<ImpactedFileDetail>();

        var agentRequest = new DeveloperAgentRequest(
            TaskId: context.TaskId,
            ExecutionId: context.ExecutionId,
            TaskTitle: context.TaskTitle,
            TaskDescription: context.TaskDescription,
            AcceptanceCriteria: context.AcceptanceCriteria,
            ImpactAnalysisSummary: summary,
            ProposedPlan: proposedPlanText,
            ImpactedFilePaths: impactedFiles,
            WorkspacePath: prepResult.WorkspacePath,
            BranchName: prepResult.BranchName,
            ImpactedFiles: impactedFileDetails,
            Model: analysis.Model);

        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: invoking DeveloperAgent for execution {ExecutionId} (Task {TaskId}).",
            context.ExecutionId,
            context.TaskId);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.DeveloperAgent,
            ExecutionActivityStatus.Started,
            "Developer Agent started.",
            new ExecutionActivityMetadata(Model: analysis.Model, EventKind: "GeneratingChange"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var agentResult = await _developerAgent
            .GenerateAndApplyEditsAsync(agentRequest, cancellationToken)
            .ConfigureAwait(false);

        var actualModel = agentResult.Model ?? analysis.Model;
        if (!string.IsNullOrWhiteSpace(actualModel))
        {
            await _executionRepository.SetModelAsync(context.ExecutionId, actualModel, cancellationToken).ConfigureAwait(false);
        }

        if (!agentResult.Success)
        {
            var agentError = $"Developer Agent failed: {agentResult.ErrorMessage ?? "Developer Agent failed to generate or apply edits."}";
            _logger.LogError(
                "GitWorkspaceExecutionProcessor: DeveloperAgent execution failed for {ExecutionId}. Error: {Error}",
                context.ExecutionId,
                agentError);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.DeveloperAgent,
                ExecutionActivityStatus.Failed,
                agentError,
                actualModel != null ? new ExecutionActivityMetadata(Model: actualModel) : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(agentError);
        }

        // Zero modified files check (must produce at least one change for coding pipeline MVP)
        if (agentResult.ModifiedFiles == null || agentResult.ModifiedFiles.Count == 0)
        {
            const string zeroFilesError = "Developer Agent failed: Developer Agent returned success but produced zero modified files.";
            _logger.LogError(
                "GitWorkspaceExecutionProcessor: {Error} ExecutionId={ExecutionId}",
                zeroFilesError,
                context.ExecutionId);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.DeveloperAgent,
                ExecutionActivityStatus.Failed,
                zeroFilesError,
                actualModel != null ? new ExecutionActivityMetadata(Model: actualModel) : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(zeroFilesError);
        }

        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: DeveloperAgent successfully applied {Count} modified files for execution {ExecutionId}.",
            agentResult.ModifiedFiles.Count,
            context.ExecutionId);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.DeveloperAgent,
            ExecutionActivityStatus.Completed,
            "Developer Agent completed.",
            new ExecutionActivityMetadata(
                ModifiedFileCount: agentResult.ModifiedFiles.Count,
                Model: actualModel,
                EventKind: "GeneratingChange"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // ── Stage 6. Validate Build (with multi-round Compiler-Diagnostic Repair Loop) ──
        var validationRequest = new ExecutionValidationRequest(
            WorkspacePath: prepResult.WorkspacePath,
            BranchName: prepResult.BranchName,
            TargetPath: null);

        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: starting build validation for execution {ExecutionId}.",
            context.ExecutionId);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Started,
            "Build started.",
            new ExecutionActivityMetadata(EventKind: "VerifyingRepository"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var initialBuildStopwatch = Stopwatch.StartNew();
        var buildResult = await _validationRunner
            .ValidateBuildAsync(validationRequest, cancellationToken)
            .ConfigureAwait(false);
        initialBuildStopwatch.Stop();

        int compileRepairRound = 0;
        var modifiedFiles = new HashSet<string>(agentResult.ModifiedFiles, StringComparer.OrdinalIgnoreCase);
        string? previousBuildFailureFingerprint = null;

        while (!buildResult.Success && compileRepairRound < _maxCompileRepairRounds)
        {
            compileRepairRound++;
            var compilerEvidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
                buildResult.StdOut,
                buildResult.StdErr,
                buildResult.ErrorMessage);
            var rawBuildError = buildResult.ErrorMessage ?? "dotnet build failed.";
            _logger.LogWarning(
                "GitWorkspaceExecutionProcessor: build validation failed (round {Round}/{MaxRounds}) for execution {ExecutionId}. Error: {Error}. Checking for repairable compiler diagnostics.",
                compileRepairRound,
                _maxCompileRepairRounds,
                context.ExecutionId,
                rawBuildError);

            if (string.Equals(previousBuildFailureFingerprint, compilerEvidence.FailureFingerprint, StringComparison.Ordinal))
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Failed,
                    "Stopped with evidence: compile repair made no diagnostic progress.",
                    new ExecutionActivityMetadata(
                        EventKind: "StoppedWithEvidence",
                        RepairKind: "Compile",
                        RepairRound: compileRepairRound,
                        FailureFingerprint: compilerEvidence.FailureFingerprint,
                        ProgressResult: "SameFailure"),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            var compilerErrors = compilerEvidence.DiagnosticLines.ToList();
            var correlatedFiles = ExecutionDiagnosticEvidence
                .SelectCompilerRepairFiles(compilerEvidence, modifiedFiles)
                .ToList();

            if (correlatedFiles.Count == 0)
            {
                _logger.LogWarning("GitWorkspaceExecutionProcessor: no modified files could be exactly correlated to build error for execution {ExecutionId}; broad repair is disabled.", context.ExecutionId);
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Failed,
                    "Stopped with evidence: compiler diagnostics could not be correlated to a touched file.",
                    new ExecutionActivityMetadata(
                        EventKind: "StoppedWithEvidence",
                        RepairKind: "Compile",
                        RepairRound: compileRepairRound,
                        FailureFingerprint: compilerEvidence.FailureFingerprint,
                        ProgressResult: "Uncorrelated"),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            var formattedErrors = compilerErrors.Take(5).Select(e =>
            {
                var match = System.Text.RegularExpressions.Regex.Match(e, @"(CS\d{4}:\s*[^\[\r\n]+)");
                return match.Success ? match.Groups[1].Value.Trim() : e.Trim();
            }).ToList();

            var repairSummary = new System.Text.StringBuilder();
            repairSummary.AppendLine($"Build failed — {compilerErrors.Count} compiler error(s)");
            foreach (var err in formattedErrors)
            {
                repairSummary.AppendLine(err);
            }
            repairSummary.AppendLine($"Repair round {compileRepairRound}/{_maxCompileRepairRounds}");
            repairSummary.AppendLine("Repairing:");
            foreach (var file in correlatedFiles)
            {
                repairSummary.AppendLine($"- {Path.GetFileName(file)}");
            }

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Started,
                "Compile repair started.",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Started,
                repairSummary.ToString().TrimEnd(),
                new ExecutionActivityMetadata(
                    EventKind: "FixingBuildIssue",
                    ModifiedFileCount: correlatedFiles.Count,
                    RepairKind: "Compile",
                    RepairRound: compileRepairRound,
                    RepairFiles: correlatedFiles,
                    FailureFingerprint: compilerEvidence.FailureFingerprint),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var availablePorts = RoslynContractExtractor.GetAvailablePortDescriptions(prepResult.WorkspacePath, correlatedFiles);
            var repairPromptDesc = $"Fix the following compilation error(s) in generated files (repair round {compileRepairRound}/{_maxCompileRepairRounds}):\n{string.Join("\n", compilerErrors.Take(10))}";
            if (!string.IsNullOrWhiteSpace(availablePorts))
            {
                repairPromptDesc += $"\n\n=== Available Repository / Port Abstractions in Codebase ===\n{availablePorts}\nCRITICAL DIRECTIVE:\nYou MUST use ONLY existing available abstractions or implement the query using existing ports. Do NOT invent non-existent types.";
            }

            var repairRequest = new DeveloperAgentRequest(
                TaskId: context.TaskId,
                ExecutionId: context.ExecutionId,
                TaskTitle: $"Repair build errors for {context.TaskTitle} (round {compileRepairRound})",
                TaskDescription: repairPromptDesc,
                AcceptanceCriteria: "Resolve all compiler errors in the modified files without inventing non-existent abstractions.",
                ImpactAnalysisSummary: analysis?.Summary ?? "Compile repair",
                ProposedPlan: "Repair compilation errors",
                ImpactedFilePaths: correlatedFiles,
                WorkspacePath: prepResult.WorkspacePath,
                BranchName: prepResult.BranchName,
                ImpactedFiles: correlatedFiles.Select(f => new ImpactedFileDetail(f, "Modify", "Fix compilation error")).ToList(),
                Model: actualModel);

            var beforeRepairFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var repairStopwatch = Stopwatch.StartNew();

            try
            {
                var repairResult = await _developerAgent.GenerateAndApplyEditsAsync(repairRequest, cancellationToken).ConfigureAwait(false);
                repairStopwatch.Stop();
                if (repairResult.Success)
                {
                    if (repairResult.ModifiedFiles != null)
                    {
                        foreach (var mf in repairResult.ModifiedFiles)
                        {
                            modifiedFiles.Add(mf);
                        }
                    }

                    var afterRepairFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
                    var noDiff = beforeRepairFingerprint != null &&
                                 afterRepairFingerprint != null &&
                                 string.Equals(beforeRepairFingerprint, afterRepairFingerprint, StringComparison.Ordinal);

                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Build,
                        noDiff ? ExecutionActivityStatus.Failed : ExecutionActivityStatus.Completed,
                        noDiff
                            ? "Stopped with evidence: compile repair produced no worktree change."
                            : "Focused compile repair applied.",
                        new ExecutionActivityMetadata(
                            EventKind: noDiff ? "StoppedWithEvidence" : "FixingBuildIssue",
                            StageDurationMs: repairStopwatch.ElapsedMilliseconds,
                            RepairKind: "Compile",
                            RepairRound: compileRepairRound,
                            RepairFiles: correlatedFiles,
                            FailureFingerprint: compilerEvidence.FailureFingerprint,
                            BeforeChangeFingerprint: beforeRepairFingerprint,
                            AfterChangeFingerprint: afterRepairFingerprint,
                            ProgressResult: noDiff ? "NoDiff" : "Changed"),
                        cancellationToken).ConfigureAwait(false);

                    if (noDiff)
                    {
                        break;
                    }

                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.DeveloperAgent,
                        ExecutionActivityStatus.Completed,
                        compileRepairRound == 1 ? "Compile repair completed." : $"Compile repair completed (round {compileRepairRound}).",
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Build,
                        ExecutionActivityStatus.Started,
                        compileRepairRound == 1 ? "Build retry started." : $"Build retry started (round {compileRepairRound}).",
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "GitWorkspaceExecutionProcessor: compile repair round {Round} applied. Re-running build validation for execution {ExecutionId}.",
                        compileRepairRound,
                        context.ExecutionId);

                    buildResult = await _validationRunner
                        .ValidateBuildAsync(validationRequest, cancellationToken)
                        .ConfigureAwait(false);
                    previousBuildFailureFingerprint = compilerEvidence.FailureFingerprint;

                    if (buildResult.Success)
                    {
                        await SafeRecordActivityAsync(
                            context.ExecutionId,
                            ExecutionStage.Build,
                            ExecutionActivityStatus.Completed,
                            "Build retry passed.",
                            new ExecutionActivityMetadata(BuildPassed: true),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    else
                    {
                        await SafeRecordActivityAsync(
                            context.ExecutionId,
                            ExecutionStage.Build,
                            ExecutionActivityStatus.Failed,
                            compileRepairRound == 1 ? "Build retry failed." : $"Build retry failed (round {compileRepairRound}).",
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Build,
                        ExecutionActivityStatus.Failed,
                        $"Compile repair failed: {repairResult.ErrorMessage}",
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GitWorkspaceExecutionProcessor: compile repair round threw an exception for execution {ExecutionId}.", context.ExecutionId);
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Failed,
                    $"Compile repair failed with exception: {ex.Message}",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        if (!buildResult.Success)
        {
            var buildError = $"Build validation failed: {buildResult.ErrorMessage ?? "dotnet build failed."}";
            _logger.LogError(
                "GitWorkspaceExecutionProcessor: build validation failed for execution {ExecutionId}. Error: {Error}",
                context.ExecutionId,
                buildError);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Build,
                ExecutionActivityStatus.Failed,
                buildError,
                new ExecutionActivityMetadata(BuildPassed: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Halt immediately: DO NOT run test validation if build fails
            throw new InvalidOperationException(buildError);
        }

        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: build validation succeeded for execution {ExecutionId}.",
            context.ExecutionId);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Completed,
            "Build passed.",
            new ExecutionActivityMetadata(
                BuildPassed: true,
                EventKind: "VerifyingRepository",
                StageDurationMs: initialBuildStopwatch.ElapsedMilliseconds),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // ── Stage 7. Validate Test (with multi-round Test-Diagnostic Repair Loop) ──
        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: starting test validation for execution {ExecutionId}.",
            context.ExecutionId);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Test,
            ExecutionActivityStatus.Started,
            "Test started.",
            new ExecutionActivityMetadata(EventKind: "VerifyingRepository"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var initialTestStopwatch = Stopwatch.StartNew();
        var fullTestRequest = validationRequest with { SkipBuild = true, TestFilter = null };
        var testResult = await _validationRunner
            .ValidateTestAsync(fullTestRequest, cancellationToken)
            .ConfigureAwait(false);
        initialTestStopwatch.Stop();

        int testRepairRound = 0;
        string? previousTestFailureFingerprint = null;
        while (!testResult.Success && testRepairRound < _maxTestRepairRounds)
        {
            testRepairRound++;
            var testEvidence = ExecutionDiagnosticEvidence.ParseTestFailure(
                testResult.StdOut,
                testResult.StdErr,
                testResult.ErrorMessage);
            var rawTestError = testResult.ErrorMessage ?? "dotnet test failed.";
            _logger.LogWarning(
                "GitWorkspaceExecutionProcessor: test validation failed (round {Round}/{MaxRounds}) for execution {ExecutionId}. Error: {Error}. Attempting bounded test repair.",
                testRepairRound,
                _maxTestRepairRounds,
                context.ExecutionId,
                rawTestError);

            if (string.Equals(previousTestFailureFingerprint, testEvidence.FailureFingerprint, StringComparison.Ordinal))
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Failed,
                    "Stopped with evidence: focused test repair made no diagnostic progress.",
                    new ExecutionActivityMetadata(
                        EventKind: "StoppedWithEvidence",
                        RepairKind: "Test",
                        RepairRound: testRepairRound,
                        FailureFingerprint: testEvidence.FailureFingerprint,
                        ProgressResult: "SameFailure"),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            var testFailures = testEvidence.RelevantLines.ToList();
            var repairFiles = ExecutionDiagnosticEvidence
                .SelectTestRepairFiles(testEvidence, modifiedFiles)
                .ToList();
            if (repairFiles.Count == 0)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Failed,
                    "Stopped with evidence: failing test could not be correlated to a touched file.",
                    new ExecutionActivityMetadata(
                        EventKind: "StoppedWithEvidence",
                        RepairKind: "Test",
                        RepairRound: testRepairRound,
                        FailureFingerprint: testEvidence.FailureFingerprint,
                        ProgressResult: "Uncorrelated"),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            var conciseFailures = testFailures.Take(5).Select(f => f.Trim()).ToList();
            var testRepairSummary = new System.Text.StringBuilder();
            testRepairSummary.AppendLine("Failing test evidence:");
            foreach (var f in conciseFailures)
            {
                testRepairSummary.AppendLine(f);
            }
            testRepairSummary.AppendLine($"Repair round {testRepairRound}/{_maxTestRepairRounds}");

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                ExecutionActivityStatus.Started,
                $"Test repair started (round {testRepairRound}/{_maxTestRepairRounds}).",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                ExecutionActivityStatus.Started,
                testRepairSummary.ToString().TrimEnd(),
                new ExecutionActivityMetadata(
                    EventKind: "FixingFailingTest",
                    ModifiedFileCount: repairFiles.Count,
                    RepairKind: "Test",
                    RepairRound: testRepairRound,
                    RepairFiles: repairFiles,
                    FailureFingerprint: testEvidence.FailureFingerprint),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var testRepairPromptDesc = $"Fix the failing test(s) or implementation to satisfy requirements (test repair round {testRepairRound}/{_maxTestRepairRounds}):\n{string.Join("\n", testFailures)}\n\nCRITICAL DIRECTIVE:\nExisting repository tests are authoritative safety invariants. You MUST NOT delete, skip, comment out, or weaken existing tests. Fix the implementation or new test code to make all tests pass.";

            var testRepairRequest = new DeveloperAgentRequest(
                TaskId: context.TaskId,
                ExecutionId: context.ExecutionId,
                TaskTitle: $"Repair test failures for {context.TaskTitle} (round {testRepairRound})",
                TaskDescription: testRepairPromptDesc,
                AcceptanceCriteria: "Resolve all test failures so all tests pass cleanly without weakening existing test assertions.",
                ImpactAnalysisSummary: analysis?.Summary ?? "Test repair",
                ProposedPlan: "Repair test failures",
                ImpactedFilePaths: repairFiles,
                WorkspacePath: prepResult.WorkspacePath,
                BranchName: prepResult.BranchName,
                ImpactedFiles: repairFiles.Select(f => new ImpactedFileDetail(f, "Modify", "Fix test failure")).ToList(),
                Model: actualModel);

            var beforeRepairFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
            var repairStopwatch = Stopwatch.StartNew();

            try
            {
                var repairResult = await _developerAgent.GenerateAndApplyEditsAsync(testRepairRequest, cancellationToken).ConfigureAwait(false);
                repairStopwatch.Stop();
                if (repairResult.Success)
                {
                    if (repairResult.ModifiedFiles != null)
                    {
                        foreach (var mf in repairResult.ModifiedFiles)
                        {
                            modifiedFiles.Add(mf);
                        }
                    }

                    var afterRepairFingerprint = await GetChangeFingerprintAsync(prepResult.WorkspacePath, cancellationToken).ConfigureAwait(false);
                    var noDiff = beforeRepairFingerprint != null &&
                                 afterRepairFingerprint != null &&
                                 string.Equals(beforeRepairFingerprint, afterRepairFingerprint, StringComparison.Ordinal);

                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Test,
                        noDiff ? ExecutionActivityStatus.Failed : ExecutionActivityStatus.Completed,
                        noDiff
                            ? "Stopped with evidence: test repair produced no worktree change."
                            : "Focused test repair applied.",
                        new ExecutionActivityMetadata(
                            EventKind: noDiff ? "StoppedWithEvidence" : "FixingFailingTest",
                            StageDurationMs: repairStopwatch.ElapsedMilliseconds,
                            RepairKind: "Test",
                            RepairRound: testRepairRound,
                            RepairFiles: repairFiles,
                            FailureFingerprint: testEvidence.FailureFingerprint,
                            BeforeChangeFingerprint: beforeRepairFingerprint,
                            AfterChangeFingerprint: afterRepairFingerprint,
                            ProgressResult: noDiff ? "NoDiff" : "Changed"),
                        cancellationToken).ConfigureAwait(false);

                    if (noDiff)
                    {
                        break;
                    }

                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.DeveloperAgent,
                        ExecutionActivityStatus.Completed,
                        $"Test repair completed (round {testRepairRound}).",
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    // Must verify build passes first after test repair
                    var postRepairBuild = await _validationRunner.ValidateBuildAsync(validationRequest, cancellationToken).ConfigureAwait(false);
                    if (!postRepairBuild.Success)
                    {
                        _logger.LogWarning("GitWorkspaceExecutionProcessor: build failed after test repair round {Round}.", testRepairRound);
                        var buildEvidence = ExecutionDiagnosticEvidence.ParseCompilerFailure(
                            postRepairBuild.StdOut,
                            postRepairBuild.StdErr,
                            postRepairBuild.ErrorMessage);
                        await SafeRecordActivityAsync(
                            context.ExecutionId,
                            ExecutionStage.Build,
                            ExecutionActivityStatus.Failed,
                            "Stopped with evidence: build failed after focused test repair.",
                            new ExecutionActivityMetadata(
                                EventKind: "StoppedWithEvidence",
                                BuildPassed: false,
                                RepairKind: "Compile",
                                RepairRound: testRepairRound,
                                FailureFingerprint: buildEvidence.FailureFingerprint,
                                ProgressResult: "NewBuildFailure"),
                            cancellationToken).ConfigureAwait(false);

                        throw new InvalidOperationException(
                            $"Build validation failed after test repair: {postRepairBuild.ErrorMessage ?? buildEvidence.DiagnosticLines.FirstOrDefault() ?? "dotnet build failed."}");
                    }

                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Test,
                        ExecutionActivityStatus.Started,
                        $"Test retry started (round {testRepairRound}).",
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "GitWorkspaceExecutionProcessor: test repair round {Round} applied. Re-running test validation for execution {ExecutionId}.",
                        testRepairRound,
                        context.ExecutionId);

                    if (testEvidence.HasReliableTestName)
                    {
                        var targetedRequest = validationRequest with
                        {
                            SkipBuild = true,
                            TestFilter = testEvidence.TestName
                        };
                        testResult = await _validationRunner
                            .ValidateTestAsync(targetedRequest, cancellationToken)
                            .ConfigureAwait(false);

                        if (testResult.Success)
                        {
                            await SafeRecordActivityAsync(
                                context.ExecutionId,
                                ExecutionStage.Test,
                                ExecutionActivityStatus.Completed,
                                "Targeted failing test passed; running full test suite.",
                                new ExecutionActivityMetadata(
                                    EventKind: "VerifyingRepository",
                                    RepairKind: "Test",
                                    RepairRound: testRepairRound,
                                    FailureFingerprint: testEvidence.FailureFingerprint,
                                    ProgressResult: "TargetPassed"),
                                cancellationToken).ConfigureAwait(false);

                            testResult = await _validationRunner
                                .ValidateTestAsync(fullTestRequest, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        testResult = await _validationRunner
                            .ValidateTestAsync(fullTestRequest, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    previousTestFailureFingerprint = testEvidence.FailureFingerprint;

                    if (testResult.Success)
                    {
                        await SafeRecordActivityAsync(
                            context.ExecutionId,
                            ExecutionStage.Test,
                            ExecutionActivityStatus.Completed,
                            "Test retry passed.",
                            new ExecutionActivityMetadata(TestPassed: true),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    else
                    {
                        await SafeRecordActivityAsync(
                            context.ExecutionId,
                            ExecutionStage.Test,
                            ExecutionActivityStatus.Failed,
                            $"Test retry failed (round {testRepairRound}).",
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await SafeRecordActivityAsync(
                        context.ExecutionId,
                        ExecutionStage.Test,
                        ExecutionActivityStatus.Failed,
                        $"Test repair failed: {repairResult.ErrorMessage}",
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Build validation failed after test repair:", StringComparison.Ordinal))
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GitWorkspaceExecutionProcessor: test repair round threw an exception for execution {ExecutionId}.", context.ExecutionId);
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Test,
                    ExecutionActivityStatus.Failed,
                    $"Test repair failed with exception: {ex.Message}",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        if (!testResult.Success)
        {
            var testError = $"Test validation failed: {testResult.ErrorMessage ?? "dotnet test failed."}";
            _logger.LogError(
                "GitWorkspaceExecutionProcessor: test validation failed for execution {ExecutionId}. Error: {Error}",
                context.ExecutionId,
                testError);

            await SafeRecordActivityAsync(
                context.ExecutionId,
                ExecutionStage.Test,
                ExecutionActivityStatus.Failed,
                testError,
                new ExecutionActivityMetadata(TestPassed: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(testError);
        }

        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: all pipeline stages succeeded for execution {ExecutionId}.",
            context.ExecutionId);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Test,
            ExecutionActivityStatus.Completed,
            "Tests passed.",
            new ExecutionActivityMetadata(
                TestPassed: true,
                EventKind: "ReadyForReview",
                StageDurationMs: initialTestStopwatch.ElapsedMilliseconds),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetChangeFingerprintAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        if (_changeFingerprintCalculator == null)
        {
            return null;
        }

        var result = await _changeFingerprintCalculator
            .ComputeFingerprintAsync(workspacePath, cancellationToken)
            .ConfigureAwait(false);

        return result.Success ? result.Fingerprint : null;
    }

    private static string BuildProposedPlanText(TaskImpactAnalysis analysis)
    {
        if (analysis.StructuredResult?.ProposedPlan != null && analysis.StructuredResult.ProposedPlan.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var step in analysis.StructuredResult.ProposedPlan)
            {
                sb.AppendLine($"Step {step.Order}: {step.Title} - {step.Description}");
            }
            return sb.ToString().TrimEnd();
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
            await _activityRecorder.RecordActivityAsync(
                executionId, stage, status, message, metadata, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "GitWorkspaceExecutionProcessor: unexpected error recording activity for execution {ExecutionId}.",
                executionId);
        }
    }
}
