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

// ---------------------------------------------------------------------------
// Executions — enums & DTOs
// ---------------------------------------------------------------------------
export const TaskExecutionStatus = {
  Pending: 0,
  Running: 1,
  Completed: 2,
  Failed: 3,
  Cancelled: 4,
} as const;
export type TaskExecutionStatusValue = (typeof TaskExecutionStatus)[keyof typeof TaskExecutionStatus];

export type Tone = "neutral" | "blue" | "amber" | "green" | "red" | "gray";

export const executionStatusMeta: Record<number, { label: string; tone: Tone }> = {
  [TaskExecutionStatus.Pending]: { label: "Pending", tone: "amber" },
  [TaskExecutionStatus.Running]: { label: "Running", tone: "blue" },
  [TaskExecutionStatus.Completed]: { label: "Completed", tone: "green" },
  [TaskExecutionStatus.Failed]: { label: "Failed", tone: "red" },
  [TaskExecutionStatus.Cancelled]: { label: "Cancelled", tone: "gray" },
};

export function getExecutionStatusMeta(status: number | string): { label: string; tone: Tone } {
  if (typeof status === "number") {
    return executionStatusMeta[status] ?? { label: `Status ${status}`, tone: "neutral" };
  }
  const s = String(status).toLowerCase();
  if (s === "pending") return executionStatusMeta[TaskExecutionStatus.Pending];
  if (s === "running") return executionStatusMeta[TaskExecutionStatus.Running];
  if (s === "completed") return executionStatusMeta[TaskExecutionStatus.Completed];
  if (s === "failed") return executionStatusMeta[TaskExecutionStatus.Failed];
  if (s === "cancelled") return executionStatusMeta[TaskExecutionStatus.Cancelled];
  return { label: String(status), tone: "neutral" };
}

export interface ExecutionListItem {
  id: string;
  developmentTaskId: string;
  taskTitle: string;
  repositoryName: string;
  status: number;
  createdAt: string;
}

export interface ExecutionDetail {
  id: string;
  developmentTaskId: string;
  taskTitle: string;
  repositoryWorkspaceId: string;
  repositoryOwner: string;
  repositoryName: string;
  status: number;
  reviewStatus: string;
  commitStatus?: string;
  commitSha?: string | null;
  committedAt?: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  errorMessage: string | null;
}

export interface ExecutionActivityMetadata {
  branchName?: string | null;
  modifiedFileCount?: number | null;
  buildPassed?: boolean | null;
  testPassed?: boolean | null;
}

export interface ExecutionActivityItem {
  id: string;
  executionId: string;
  stage: string;
  status: string;
  createdAt: string;
  message: string;
  metadata?: ExecutionActivityMetadata | null;
}


// ---------------------------------------------------------------------------
// Execution Review — DTOs
// ---------------------------------------------------------------------------
export type ExecutionReviewStageResult = "Passed" | "Failed" | "Unknown";

export interface ExecutionReviewStageStatus {
  status: ExecutionReviewStageResult;
}

export interface ExecutionReviewFile {
  path: string;
  changeType: string;
  additions: number | null;
  deletions: number | null;
}

export interface ExecutionReview {
  executionId: string;
  taskId: string;
  taskTitle: string;
  executionStatus: string;
  branchName: string;
  changedFileCount: number;
  changedFiles: ExecutionReviewFile[];
  diff: string;
  diffTruncated: boolean;
  build: ExecutionReviewStageStatus;
  test: ExecutionReviewStageStatus;
  reviewStatus: string;
  decidedAt: string | null;
  rejectionReason: string | null;
  changeFingerprint: string;
  approvedSnapshotMatchesCurrent: boolean;
  commitEligible: boolean;
  commitStatus: string;
  commitSha: string | null;
  committedAt: string | null;
}

export interface ExecutionReviewDecision {
  executionId: string;
  reviewStatus: string;
  decidedAt: string;
  rejectionReason: string | null;
}

export interface CommitExecutionResult {
  executionId: string;
  branchName: string;
  commitStatus: string;
  commitSha: string;
  committedAt: string;
}
