import type { CreateTaskRequest, ImpactAnalysis, Task, TaskListItem, UpdateTaskStatusRequest, Workspace } from './types';

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

export async function getWorkspaces(): Promise<Workspace[]> {
  return http<Workspace[]>('/repositoryworkspaces');
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
