import i18n from "@/i18n"
import type {
  ExecutionRevisionDiff,
  CommitExecutionResult,
  CreateTaskRequest,
  ExecutionActivityItem,
  ExecutionDetail,
  ExecutionListItem,
  ExecutionReview,
  ExecutionReviewDecision,
  ImpactAnalysis,
  PushExecutionResult,
  PullRequestResult,
  SyncPullRequestResult,
  MergeExecutionResult,
  Task,
  TaskListItem,
  UpdateTaskStatusRequest,
  RepositoryWorkspace,
  CreateRepositoryWorkspaceRequest,
  CreateGitConnectionRequest,
  GitConnection,
  CreateTrackerConnectionRequest,
  ImportIssuesResponse,
  TrackerConnection,
  TrackerIssue,
  WorkspaceAnalysis,
  WorkspaceArchitecture,
  BrainStatus,
  BrainChatResponse,
  BrainIndexResponse,
  BrainConversation,
  BrainConversationDetail,
  WorkspaceOverview,
  WorkspaceInsights,
  GitHubConnectionStatus,
  GitHubDiscoveredRepository,
  GitHubBranch,
  AiModel,
  SaveAiModelRequest,
  AiModelTestResult,
  DiscoverAiModelsResult,
  AiAdapterType,
  AiStageAssignment,
  ModelComparison,
  VisualCaptureManifest,
  AutomationPolicy,
  UpdateAutomationPolicyRequest,
  TaskBatchDraft,
  TaskBatchParseResult,
  TaskBatchCreateResult,
  GoalPlan,
  GoalTaskPlan,
  GoalCostEstimate,
  GoalDetail,
  GoalSummary,
  StartGoalRequest,
} from './types';

const BASE_URL = '/api';

/** An HTTP failure that keeps the status, so callers can tell "not found" from "server error" without reading text. */
export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
    this.name = 'ApiError';
  }
}

async function http<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });

  if (!response.ok) {
    let message = i18n.t("shared.errRequest", { status: response.status, text: response.statusText });
    try {
      const body = await response.json();
      if (body.error) {
        message = body.error;
      }
    } catch {
      // ignore parse error
    }
    throw new ApiError(message, response.status);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return response.json() as Promise<T>;
}

export async function getTasks(
  filters: { status?: number; priority?: number; repositoryWorkspaceId?: string } = {},
): Promise<TaskListItem[]> {
  const params = new URLSearchParams();
  if (filters.status !== undefined) params.set('status', String(filters.status));
  if (filters.priority !== undefined) params.set('priority', String(filters.priority));
  if (filters.repositoryWorkspaceId) params.set('repositoryWorkspaceId', filters.repositoryWorkspaceId);

  const query = params.toString();
  return http<TaskListItem[]>(`/tasks${query ? `?${query}` : ''}`);
}

export async function getTask(id: string): Promise<Task> {
  return http<Task>(`/tasks/${id}`);
}

