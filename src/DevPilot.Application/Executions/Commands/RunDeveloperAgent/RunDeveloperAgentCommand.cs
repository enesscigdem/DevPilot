using System.Text;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using DevPilot.Application.Executions.Ports;
using DevPilot.Application.TaskImpactAnalysis.Ports;
using DevPilot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DevPilot.Application.Executions.Commands.RunDeveloperAgent;

public sealed record RunDeveloperAgentCommand(Guid ExecutionId);

public sealed class RunDeveloperAgentResult
{
    public bool Success { get; set; }

    /// <summary>True when the execution or linked task was not found.</summary>
    public bool NotFound { get; set; }

    /// <summary>True when state validation fails (missing workspace, wrong branch, dirty worktree, missing/incomplete impact analysis).</summary>
    public bool Conflict { get; set; }

    public string? ErrorMessage { get; set; }

    public IReadOnlyList<string>? ModifiedFiles { get; set; }
}

public interface IRunDeveloperAgentCommandHandler
{
    Task<RunDeveloperAgentResult> HandleAsync(
        RunDeveloperAgentCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class RunDeveloperAgentCommandHandler : IRunDeveloperAgentCommandHandler
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IImpactAnalysisRepository _impactAnalysisRepository;
    private readonly IExecutionWorkspaceManager _workspaceManager;
    private readonly IDeveloperAgent _developerAgent;
    private readonly ILogger<RunDeveloperAgentCommandHandler> _logger;

    public RunDeveloperAgentCommandHandler(
        IExecutionRepository executionRepository,
        IImpactAnalysisRepository impactAnalysisRepository,
        IExecutionWorkspaceManager workspaceManager,
        IDeveloperAgent developerAgent,
        ILogger<RunDeveloperAgentCommandHandler> logger)
    {
        _executionRepository = executionRepository;
        _impactAnalysisRepository = impactAnalysisRepository;
        _workspaceManager = workspaceManager;
        _developerAgent = developerAgent;
        _logger = logger;
    }

    public async Task<RunDeveloperAgentResult> HandleAsync(
        RunDeveloperAgentCommand command,
        CancellationToken cancellationToken = default)
    {
        // 1. Load execution
        var execution = await _executionRepository
            .GetByIdAsync(command.ExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return new RunDeveloperAgentResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = $"Task execution '{command.ExecutionId}' was not found.",
            };
        }

        // Prevent manual endpoint from interfering with an automatic Pending/Running execution owned by worker
        if (execution.Status == TaskExecutionStatus.Pending || execution.Status == TaskExecutionStatus.Running)
        {
            return new RunDeveloperAgentResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = $"Task execution '{command.ExecutionId}' is currently '{execution.Status}' and owned by the automatic execution pipeline.",
            };
        }

        // 2. Verify WorkspacePath and BranchName exist on record
        if (string.IsNullOrWhiteSpace(execution.WorkspacePath) || string.IsNullOrWhiteSpace(execution.BranchName))
        {
            return new RunDeveloperAgentResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = "Task execution does not have a persisted workspace path or branch name.",
            };
        }

        // 3. Load DevelopmentTask
        var task = execution.DevelopmentTask;
        if (task is null)
        {
            return new RunDeveloperAgentResult
            {
                Success = false,
                NotFound = true,
                ErrorMessage = $"Linked DevelopmentTask for execution '{command.ExecutionId}' was not found.",
            };
        }

        // 4. Load completed persisted TaskImpactAnalysis
        var analysis = await _impactAnalysisRepository
            .GetLatestByTaskIdAsync(task.Id, cancellationToken)
            .ConfigureAwait(false);

        if (analysis is null || analysis.Status != ImpactAnalysisStatus.Completed)
        {
            return new RunDeveloperAgentResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = "A completed TaskImpactAnalysis is required before running the Developer Agent.",
            };
        }

        // 5. Verify execution worktree via workspace manager abstraction (exists, branch match, clean worktree)
        var verification = await _workspaceManager
            .VerifyWorkspaceStateAsync(execution.WorkspacePath, execution.BranchName, requireClean: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!verification.IsValid)
        {
            _logger.LogWarning(
                "RunDeveloperAgent: workspace verification failed for execution {ExecutionId}. Error: {Error}",
                command.ExecutionId,
                verification.ErrorMessage);

            return new RunDeveloperAgentResult
            {
                Success = false,
                Conflict = true,
                ErrorMessage = verification.ErrorMessage ?? "Execution workspace verification failed.",
            };
        }

        // 6. Build DeveloperAgentRequest
        var summary = !string.IsNullOrWhiteSpace(analysis.StructuredResult?.Summary)
            ? analysis.StructuredResult.Summary
            : analysis.Summary;

        var proposedPlanText = BuildProposedPlanText(analysis);

        var impactedFiles = analysis.StructuredResult?.ImpactedFiles?
            .Select(f => f.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList() ?? new List<string>();

        var agentRequest = new DeveloperAgentRequest(
            TaskId: task.Id,
            ExecutionId: execution.Id,
            TaskTitle: task.Title,
            TaskDescription: task.Description,
            AcceptanceCriteria: task.AcceptanceCriteria,
            ImpactAnalysisSummary: summary,
            ProposedPlan: proposedPlanText,
            ImpactedFilePaths: impactedFiles,
            WorkspacePath: execution.WorkspacePath,
            BranchName: execution.BranchName);

        _logger.LogInformation(
            "RunDeveloperAgent: invoking DeveloperAgent for execution {ExecutionId} (Task {TaskId}).",
            execution.Id,
            task.Id);

        // 7. Invoke existing IDeveloperAgent (AI call)
        var agentResult = await _developerAgent
            .GenerateAndApplyEditsAsync(agentRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!agentResult.Success)
        {
            _logger.LogWarning(
                "RunDeveloperAgent: DeveloperAgent execution failed for {ExecutionId}. Error: {Error}",
                execution.Id,
                agentResult.ErrorMessage);

            return new RunDeveloperAgentResult
            {
                Success = false,
                ErrorMessage = agentResult.ErrorMessage ?? "Developer Agent failed to generate or apply edits.",
            };
        }

        _logger.LogInformation(
            "RunDeveloperAgent: DeveloperAgent successfully applied {Count} modified files for execution {ExecutionId}.",
            agentResult.ModifiedFiles?.Count ?? 0,
            execution.Id);

        return new RunDeveloperAgentResult
        {
            Success = true,
            ModifiedFiles = agentResult.ModifiedFiles,
        };
    }

    private static string BuildProposedPlanText(Domain.Entities.TaskImpactAnalysis analysis)
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
}
