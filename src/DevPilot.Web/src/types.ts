export interface TaskListItem {
  id: string;
  title: string;
  repositoryName: string;
  status: number;
  priority: number;
  updatedAt: string;
}

export interface Task {
  id: string;
  repositoryWorkspaceId: string;
  repositoryWorkspaceName: string;
  repositoryOwner: string;
  repositoryName: string;
  title: string;
  description: string;
  acceptanceCriteria: string | null;
  priority: number;
  status: number;
  createdAt: string;
  updatedAt: string;
}

export interface CreateTaskRequest {
  repositoryWorkspaceId: string;
  title: string;
  description: string;
  acceptanceCriteria?: string;
  priority: number;
}

export interface UpdateTaskStatusRequest {
  status: number;
}

export interface Workspace {
  id: string;
  owner: string;
  repository: string;
  branch: string;
  status: number;
  displayName: string;
}

export const TaskStatus = {
  Draft: 0,
  ReadyForAnalysis: 1,
  Analyzing: 2,
  AwaitingApproval: 3,
  Approved: 4,
  Executing: 5,
  Completed: 6,
  Failed: 7,
  Rejected: 8,
} as const;

export const TaskPriority = {
  Low: 0,
  Medium: 1,
  High: 2,
  Critical: 3,
} as const;

export const statusLabels: Record<number, string> = {
  [TaskStatus.Draft]: 'Draft',
  [TaskStatus.ReadyForAnalysis]: 'Ready for Analysis',
  [TaskStatus.Analyzing]: 'Analyzing',
  [TaskStatus.AwaitingApproval]: 'Awaiting Approval',
  [TaskStatus.Approved]: 'Approved',
  [TaskStatus.Executing]: 'Executing',
  [TaskStatus.Completed]: 'Completed',
  [TaskStatus.Failed]: 'Failed',
  [TaskStatus.Rejected]: 'Rejected',
};

export const priorityLabels: Record<number, string> = {
  [TaskPriority.Low]: 'Low',
  [TaskPriority.Medium]: 'Medium',
  [TaskPriority.High]: 'High',
  [TaskPriority.Critical]: 'Critical',
};

export const statusOptions = Object.entries(statusLabels).map(([value, label]) => ({
  value: Number(value),
  label,
}));

export const priorityOptions = Object.entries(priorityLabels).map(([value, label]) => ({
  value: Number(value),
  label,
}));