export async function createTask(request: CreateTaskRequest): Promise<Task> {
  return http<Task>('/tasks', {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

export async function updateTaskStatus(id: string, request: UpdateTaskStatusRequest): Promise<void> {
  await http<void>(`/tasks/${id}/status`, {
    method: 'PATCH',
    body: JSON.stringify(request),
  });
}

export async function deleteTask(id: string): Promise<void> {
  await http<void>(`/tasks/${id}`, {
    method: 'DELETE',
  });
}

export async function getRepositoryWorkspaces(): Promise<RepositoryWorkspace[]> {
  return http<RepositoryWorkspace[]>('/repositoryworkspaces');
}

export const getWorkspaces = getRepositoryWorkspaces;

export async function getRepositoryWorkspace(id: string): Promise<RepositoryWorkspace> {
  return http<RepositoryWorkspace>(`/repositoryworkspaces/${id}`);
}

export async function createRepositoryWorkspace(request: CreateRepositoryWorkspaceRequest): Promise<RepositoryWorkspace> {
  return http<RepositoryWorkspace>('/repositoryworkspaces', {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

export async function getTaskImpactAnalysis(id: string): Promise<ImpactAnalysis | null> {
  try {
    return await http<ImpactAnalysis>(`/tasks/${id}/impact-analysis`);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      return null;
    }
    throw err;
  }
}

export async function analyzeTaskImpact(id: string): Promise<ImpactAnalysis> {
  return http<ImpactAnalysis>(`/tasks/${id}/impact-analysis`, {
    method: 'POST',
  });
}

export async function approveTask(id: string): Promise<Task> {
  return http<Task>(`/tasks/${id}/approve`, {
    method: 'POST',
  });
}

export async function rejectTask(id: string): Promise<Task> {
  return http<Task>(`/tasks/${id}/reject`, {
    method: 'POST',
  });
}

export async function startExecution(taskId: string): Promise<ExecutionDetail> {
  return http<ExecutionDetail>(`/tasks/${taskId}/executions`, {
    method: 'POST',
  });
}

export async function retryExecution(taskId: string, workspaceId?: string | null): Promise<ExecutionDetail> {
  return http<ExecutionDetail>(appendWorkspaceQuery(`/tasks/${taskId}/executions/retry`, workspaceId), {
    method: 'POST',
  });
}

function appendWorkspaceQuery(url: string, workspaceId?: string | null): string {
  if (!workspaceId) return url;
  const separator = url.includes('?') ? '&' : '?';
  return `${url}${separator}repositoryWorkspaceId=${encodeURIComponent(workspaceId)}`;
}

export async function getExecutions(workspaceId?: string | null, init?: RequestInit): Promise<ExecutionListItem[]> {
  return http<ExecutionListItem[]>(appendWorkspaceQuery('/executions', workspaceId), init);
}

export async function getExecution(id: string, workspaceId?: string | null, init?: RequestInit): Promise<ExecutionDetail> {
  return http<ExecutionDetail>(appendWorkspaceQuery(`/executions/${id}`, workspaceId), init);
}

export async function verifyExecution(id: string, workspaceId?: string | null): Promise<{ message: string }> {
  return http<{ message: string }>(appendWorkspaceQuery(`/executions/${id}/verify`, workspaceId), {
    method: 'POST',
  });
}

export async function cancelExecution(id: string, workspaceId?: string | null, init?: RequestInit): Promise<{ message: string }> {
  return http<{ message: string }>(appendWorkspaceQuery(`/executions/${id}/cancel`, workspaceId), {
    ...init,
    method: 'POST',
  });
}

export async function getExecutionActivity(id: string, workspaceId?: string | null, init?: RequestInit): Promise<ExecutionActivityItem[]> {
  return http<ExecutionActivityItem[]>(appendWorkspaceQuery(`/executions/${id}/activity`, workspaceId), init);
}

export async function getExecutionReview(id: string, workspaceId?: string | null, init?: RequestInit): Promise<ExecutionReview> {
  return http<ExecutionReview>(appendWorkspaceQuery(`/executions/${id}/review`, workspaceId), init);
}

export async function approveExecutionReview(
  id: string,
  expectedChangeFingerprint: string,
  workspaceId?: string | null,
  init?: RequestInit,
  visualAcknowledged = false
): Promise<ExecutionReviewDecision> {
  return http<ExecutionReviewDecision>(appendWorkspaceQuery(`/executions/${id}/review/approve`, workspaceId), {
    ...init,
    method: 'POST',
    body: JSON.stringify({ expectedChangeFingerprint, visualAcknowledged }),
  });
}

export async function rejectExecutionReview(
  id: string,
  reason?: string,
  workspaceId?: string | null,
  init?: RequestInit
): Promise<ExecutionReviewDecision> {
  return http<ExecutionReviewDecision>(appendWorkspaceQuery(`/executions/${id}/review/reject`, workspaceId), {
    ...init,
    method: 'POST',
    body: JSON.stringify({ reason }),
  });
}

/**
 * "Request changes": the feedback is applied by the AI on the execution's own branch, build and test run again,
 * the old approval is removed and the result returns to review. The same pull request is updated on approval.
 */
export async function requestExecutionChanges(
  id: string,
  feedback: string,
  workspaceId?: string | null,
  init?: RequestInit
): Promise<{ message: string; revisionNumber: number }> {
  return http<{ message: string; revisionNumber: number }>(appendWorkspaceQuery(`/executions/${id}/review/request-changes`, workspaceId), {
    ...init,
    method: 'POST',
    body: JSON.stringify({ feedback }),
  });
}

export async function getExecutionRevisionDiff(id: string, workspaceId?: string | null, init?: RequestInit, number?: number): Promise<ExecutionRevisionDiff> {
  const base = appendWorkspaceQuery(`/executions/${id}/revision/diff`, workspaceId);
  return http<ExecutionRevisionDiff>(number != null ? `${base}${base.includes("?") ? "&" : "?"}number=${number}` : base, init);
}

export async function commitExecution(id: string, workspaceId?: string | null, init?: RequestInit): Promise<CommitExecutionResult> {
  return http<CommitExecutionResult>(appendWorkspaceQuery(`/executions/${id}/commit`, workspaceId), {
    ...init,
    method: 'POST',
  });
}

export async function pushExecution(id: string, workspaceId?: string | null, init?: RequestInit): Promise<PushExecutionResult> {
  return http<PushExecutionResult>(appendWorkspaceQuery(`/executions/${id}/push`, workspaceId), {
    ...init,
    method: 'POST',
  });
}

export async function createPullRequest(id: string, workspaceId?: string | null, init?: RequestInit): Promise<PullRequestResult> {
  return http<PullRequestResult>(appendWorkspaceQuery(`/executions/${id}/pull-request`, workspaceId), {
    ...init,
    method: 'POST',
  });
}

export async function syncPullRequest(id: string, workspaceId?: string | null, init?: RequestInit): Promise<SyncPullRequestResult> {
  return http<SyncPullRequestResult>(appendWorkspaceQuery(`/executions/${id}/pull-request/sync`, workspaceId), {
    ...init,
    method: 'POST',
  });
}

export async function mergeExecution(id: string, workspaceId?: string | null, init?: RequestInit): Promise<MergeExecutionResult> {
  return http<MergeExecutionResult>(appendWorkspaceQuery(`/executions/${id}/merge`, workspaceId), {
    ...init,
    method: 'POST',
  });
}

export async function getRepositoryWorkspaceAnalysis(workspaceId: string): Promise<WorkspaceAnalysis> {
  return http<WorkspaceAnalysis>(`/repositoryworkspaces/${workspaceId}/analysis`);
}

export async function getRepositoryWorkspaceArchitecture(workspaceId: string): Promise<WorkspaceArchitecture> {
  return http<WorkspaceArchitecture>(`/repositoryworkspaces/${workspaceId}/architecture`);
}

export async function getBrainStatus(workspaceId: string): Promise<BrainStatus> {
  return http<BrainStatus>(`/repositoryworkspaces/${workspaceId}/brain/status`);
}

export async function indexBrain(
  workspaceId: string,
  generateEmbeddings = true,
): Promise<BrainIndexResponse> {
  return http<BrainIndexResponse>(`/repositoryworkspaces/${workspaceId}/brain/index`, {
    method: 'POST',
    body: JSON.stringify({ generateEmbeddings }),
  });
}

export async function askBrain(
  workspaceId: string,
  question: string,
  conversationId?: string | null,
): Promise<BrainChatResponse> {
  return http<BrainChatResponse>(`/repositoryworkspaces/${workspaceId}/brain/chat`, {
    method: 'POST',
    body: JSON.stringify({ question, conversationId: conversationId ?? undefined }),
  });
}

export async function getBrainConversations(
  workspaceId: string,
): Promise<BrainConversation[]> {
  return http<BrainConversation[]>(`/repositoryworkspaces/${workspaceId}/brain/conversations`);
}

export async function getBrainConversationById(
  workspaceId: string,
  conversationId: string,
): Promise<BrainConversationDetail> {
  return http<BrainConversationDetail>(`/repositoryworkspaces/${workspaceId}/brain/conversations/${conversationId}`);
}

export async function createBrainConversation(
  workspaceId: string,
  title?: string,
): Promise<BrainConversation> {
  return http<BrainConversation>(`/repositoryworkspaces/${workspaceId}/brain/conversations`, {
    method: 'POST',
    body: JSON.stringify({ title }),
  });
}

export async function deleteBrainConversation(
  workspaceId: string,
  conversationId: string,
): Promise<{ success: boolean }> {
  return http<{ success: boolean }>(`/repositoryworkspaces/${workspaceId}/brain/conversations/${conversationId}`, {
    method: 'DELETE',
  });
}

export async function getWorkspaceOverview(
  workspaceId: string,
  init?: RequestInit,
): Promise<WorkspaceOverview> {
  return http<WorkspaceOverview>(`/repositoryworkspaces/${workspaceId}/overview`, init);
}

export async function getWorkspaceInsights(
  workspaceId: string,
  init?: RequestInit,
): Promise<WorkspaceInsights> {
  return http<WorkspaceInsights>(`/repositoryworkspaces/${workspaceId}/insights`, init);
}

export async function getGitHubStatus(): Promise<GitHubConnectionStatus> {
  return http<GitHubConnectionStatus>('/github/status');
}

export async function getGitHubConnectUrl(returnUrl?: string): Promise<{ url: string }> {
  const query = returnUrl ? `?returnUrl=${encodeURIComponent(returnUrl)}` : '';
  return http<{ url: string }>(`/github/connect-url${query}`);
}

export async function getGitHubRepositories(): Promise<GitHubDiscoveredRepository[]> {
  return http<GitHubDiscoveredRepository[]>('/github/repositories');
}

export async function getGitHubBranches(owner: string, repo: string): Promise<GitHubBranch[]> {
  return http<GitHubBranch[]>(`/github/repositories/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/branches`);
}

export async function disconnectGitHubInstallation(id: string): Promise<void> {
  await http<void>(`/github/installations/${id}`, {
    method: 'DELETE',
  });
}

export async function getAiModels(): Promise<AiModel[]> {
  return http<AiModel[]>('/ai-models');
}

export async function createAiModel(request: SaveAiModelRequest): Promise<AiModel> {
  return http<AiModel>('/ai-models', { method: 'POST', body: JSON.stringify(request) });
}

export async function updateAiModel(id: string, request: SaveAiModelRequest): Promise<AiModel> {
  return http<AiModel>(`/ai-models/${id}`, { method: 'PUT', body: JSON.stringify(request) });
}

export async function deleteAiModel(id: string): Promise<void> {
  await http<void>(`/ai-models/${id}`, { method: 'DELETE' });
}

/** The server ends a connection test after this many seconds (AiModelService default test timeout). */
export const MODEL_TEST_LIMIT_SECONDS = 120;

/** The browser waits a little longer than the server limit, so the server's own timeout result always arrives first. */
export const MODEL_TEST_CLIENT_GUARD_SECONDS = MODEL_TEST_LIMIT_SECONDS + 15;

export async function testAiModel(id: string, init?: RequestInit): Promise<AiModelTestResult> {
  return http<AiModelTestResult>(`/ai-models/${id}/test`, { ...init, method: 'POST' });
}

export async function discoverAiModels(request: {
  adapterType: AiAdapterType;
  baseUrl: string;
  apiKey?: string;
  existingModelId?: string;
}): Promise<DiscoverAiModelsResult> {
  return http<DiscoverAiModelsResult>('/ai-models/discover', { method: 'POST', body: JSON.stringify(request) });
}

export async function getAiStageAssignments(): Promise<AiStageAssignment[]> {
  return http<AiStageAssignment[]>('/ai-models/stages');
}

export async function setAiStageAssignments(assignments: AiStageAssignment[]): Promise<AiStageAssignment[]> {
  return http<AiStageAssignment[]>('/ai-models/stages', { method: 'PUT', body: JSON.stringify(assignments) });
}

export async function startModelComparison(taskId: string, modelIds: string[]): Promise<ModelComparison> {
  return http<ModelComparison>('/model-comparisons', { method: 'POST', body: JSON.stringify({ taskId, modelIds }) });
}

export async function getModelComparison(id: string, init?: RequestInit): Promise<ModelComparison> {
  return http<ModelComparison>(`/model-comparisons/${id}`, init);
}

export async function getModelComparisonsForTask(taskId: string, init?: RequestInit): Promise<ModelComparison[]> {
  return http<ModelComparison[]>(`/model-comparisons?taskId=${taskId}`, init);
}

export async function cancelModelComparison(id: string): Promise<ModelComparison> {
  return http<ModelComparison>(`/model-comparisons/${id}/cancel`, { method: 'POST' });
}

export async function getExecutionVisual(id: string, init?: RequestInit): Promise<VisualCaptureManifest> {
  return http<VisualCaptureManifest>(`/executions/${id}/visual`, init);
}

export function executionVisualImageUrl(id: string, fileName: string, cacheKey?: string): string {
  const suffix = cacheKey ? `?v=${encodeURIComponent(cacheKey)}` : '';
  return `${BASE_URL}/executions/${id}/visual/${fileName}${suffix}`;
}

export async function getAutomationPolicy(workspaceId: string): Promise<AutomationPolicy> {
  return http<AutomationPolicy>(`/repositoryworkspaces/${workspaceId}/automation`);
}

export async function updateAutomationPolicy(
  workspaceId: string,
  request: UpdateAutomationPolicyRequest,
): Promise<AutomationPolicy> {
  return http<AutomationPolicy>(`/repositoryworkspaces/${workspaceId}/automation`, {
    method: 'PUT',
    body: JSON.stringify(request),
  });
}

export async function parseTaskBatch(text: string): Promise<TaskBatchParseResult> {
  return http<TaskBatchParseResult>('/tasks/batch/parse', { method: 'POST', body: JSON.stringify({ text }) });
}

export async function createTaskBatch(
  repositoryWorkspaceId: string,
  tasks: TaskBatchDraft[],
): Promise<TaskBatchCreateResult> {
  return http<TaskBatchCreateResult>('/tasks/batch', {
    method: 'POST',
    body: JSON.stringify({ repositoryWorkspaceId, tasks }),
  });
}

export async function planGoal(workspaceId: string, text: string): Promise<GoalPlan> {
  return http<GoalPlan>(`/repositoryworkspaces/${workspaceId}/goals/plan`, {
    method: 'POST',
    body: JSON.stringify({ text }),
  });
}

export async function arrangeGoal(
  workspaceId: string,
  tasks: GoalTaskPlan[],
  estimate: GoalCostEstimate,
): Promise<GoalPlan> {
  return http<GoalPlan>(`/repositoryworkspaces/${workspaceId}/goals/arrange`, {
    method: 'POST',
    body: JSON.stringify({
      tasks,
      inputPerMillionUsd: estimate.inputPerMillionUsd,
      outputPerMillionUsd: estimate.outputPerMillionUsd,
    }),
  });
}

export async function startGoal(workspaceId: string, request: StartGoalRequest): Promise<GoalDetail> {
  return http<GoalDetail>(`/repositoryworkspaces/${workspaceId}/goals`, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

export async function getGoals(workspaceId: string): Promise<GoalSummary[]> {
  return http<GoalSummary[]>(`/repositoryworkspaces/${workspaceId}/goals`);
}

export async function getGoal(workspaceId: string, goalId: string): Promise<GoalDetail> {
  return http<GoalDetail>(`/repositoryworkspaces/${workspaceId}/goals/${goalId}`);
}

export async function cancelGoal(workspaceId: string, goalId: string): Promise<GoalDetail> {
  return http<GoalDetail>(`/repositoryworkspaces/${workspaceId}/goals/${goalId}/cancel`, { method: 'POST' });
}

export async function getGitConnections(): Promise<GitConnection[]> {
  return http<GitConnection[]>('/git-connections');
}

export async function createGitConnection(request: CreateGitConnectionRequest): Promise<GitConnection> {
  return http<GitConnection>('/git-connections', {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

export async function deleteGitConnection(id: string): Promise<void> {
  await http<void>(`/git-connections/${id}`, { method: 'DELETE' });
}

export async function getTrackerConnections(): Promise<TrackerConnection[]> {
  return http<TrackerConnection[]>('/tracker-connections');
}

export async function createTrackerConnection(request: CreateTrackerConnectionRequest): Promise<TrackerConnection> {
  return http<TrackerConnection>('/tracker-connections', {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

export async function deleteTrackerConnection(id: string): Promise<void> {
  await http<void>(`/tracker-connections/${id}`, { method: 'DELETE' });
}

export async function searchTrackerIssues(
  connectionId: string,
  query: string,
  workspaceId: string | null,
): Promise<TrackerIssue[]> {
  const params = new URLSearchParams();
  if (query.trim()) params.set('q', query.trim());
  if (workspaceId) params.set('workspaceId', workspaceId);
  const qs = params.toString();
  return http<TrackerIssue[]>(`/tracker-connections/${connectionId}/issues${qs ? `?${qs}` : ''}`);
}

export async function importTrackerIssues(
  connectionId: string,
  repositoryWorkspaceId: string,
  issueKeys: string[],
): Promise<ImportIssuesResponse> {
  return http<ImportIssuesResponse>(`/tracker-connections/${connectionId}/import`, {
    method: 'POST',
    body: JSON.stringify({ repositoryWorkspaceId, issueKeys }),
  });
}
