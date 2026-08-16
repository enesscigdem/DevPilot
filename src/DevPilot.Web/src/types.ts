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

// ---------------------------------------------------------------------------
// Impact Analysis — enums
// ImpactAnalysisStatus arrives as integer (no JsonStringEnumConverter)
// ---------------------------------------------------------------------------
export const ImpactAnalysisStatus = {
  Pending: 0,
  InProgress: 1,
  Completed: 2,
  Failed: 3,
} as const;
export type ImpactAnalysisStatusValue = (typeof ImpactAnalysisStatus)[keyof typeof ImpactAnalysisStatus];

// ImpactFileChangeType, RiskLevel, SystemImpactLevel arrive as strings
export type ImpactFileChangeType = 'Unknown' | 'Add' | 'Modify' | 'Delete' | 'Refactor';
export type RiskLevelValue = 'Low' | 'Medium' | 'High' | 'Critical';
export type SystemImpactLevelValue = 'Low' | 'Medium' | 'High' | 'Critical';

// ---------------------------------------------------------------------------
// Impact Analysis — DTOs
// ---------------------------------------------------------------------------
export interface ImpactedFile {
  filePath: string;
  changeType: ImpactFileChangeType;
  reason: string;
  confidence: number;
}

export interface ProposedPlanStep {
  order: number;
  title: string;
  description: string;
  relatedFiles: string[];
}

export interface SystemImpact {
  area: string;
  impactLevel: SystemImpactLevelValue;
  description: string;
}

export interface Risk {
  level: RiskLevelValue;
  description: string;
  mitigation: string;
}

export interface StructuredResult {
  summary: string;
  confidence: number;
  impactedFiles: ImpactedFile[];
  proposedPlan: ProposedPlanStep[];
  systemImpacts: SystemImpact[];
  risks: Risk[];
  metadata?: Record<string, unknown>;
}

export interface ImpactAnalysis {
  id: string;
  developmentTaskId: string;
  status: ImpactAnalysisStatusValue;
  summary: string;
  confidence: number;
  model: string | null;
  providerName: string | null;
  rawResponse: string | null;
  errorMessage: string | null;
  structuredResult: StructuredResult | null;
  createdAt: string;
  completedAt: string | null;
}
