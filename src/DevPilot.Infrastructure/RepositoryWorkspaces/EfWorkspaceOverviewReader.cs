using System.Text.RegularExpressions;
using DevPilot.Application.RepositoryWorkspaces.Dtos;
using DevPilot.Application.RepositoryWorkspaces.Ports;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using DevPilot.Domain.ProjectBrain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.RepositoryWorkspaces;

public sealed class EfWorkspaceOverviewReader : IWorkspaceOverviewReader
{
    private readonly DevPilotDbContext _db;
    private readonly ILogger<EfWorkspaceOverviewReader> _logger;

    public EfWorkspaceOverviewReader(
        DevPilotDbContext db,
        ILogger<EfWorkspaceOverviewReader> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<WorkspaceOverviewDto?> ReadOverviewAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _db.RepositoryWorkspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId, cancellationToken)
            .ConfigureAwait(false);

        if (workspace is null)
        {
            return null;
        }

        var repoFullName = $"{workspace.Owner}/{workspace.Repository}";

        // 1. Index job & chunk metrics
        var latestIndexJob = await _db.IndexJobs
            .AsNoTracking()
            .Where(j => j.RepositoryWorkspaceId == workspaceId)
            .OrderByDescending(j => j.StartedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var chunkMetrics = await _db.CodeChunks
            .AsNoTracking()
            .Where(c => c.RepositoryWorkspaceId == workspaceId)
            .Select(c => new
            {
                c.RelativePath,
                c.TypeName,
                c.SymbolName,
                c.DeclaredSymbols,
                c.Language,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var distinctFilesCount = chunkMetrics
            .Select(c => c.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var distinctTypesCount = chunkMetrics
            .Where(c => !string.IsNullOrWhiteSpace(c.TypeName))
            .Select(c => c.TypeName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var distinctSymbolsCount = chunkMetrics
            .Where(c => !string.IsNullOrWhiteSpace(c.DeclaredSymbols))
            .SelectMany(c => c.DeclaredSymbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (distinctSymbolsCount == 0)
        {
            distinctSymbolsCount = chunkMetrics
                .Where(c => !string.IsNullOrWhiteSpace(c.SymbolName))
                .Select(c => c.SymbolName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        var distinctLanguages = chunkMetrics
            .Where(c => !string.IsNullOrWhiteSpace(c.Language))
            .Select(c => c.Language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var languageDisplay = distinctLanguages.Count > 0
            ? string.Join(" · ", distinctLanguages)
            : null;

        var lastIndexedAt = latestIndexJob?.CompletedAt ?? latestIndexJob?.StartedAt;
        var isIndexed = latestIndexJob != null && (latestIndexJob.Status == IndexJobStatus.Completed || distinctFilesCount > 0);
        var totalFiles = latestIndexJob != null && latestIndexJob.TotalFiles > 0 ? latestIndexJob.TotalFiles : distinctFilesCount;

        var header = new WorkspaceHeaderDto
        {
            WorkspaceId = workspace.Id,
            RepositoryFullName = repoFullName,
            Branch = workspace.Branch,
            FileCount = totalFiles,
            LastIndexedAt = lastIndexedAt,
            IsIndexed = isIndexed,
        };

        var recentlyAnalyzed = new WorkspaceAnalysisOverviewDto
        {
            RepositoryFullName = repoFullName,
            Language = languageDisplay,
            Loc = null, // LOC not cheaply persisted; truthful null
            SymbolsCount = distinctSymbolsCount,
            TypesCount = distinctTypesCount,
            ReferencesCount = null, // Reference resolution not persisted in chunks; truthful null
            LastIndexedAt = lastIndexedAt,
            IsIndexed = isIndexed,
        };

        // 2. Tasks & Impact Analyses
        var tasks = await _db.DevelopmentTasks
            .AsNoTracking()
            .Where(t => t.RepositoryWorkspaceId == workspaceId)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var taskIds = tasks.Select(t => t.Id).ToList();

        var rawAnalyses = await _db.TaskImpactAnalyses
            .AsNoTracking()
            .Where(a => taskIds.Contains(a.DevelopmentTaskId))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var analysesByTaskId = rawAnalyses
            .GroupBy(a => a.DevelopmentTaskId)
            .ToDictionary(g => g.Key, g => g.First());

        // 3. Executions & Activities
        var executions = await _db.TaskExecutions
            .AsNoTracking()
            .Where(e => taskIds.Contains(e.DevelopmentTaskId))
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var executionIds = executions.Select(e => e.Id).ToList();

        var rawActivities = await _db.ExecutionActivities
            .AsNoTracking()
            .Where(a => executionIds.Contains(a.ExecutionId))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var activitiesByExecutionId = rawActivities
            .GroupBy(a => a.ExecutionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAt).ToList());

        var taskMap = tasks.ToDictionary(t => t.Id);

        // 4. Active Execution Selection: Latest relevant NON-TERMINAL workflow
        bool IsTerminal(TaskExecution e)
        {
            if (e.MergeStatus == ExecutionMergeStatus.Merged ||
                e.PullRequestRemoteState == ExecutionPullRequestRemoteState.Merged)
                return true;

            if (e.Status == TaskExecutionStatus.Failed ||
                e.Status == TaskExecutionStatus.Cancelled)
                return true;

            if (e.ReviewStatus == ExecutionReviewStatus.Rejected)
                return true;

            if (e.CiStatus == ExecutionCiStatus.Failure)
                return true;

            if (e.PullRequestStatus == ExecutionPullRequestStatus.Failed)
                return true;

            return false;
        }

        var nonTerminalExecutions = executions.Where(e => !IsTerminal(e)).ToList();

        var activeExecution = nonTerminalExecutions
            .Where(e => e.Status == TaskExecutionStatus.Running)
            .OrderByDescending(e => e.StartedAt ?? e.CreatedAt)
            .FirstOrDefault()
            ?? nonTerminalExecutions
                .Where(e => e.Status == TaskExecutionStatus.Pending)
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefault()
            ?? nonTerminalExecutions
                .Where(e => e.Status == TaskExecutionStatus.Completed && e.ReviewStatus == ExecutionReviewStatus.Pending)
                .OrderByDescending(e => e.CompletedAt ?? e.CreatedAt)
                .FirstOrDefault()
            ?? nonTerminalExecutions
                .Where(e => e.Status == TaskExecutionStatus.Completed && e.ReviewStatus == ExecutionReviewStatus.Approved)
                .OrderByDescending(e => e.ReviewDecidedAt ?? e.CompletedAt ?? e.CreatedAt)
                .FirstOrDefault();

        WorkspaceActiveExecutionDto? activeExecutionDto = null;
        if (activeExecution is not null && taskMap.TryGetValue(activeExecution.DevelopmentTaskId, out var activeTask))
        {
            analysesByTaskId.TryGetValue(activeTask.Id, out var activeAnalysis);
            activitiesByExecutionId.TryGetValue(activeExecution.Id, out var execActivities);
            execActivities ??= new List<ExecutionActivity>();

            activeExecutionDto = BuildActiveExecutionDto(activeExecution, activeTask, activeAnalysis, execActivities);
        }

        // 5. Needs Your Attention (Max 3, prioritized)
        var needsAttention = BuildAttentionItems(tasks, executions, analysesByTaskId, activitiesByExecutionId);

        // 6. Awaiting Your Approval
        var awaitingApproval = BuildAwaitingApprovalItems(tasks, executions, analysesByTaskId);

        // 7. Failed or Blocked (Deduplicated per task)
        var failedOrBlocked = BuildFailedOrBlockedItems(tasks, executions, activitiesByExecutionId);

        // 8. Recent Engineering Activity
        var recentActivity = BuildRecentActivity(tasks, executions, rawActivities, workspaceId, cancellationToken);

        // 9. Shipped Recently (Merged only)
        var shippedRecently = BuildShippedRecently(executions, taskMap);

        return new WorkspaceOverviewDto
        {
            Header = header,
            NeedsAttention = needsAttention,
            ActiveExecution = activeExecutionDto,
            AwaitingApproval = awaitingApproval,
            FailedOrBlocked = failedOrBlocked,
            RecentActivity = recentActivity,
            RecentlyAnalyzed = recentlyAnalyzed,
            ShippedRecently = shippedRecently,
        };
    }

    private static WorkspaceActiveExecutionDto BuildActiveExecutionDto(
        TaskExecution execution,
        DevelopmentTask task,
        TaskImpactAnalysis? analysis,
        List<ExecutionActivity> activities)
    {
        var taskDisplayId = FormatTaskDisplayId(task.Id);

        // 7 stages: analyze, plan, approved, implement, build, review, pr
        var hasCompletedAnalysis = analysis is not null && analysis.Status == ImpactAnalysisStatus.Completed;
        var hasFailedAnalysis = analysis is not null && analysis.Status == ImpactAnalysisStatus.Failed;

        // Stage 1: Analyze
        var analyzeState = hasCompletedAnalysis
            ? WorkspaceStageState.Done
            : (hasFailedAnalysis ? WorkspaceStageState.Failed : (task.Status == DevelopmentTaskStatus.Analyzing ? WorkspaceStageState.Active : WorkspaceStageState.Done));

        // Stage 2: Plan
        var planState = hasCompletedAnalysis
            ? WorkspaceStageState.Done
            : (hasFailedAnalysis ? WorkspaceStageState.Failed : WorkspaceStageState.Todo);

        // Stage 3: Approved (explicit semantic states and execution invariants)
        var hasExecutionStarted = execution.Status is TaskExecutionStatus.Running
                                                   or TaskExecutionStatus.Completed
                                                   or TaskExecutionStatus.Failed
                                                   or TaskExecutionStatus.Cancelled
                                  || activities.Count > 0;

        var isExplicitlyApproved = task.Status is DevelopmentTaskStatus.Approved
                                               or DevelopmentTaskStatus.Executing
                                               or DevelopmentTaskStatus.Completed
                                  || hasExecutionStarted;

        var approvedState = isExplicitlyApproved
            ? WorkspaceStageState.Done
            : (task.Status == DevelopmentTaskStatus.Rejected ? WorkspaceStageState.Failed : (task.Status == DevelopmentTaskStatus.AwaitingApproval ? WorkspaceStageState.Active : WorkspaceStageState.Todo));

        // Stage 4: Implement (DeveloperAgent)
        var hasDevAgentCompleted = activities.Any(a => a.Stage == ExecutionStage.DeveloperAgent && a.Status == ExecutionActivityStatus.Completed);
        var hasDevAgentFailed = activities.Any(a => a.Stage == ExecutionStage.DeveloperAgent && a.Status == ExecutionActivityStatus.Failed);
        var hasDevAgentStarted = activities.Any(a => a.Stage == ExecutionStage.DeveloperAgent && a.Status == ExecutionActivityStatus.Started);

        WorkspaceStageState implementState;
        if (hasDevAgentCompleted)
        {
            implementState = WorkspaceStageState.Done;
        }
        else if (hasDevAgentFailed)
        {
            implementState = WorkspaceStageState.Failed;
        }
        else if (execution.Status == TaskExecutionStatus.Running && (hasDevAgentStarted || activities.All(a => a.Stage <= ExecutionStage.Workspace)))
        {
            implementState = WorkspaceStageState.Active;
        }
        else if (execution.Status == TaskExecutionStatus.Cancelled)
        {
            implementState = WorkspaceStageState.Blocked;
        }
        else
        {
            implementState = WorkspaceStageState.Todo;
        }

        // Stage 5: Build & Test (Collapsed)
        var buildPassed = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Completed);
        var buildFailed = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Failed);
        var buildStarted = activities.Any(a => a.Stage == ExecutionStage.Build && a.Status == ExecutionActivityStatus.Started);

        var testPassed = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Completed);
        var testFailed = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Failed);
        var testStarted = activities.Any(a => a.Stage == ExecutionStage.Test && a.Status == ExecutionActivityStatus.Started);

        WorkspaceStageState buildTestState;
        if (buildPassed && testPassed)
        {
            buildTestState = WorkspaceStageState.Done;
        }
        else if (buildFailed || testFailed)
        {
            buildTestState = WorkspaceStageState.Failed;
        }
        else if (execution.Status == TaskExecutionStatus.Running && (buildStarted || testStarted || (hasDevAgentCompleted && !buildPassed)))
        {
            buildTestState = WorkspaceStageState.Active;
        }
        else
        {
            buildTestState = WorkspaceStageState.Todo;
        }

        // Stage 6: Review
        WorkspaceStageState reviewState;
        if (execution.ReviewStatus == ExecutionReviewStatus.Approved)
        {
            reviewState = WorkspaceStageState.Done;
        }
        else if (execution.ReviewStatus == ExecutionReviewStatus.Rejected)
        {
            reviewState = WorkspaceStageState.Failed;
        }
        else if (execution.Status == TaskExecutionStatus.Completed && execution.ReviewStatus == ExecutionReviewStatus.Pending)
        {
            reviewState = WorkspaceStageState.Active;
        }
        else
        {
            reviewState = WorkspaceStageState.Todo;
        }

        // Stage 7: Pull Request
        WorkspaceStageState prState;
        if (execution.PullRequestStatus == ExecutionPullRequestStatus.Open || execution.MergeStatus == ExecutionMergeStatus.Merged)
        {
            prState = WorkspaceStageState.Done;
        }
        else if (execution.PullRequestStatus == ExecutionPullRequestStatus.Failed)
        {
            prState = WorkspaceStageState.Failed;
        }
        else if (execution.PullRequestStatus == ExecutionPullRequestStatus.InProgress || execution.ReviewStatus == ExecutionReviewStatus.Approved)
        {
            prState = WorkspaceStageState.Active;
        }
        else
        {
            prState = WorkspaceStageState.Todo;
        }

        var stageList = new List<WorkspaceStageStepDto>
        {
            new() { StageKey = "analyze", State = analyzeState },
            new() { StageKey = "plan", State = planState },
            new() { StageKey = "approved", State = approvedState },
            new() { StageKey = "implement", State = implementState },
            new() { StageKey = "build", State = buildTestState },
            new() { StageKey = "review", State = reviewState },
            new() { StageKey = "pr", State = prState },
        };

        // Determine current stage key
        var currentStageKey = "analyze";
        var activeIndex = stageList.FindIndex(s => s.State == WorkspaceStageState.Active || s.State == WorkspaceStageState.Failed);
        if (activeIndex >= 0)
        {
            currentStageKey = stageList[activeIndex].StageKey;
        }
        else
        {
            var lastDoneIndex = stageList.FindLastIndex(s => s.State == WorkspaceStageState.Done);
            currentStageKey = lastDoneIndex >= 0 ? stageList[lastDoneIndex].StageKey : "analyze";
        }

        int? elapsedSeconds = null;
        if (execution.StartedAt.HasValue)
        {
            var end = execution.CompletedAt ?? DateTime.UtcNow;
            elapsedSeconds = (int)Math.Max(0, (end - execution.StartedAt.Value).TotalSeconds);
        }

        // File touch count from metadata or analysis
        int? filesTouched = null;
        var devAgentCompletedActivity = activities.LastOrDefault(a => a.Stage == ExecutionStage.DeveloperAgent && a.MetadataJson != null);
        if (devAgentCompletedActivity?.MetadataJson != null && devAgentCompletedActivity.MetadataJson.Contains("ModifiedFileCount"))
        {
            try
            {
                var match = Regex.Match(devAgentCompletedActivity.MetadataJson, @"[""']?ModifiedFileCount[""']?\s*:\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
                {
                    filesTouched = count;
                }
            }
            catch
            {
                // ignore parse failure
            }
        }

        if (!filesTouched.HasValue && analysis?.StructuredResult?.ImpactedFiles != null)
        {
            filesTouched = analysis.StructuredResult.ImpactedFiles.Count;
        }

        return new WorkspaceActiveExecutionDto
        {
            ExecutionId = execution.Id,
            TaskId = task.Id,
            TaskDisplayId = taskDisplayId,
            TaskTitle = task.Title,
            CurrentStageKey = currentStageKey,
            Stages = stageList,
            StartedAt = execution.StartedAt,
            CompletedAt = execution.CompletedAt,
            ElapsedSeconds = elapsedSeconds,
            TokensUsed = null, // Truthful null
            EstimatedCost = null, // Truthful null
            ModifiedFileCount = filesTouched,
        };
    }

    private static List<WorkspaceAttentionItemDto> BuildAttentionItems(
        List<DevelopmentTask> tasks,
        List<TaskExecution> executions,
        Dictionary<Guid, TaskImpactAnalysis> analysesByTaskId,
        Dictionary<Guid, List<ExecutionActivity>> activitiesByExecutionId)
    {
        var items = new List<WorkspaceAttentionItemDto>();
        var taskMap = tasks.ToDictionary(t => t.Id);

        // 1. Failed Executions with specific failure kind
        foreach (var exec in executions.Where(e => e.Status == TaskExecutionStatus.Failed))
        {
            if (!taskMap.TryGetValue(exec.DevelopmentTaskId, out var task)) continue;
            activitiesByExecutionId.TryGetValue(exec.Id, out var acts);
            var lastFailedAct = acts?.LastOrDefault(a => a.Status == ExecutionActivityStatus.Failed);

            var (specificKind, title, meta) = DetermineAttentionFailureDetails(exec, lastFailedAct);

            items.Add(new WorkspaceAttentionItemDto
            {
                Id = $"att-exec-failed-{exec.Id}",
                Kind = specificKind,
                TaskId = task.Id,
                ExecutionId = exec.Id,
                TaskDisplayId = FormatTaskDisplayId(task.Id),
                Title = title,
                Reason = $"{FormatTaskDisplayId(task.Id)} · {task.Title}",
                MetaDetail = meta,
                OccurredAt = exec.CompletedAt ?? exec.CreatedAt,
            });
        }

        // 2. Pending Reviews (Completed execution awaiting human review decision)
        foreach (var exec in executions.Where(e => e.Status == TaskExecutionStatus.Completed && e.ReviewStatus == ExecutionReviewStatus.Pending))
        {
            if (!taskMap.TryGetValue(exec.DevelopmentTaskId, out var task)) continue;

            items.Add(new WorkspaceAttentionItemDto
            {
                Id = $"att-review-pending-{exec.Id}",
                Kind = WorkspaceAttentionKind.ReviewPending,
                TaskId = task.Id,
                ExecutionId = exec.Id,
                TaskDisplayId = FormatTaskDisplayId(task.Id),
                Title = "Review ready for approval",
                Reason = $"{FormatTaskDisplayId(task.Id)} · {task.Title}",
                MetaDetail = "Code changes ready · review pending",
                OccurredAt = exec.CompletedAt ?? exec.CreatedAt,
            });
        }

        // 3. Plan Approval Required (Task awaiting approval)
        foreach (var task in tasks.Where(t => t.Status == DevelopmentTaskStatus.AwaitingApproval))
        {
            analysesByTaskId.TryGetValue(task.Id, out var analysis);
            var fileCount = analysis?.StructuredResult?.ImpactedFiles?.Count;
            var meta = fileCount.HasValue ? $"{fileCount} files · ready for review" : "Plan ready for review";

            items.Add(new WorkspaceAttentionItemDto
            {
                Id = $"att-plan-approval-{task.Id}",
                Kind = WorkspaceAttentionKind.PlanApprovalRequired,
                TaskId = task.Id,
                TaskDisplayId = FormatTaskDisplayId(task.Id),
                Title = "Plan ready for review",
                Reason = $"{FormatTaskDisplayId(task.Id)} · {task.Title}",
                MetaDetail = meta,
                OccurredAt = analysis?.CompletedAt ?? task.UpdatedAt,
            });
        }

        // 4. Review Rejected
        foreach (var exec in executions.Where(e => e.ReviewStatus == ExecutionReviewStatus.Rejected))
        {
            if (!taskMap.TryGetValue(exec.DevelopmentTaskId, out var task)) continue;

            items.Add(new WorkspaceAttentionItemDto
            {
                Id = $"att-review-rejected-{exec.Id}",
                Kind = WorkspaceAttentionKind.ReviewRejected,
                TaskId = task.Id,
                ExecutionId = exec.Id,
                TaskDisplayId = FormatTaskDisplayId(task.Id),
                Title = "Review rejected",
                Reason = $"{FormatTaskDisplayId(task.Id)} · {task.Title}",
                MetaDetail = exec.ReviewRejectionReason != null ? SanitizeError(exec.ReviewRejectionReason) : "Reviewer rejected changes",
                OccurredAt = exec.ReviewDecidedAt ?? exec.CompletedAt ?? exec.CreatedAt,
            });
        }

        // Order by latest occurred and take at most 3
        return items
            .OrderByDescending(i => i.OccurredAt)
            .Take(3)
            .ToList();
    }

    private static List<WorkspaceApprovalItemDto> BuildAwaitingApprovalItems(
        List<DevelopmentTask> tasks,
        List<TaskExecution> executions,
        Dictionary<Guid, TaskImpactAnalysis> analysesByTaskId)
    {
        var items = new List<WorkspaceApprovalItemDto>();
        var taskMap = tasks.ToDictionary(t => t.Id);

        // A. Plan Approvals
        foreach (var task in tasks.Where(t => t.Status == DevelopmentTaskStatus.AwaitingApproval))
        {
            analysesByTaskId.TryGetValue(task.Id, out var analysis);
            var fileCount = analysis?.StructuredResult?.ImpactedFiles?.Count;

            items.Add(new WorkspaceApprovalItemDto
            {
                Id = FormatTaskDisplayId(task.Id),
                Kind = WorkspaceApprovalKind.PlanApproval,
                TaskId = task.Id,
                TaskDisplayId = FormatTaskDisplayId(task.Id),
                Title = task.Title,
                Branch = "—",
                FilesTouched = fileCount,
                RequestedAt = analysis?.CompletedAt ?? task.UpdatedAt,
            });
        }

        // B. Code Review Approvals
        foreach (var exec in executions.Where(e => e.Status == TaskExecutionStatus.Completed && e.ReviewStatus == ExecutionReviewStatus.Pending))
        {
            if (!taskMap.TryGetValue(exec.DevelopmentTaskId, out var task)) continue;
            analysesByTaskId.TryGetValue(task.Id, out var analysis);
            var fileCount = analysis?.StructuredResult?.ImpactedFiles?.Count;

            items.Add(new WorkspaceApprovalItemDto
            {
                Id = FormatTaskDisplayId(task.Id),
                Kind = WorkspaceApprovalKind.CodeReviewApproval,
                TaskId = task.Id,
                ExecutionId = exec.Id,
                TaskDisplayId = FormatTaskDisplayId(task.Id),
                Title = task.Title,
                Branch = exec.BranchName ?? "—",
                FilesTouched = fileCount,
                RequestedAt = exec.CompletedAt ?? exec.CreatedAt,
            });
        }

        return items
            .OrderByDescending(i => i.RequestedAt)
            .ToList();
    }

    private static List<WorkspaceFailedOrBlockedItemDto> BuildFailedOrBlockedItems(
        List<DevelopmentTask> tasks,
        List<TaskExecution> executions,
        Dictionary<Guid, List<ExecutionActivity>> activitiesByExecutionId)
    {
        var items = new List<WorkspaceFailedOrBlockedItemDto>();
        var taskExecutions = executions.GroupBy(e => e.DevelopmentTaskId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var task in tasks)
        {
            taskExecutions.TryGetValue(task.Id, out var taskExecs);
            var latestExec = taskExecs?.OrderByDescending(e => e.CreatedAt).FirstOrDefault();

            var isFailed = task.Status == DevelopmentTaskStatus.Failed || (latestExec != null && latestExec.Status == TaskExecutionStatus.Failed);
            var isRejected = task.Status == DevelopmentTaskStatus.Rejected || (latestExec != null && latestExec.ReviewStatus == ExecutionReviewStatus.Rejected);

            if (!isFailed && !isRejected)
            {
                continue;
            }

            WorkspaceFailureKind failureKind = WorkspaceFailureKind.ExecutionFailed;
            string summary = task.Description;
            DateTime failedAt = task.UpdatedAt;

            if (latestExec != null)
            {
                failedAt = latestExec.CompletedAt ?? latestExec.CreatedAt;
                if (!string.IsNullOrWhiteSpace(latestExec.ErrorMessage))
                {
                    summary = SanitizeError(latestExec.ErrorMessage);
                }

                activitiesByExecutionId.TryGetValue(latestExec.Id, out var acts);
                var failedAct = acts?.LastOrDefault(a => a.Status == ExecutionActivityStatus.Failed);

                if (failedAct != null)
                {
                    if (!string.IsNullOrWhiteSpace(failedAct.Message))
                    {
                        summary = SanitizeError(failedAct.Message);
                    }

                    failureKind = failedAct.Stage switch
                    {
                        ExecutionStage.Build => WorkspaceFailureKind.BuildFailed,
                        ExecutionStage.Test => WorkspaceFailureKind.TestFailed,
                        ExecutionStage.DeveloperAgent => WorkspaceFailureKind.DeveloperAgentFailed,
                        ExecutionStage.PullRequest => WorkspaceFailureKind.PullRequestFailed,
                        _ => WorkspaceFailureKind.ExecutionFailed
                    };
                }
                else if (latestExec.ReviewStatus == ExecutionReviewStatus.Rejected)
                {
                    failureKind = WorkspaceFailureKind.ReviewRejected;
                    summary = !string.IsNullOrWhiteSpace(latestExec.ReviewRejectionReason)
                        ? SanitizeError(latestExec.ReviewRejectionReason)
                        : "Reviewer rejected changes";
                }
                else if (latestExec.CiStatus == ExecutionCiStatus.Failure)
                {
                    failureKind = WorkspaceFailureKind.CiFailed;
                }
            }
            else if (task.Status == DevelopmentTaskStatus.Rejected)
            {
                failureKind = WorkspaceFailureKind.TaskRejected;
                summary = "Task rejected by reviewer";
            }

            items.Add(new WorkspaceFailedOrBlockedItemDto
            {
                Id = FormatTaskDisplayId(task.Id),
                Kind = failureKind,
                TaskId = task.Id,
                ExecutionId = latestExec?.Id,
                TaskDisplayId = FormatTaskDisplayId(task.Id),
                Title = task.Title,
                Summary = summary,
                FailedAt = failedAt,
            });
        }

        return items
            .OrderByDescending(i => i.FailedAt)
            .ToList();
    }

    private List<WorkspaceActivityItemDto> BuildRecentActivity(
        List<DevelopmentTask> tasks,
        List<TaskExecution> executions,
        List<ExecutionActivity> activities,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var items = new List<WorkspaceActivityItemDto>();
        var taskMap = tasks.ToDictionary(t => t.Id);
        var execMap = executions.ToDictionary(e => e.Id);

        // 1. Historical execution activities
        foreach (var act in activities)
        {
            if (!execMap.TryGetValue(act.ExecutionId, out var exec)) continue;
            taskMap.TryGetValue(exec.DevelopmentTaskId, out var task);

            var taskDisplay = task != null ? FormatTaskDisplayId(task.Id) : "TASK";

            var kind = act.Status == ExecutionActivityStatus.Completed
                ? WorkspaceActivityKind.ExecutionStageCompleted
                : WorkspaceActivityKind.ExecutionStageFailed;

            var actor = act.Stage switch
            {
                ExecutionStage.DeveloperAgent => WorkspaceActivityActor.Developer,
                ExecutionStage.Review => WorkspaceActivityActor.Reviewer,
                _ => WorkspaceActivityActor.System
            };

            items.Add(new WorkspaceActivityItemDto
            {
                Id = $"act-{act.Id}",
                Kind = kind,
                Actor = actor,
                Action = FormatActivityAction(act.Message, act.Stage, act.Status),
                Target = taskDisplay,
                TaskId = task?.Id,
                ExecutionId = exec.Id,
                OccurredAt = act.CreatedAt,
            });
        }

        // 2. Historical review decisions
        foreach (var exec in executions.Where(e => e.ReviewDecidedAt.HasValue))
        {
            if (!taskMap.TryGetValue(exec.DevelopmentTaskId, out var task)) continue;
            var isApproved = exec.ReviewStatus == ExecutionReviewStatus.Approved;

            items.Add(new WorkspaceActivityItemDto
            {
                Id = $"act-review-{exec.Id}",
                Kind = isApproved ? WorkspaceActivityKind.ReviewApproved : WorkspaceActivityKind.ReviewRejected,
                Actor = WorkspaceActivityActor.Reviewer,
                Action = isApproved ? "approved review for" : "rejected review for",
                Target = FormatTaskDisplayId(task.Id),
                TaskId = task.Id,
                ExecutionId = exec.Id,
                OccurredAt = exec.ReviewDecidedAt!.Value,
            });
        }

        // 3. Historical merges
        foreach (var exec in executions.Where(e => e.MergedAt.HasValue && e.MergeStatus == ExecutionMergeStatus.Merged))
        {
            if (!taskMap.TryGetValue(exec.DevelopmentTaskId, out var task)) continue;

            items.Add(new WorkspaceActivityItemDto
            {
                Id = $"act-merge-{exec.Id}",
                Kind = WorkspaceActivityKind.MergeCompleted,
                Actor = WorkspaceActivityActor.System,
                Action = "merged changes for",
                Target = FormatTaskDisplayId(task.Id),
                TaskId = task.Id,
                ExecutionId = exec.Id,
                OccurredAt = exec.MergedAt!.Value,
            });
        }

        return items
            .OrderByDescending(a => a.OccurredAt)
            .Take(10)
            .ToList();
    }

    private static List<WorkspaceShippedItemDto> BuildShippedRecently(
        List<TaskExecution> executions,
        Dictionary<Guid, DevelopmentTask> taskMap)
    {
        var items = new List<WorkspaceShippedItemDto>();

        foreach (var exec in executions)
        {
            var isMerged = exec.MergeStatus == ExecutionMergeStatus.Merged ||
                           exec.PullRequestRemoteState == ExecutionPullRequestRemoteState.Merged;

            if (!isMerged)
            {
                continue;
            }

            if (!taskMap.TryGetValue(exec.DevelopmentTaskId, out var task))
            {
                continue;
            }

            var mergedAt = exec.MergedAt ?? exec.PullRequestMergedAt ?? exec.CompletedAt ?? exec.CreatedAt;

            items.Add(new WorkspaceShippedItemDto
            {
                Id = $"shipped-{exec.Id}",
                TaskId = task.Id,
                ExecutionId = exec.Id,
                TaskDisplayId = FormatTaskDisplayId(task.Id),
                Title = task.Title,
                PullRequestNumber = exec.PullRequestNumber,
                MergeCommitSha = exec.MergeCommitSha,
                MergedAt = mergedAt,
            });
        }

        return items
            .OrderByDescending(s => s.MergedAt)
            .Take(5)
            .ToList();
    }

    private static (WorkspaceAttentionKind Kind, string Title, string MetaDetail) DetermineAttentionFailureDetails(
        TaskExecution exec,
        ExecutionActivity? lastFailedAct)
    {
        if (lastFailedAct != null)
        {
            var meta = !string.IsNullOrWhiteSpace(lastFailedAct.Message)
                ? SanitizeError(lastFailedAct.Message)
                : $"Failed at {lastFailedAct.Stage}";

            return lastFailedAct.Stage switch
            {
                ExecutionStage.Build => (WorkspaceAttentionKind.BuildFailed, "Build failed", meta),
                ExecutionStage.Test => (WorkspaceAttentionKind.TestFailed, "Test failed", meta),
                ExecutionStage.DeveloperAgent => (WorkspaceAttentionKind.DeveloperAgentFailed, "Agent execution failed", meta),
                ExecutionStage.PullRequest => (WorkspaceAttentionKind.PullRequestFailed, "PR creation failed", meta),
                _ => (WorkspaceAttentionKind.ExecutionFailed, "Execution failed", meta)
            };
        }

        if (exec.CiStatus == ExecutionCiStatus.Failure)
        {
            return (WorkspaceAttentionKind.CiFailed, "CI checks failed", "Remote CI checks failed");
        }

        var genericMeta = !string.IsNullOrWhiteSpace(exec.ErrorMessage)
            ? SanitizeError(exec.ErrorMessage)
            : "Execution failed";

        return (WorkspaceAttentionKind.ExecutionFailed, "Execution failed", genericMeta);
    }

    private static string FormatActivityAction(string? rawMessage, ExecutionStage stage, ExecutionActivityStatus status)
    {
        if (!string.IsNullOrWhiteSpace(rawMessage))
        {
            return SanitizeError(rawMessage);
        }

        return status == ExecutionActivityStatus.Completed
            ? $"{stage} completed"
            : $"{stage} failed";
    }

    private static string FormatTaskDisplayId(Guid taskId)
    {
        var hex = taskId.ToString("N");
        return $"TASK-{hex[..6].ToUpperInvariant()}";
    }

    private static string SanitizeError(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return string.Empty;

        // Remove absolute windows/unix paths
        var sanitized = Regex.Replace(error, @"[a-zA-Z]:\\[^\s\r\n]+", "[path]");
        sanitized = Regex.Replace(sanitized, @"/(?:[\w.-]+/)+[\w.-]+", "[path]");

        // Remove stack trace lines
        sanitized = Regex.Replace(sanitized, @"\s+at\s+[^\r\n]+", string.Empty);

        // Remove potential token patterns
        sanitized = Regex.Replace(sanitized, @"ghp_[a-zA-Z0-9]+", "[REDACTED]");
        sanitized = Regex.Replace(sanitized, @"Bearer\s+[a-zA-Z0-9_.-]+", "Bearer [REDACTED]");

        return sanitized.Trim();
    }
}
