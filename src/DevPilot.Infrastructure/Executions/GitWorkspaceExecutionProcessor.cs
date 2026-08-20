using System.Text;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Infrastructure.DeveloperAgent;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.Executions;

/// <summary>
/// Execution processor that orchestrates the real Developer Agent execution pipeline:
/// Prepare Workspace → Verify Clean → Run Developer Agent → Build Validation → Test Validation.
/// </summary>
public sealed class GitWorkspaceExecutionProcessor : IExecutionProcessor
{
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IExecutionRepository _executionRepository;
    private readonly IImpactAnalysisRepository _impactAnalysisRepository;
    private readonly IDeveloperAgent _developerAgent;
    private readonly IExecutionValidationRunner _validationRunner;
    private readonly IExecutionActivityRecorder _activityRecorder;
    private readonly ILogger<GitWorkspaceExecutionProcessor> _logger;

    public GitWorkspaceExecutionProcessor(
        IExecutionWorkspaceManager workspaceManager,
        IExecutionRepository executionRepository,
        IImpactAnalysisRepository impactAnalysisRepository,
        IDeveloperAgent developerAgent,
        IExecutionValidationRunner validationRunner,
        IExecutionActivityRecorder activityRecorder,
        ILogger<GitWorkspaceExecutionProcessor> logger)
    {
        _workspaceManager = workspaceManager;
        _executionRepository = executionRepository;
        _impactAnalysisRepository = impactAnalysisRepository;
        _developerAgent = developerAgent;
        _validationRunner = validationRunner;
        _activityRecorder = activityRecorder;
        _logger = logger;
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
            analysis.Model != null ? new ExecutionActivityMetadata(Model: analysis.Model) : null,
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
            new ExecutionActivityMetadata(ModifiedFileCount: agentResult.ModifiedFiles.Count, Model: actualModel),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // ── Stage 6. Validate Build ───────────────────────────────────────────────
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
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var buildResult = await _validationRunner
            .ValidateBuildAsync(validationRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!buildResult.Success)
        {
            var fullDiagnosticOutput = $"{buildResult.StdOut}\n{buildResult.StdErr}\n{buildResult.ErrorMessage}";
            var rawBuildError = buildResult.ErrorMessage ?? "dotnet build failed.";
            _logger.LogWarning(
                "GitWorkspaceExecutionProcessor: initial build validation failed for execution {ExecutionId}. Error: {Error}. Checking for repairable compiler diagnostics.",
                context.ExecutionId,
                rawBuildError);

            var compilerErrors = fullDiagnosticOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("error CS", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

            var correlatedFiles = agentResult.ModifiedFiles
                .Where(f => compilerErrors.Any(err => err.Contains(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase) || err.Contains(f.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (correlatedFiles.Count > 0 && compilerErrors.Count > 0)
            {
                await SafeRecordActivityAsync(
                    context.ExecutionId,
                    ExecutionStage.Build,
                    ExecutionActivityStatus.Started,
                    "Compile repair started.",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var availablePorts = RoslynContractExtractor.GetAvailablePortDescriptions(prepResult.WorkspacePath, correlatedFiles);
                var repairPromptDesc = $"Fix the following compilation error(s) in generated files:\n{string.Join("\n", compilerErrors.Take(10))}";
                if (!string.IsNullOrWhiteSpace(availablePorts))
                {
                    repairPromptDesc += $"\n\n=== Available Repository / Port Abstractions in Codebase ===\n{availablePorts}\nCRITICAL DIRECTIVE:\nYou MUST use ONLY existing available abstractions or implement the query using existing ports. Do NOT invent non-existent types.";
                }

                var repairRequest = new DeveloperAgentRequest(
                    TaskId: context.TaskId,
                    ExecutionId: context.ExecutionId,
                    TaskTitle: $"Repair build errors for {context.TaskTitle}",
                    TaskDescription: repairPromptDesc,
                    AcceptanceCriteria: "Resolve all compiler errors in the modified files without inventing non-existent abstractions.",
                    ImpactAnalysisSummary: analysis?.Summary ?? "Compile repair",
                    ProposedPlan: "Repair compilation errors",
                    ImpactedFilePaths: correlatedFiles,
                    WorkspacePath: prepResult.WorkspacePath,
                    BranchName: prepResult.BranchName,
                    ImpactedFiles: correlatedFiles.Select(f => new ImpactedFileDetail(f, "Modify", "Fix compilation error")).ToList(),
                    Model: actualModel);

                try
                {
                    var repairResult = await _developerAgent.GenerateAndApplyEditsAsync(repairRequest, cancellationToken).ConfigureAwait(false);
                    if (repairResult.Success)
                    {
                        await SafeRecordActivityAsync(
                            context.ExecutionId,
                            ExecutionStage.DeveloperAgent,
                            ExecutionActivityStatus.Completed,
                            "Compile repair completed.",
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        await SafeRecordActivityAsync(
                            context.ExecutionId,
                            ExecutionStage.Build,
                            ExecutionActivityStatus.Started,
                            "Build retry started.",
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        _logger.LogInformation(
                            "GitWorkspaceExecutionProcessor: compile repair applied. Re-running build validation for execution {ExecutionId}.",
                            context.ExecutionId);

                        buildResult = await _validationRunner
                            .ValidateBuildAsync(validationRequest, cancellationToken)
                            .ConfigureAwait(false);

                        if (buildResult.Success)
                        {
                            await SafeRecordActivityAsync(
                                context.ExecutionId,
                                ExecutionStage.Build,
                                ExecutionActivityStatus.Completed,
                                "Build retry passed.",
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await SafeRecordActivityAsync(
                                context.ExecutionId,
                                ExecutionStage.Build,
                                ExecutionActivityStatus.Failed,
                                "Build retry failed.",
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
        }

        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: build validation succeeded for execution {ExecutionId}.",
            context.ExecutionId);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Build,
            ExecutionActivityStatus.Completed,
            "Build passed.",
            new ExecutionActivityMetadata(BuildPassed: true),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // ── Stage 7. Validate Test ────────────────────────────────────────────────
        _logger.LogInformation(
            "GitWorkspaceExecutionProcessor: starting test validation for execution {ExecutionId}.",
            context.ExecutionId);

        await SafeRecordActivityAsync(
            context.ExecutionId,
            ExecutionStage.Test,
            ExecutionActivityStatus.Started,
            "Test started.",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var testResult = await _validationRunner
            .ValidateTestAsync(validationRequest, cancellationToken)
            .ConfigureAwait(false);

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
            new ExecutionActivityMetadata(TestPassed: true),
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
