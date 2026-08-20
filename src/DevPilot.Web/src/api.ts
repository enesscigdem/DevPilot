import type {
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
  WorkspaceAnalysis,
  WorkspaceArchitecture,
  BrainStatus,
  BrainChatResponse,
  BrainIndexResponse,
  WorkspaceOverview,
} from './types';

const BASE_URL = '/api';

async function http<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });

  if (!response.ok) {
    let message = `Request failed: ${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      if (body.error) {
        message = body.error;
      }
    } catch {
      // ignore parse error
    }
    throw new Error(message);
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
    if (err instanceof Error && err.message.includes('404')) {
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
  init?: RequestInit
): Promise<ExecutionReviewDecision> {
  return http<ExecutionReviewDecision>(appendWorkspaceQuery(`/executions/${id}/review/approve`, workspaceId), {
    ...init,
    method: 'POST',
    body: JSON.stringify({ expectedChangeFingerprint }),
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
): Promise<BrainChatResponse> {
  return http<BrainChatResponse>(`/repositoryworkspaces/${workspaceId}/brain/chat`, {
    method: 'POST',
    body: JSON.stringify({ question }),
  });
}

export async function getWorkspaceOverview(
  workspaceId: string,
  init?: RequestInit,
): Promise<WorkspaceOverview> {
  return http<WorkspaceOverview>(`/repositoryworkspaces/${workspaceId}/overview`, init);
}
