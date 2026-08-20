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

export const RepositoryWorkspaceStatus = {
  Cloning: 0,
  Completed: 1,
  Failed: 2,
  AlreadyExists: 3,
} as const;
export type RepositoryWorkspaceStatusValue =
  (typeof RepositoryWorkspaceStatus)[keyof typeof RepositoryWorkspaceStatus];

export interface RepositoryWorkspace {
  id: string;
  owner: string;
  repository: string;
  branch: string;
  status: number;
  displayName?: string;
  commitSha?: string;
  createdAt?: string;
  updatedAt?: string;
}

export type Workspace = RepositoryWorkspace;

export interface CreateRepositoryWorkspaceRequest {
  owner: string;
  repository: string;
  branch: string;
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

export type ExecutionStageStepState = "Todo" | "Active" | "Done" | "Failed" | "Blocked";

export interface ExecutionStageStep {
  stageKey: string;
  label: string;
  state: ExecutionStageStepState;
}

export interface ExecutionListItem {
  id: string;
  developmentTaskId: string;
  taskTitle: string;
  repositoryName: string;
  status: number;
  createdAt: string;
  startedAt?: string | null;
  completedAt?: string | null;
  reviewStatus?: string;
  commitStatus?: string;
  pushStatus?: string;
  pullRequestStatus?: string;
  pullRequestRemoteState?: string;
  ciStatus?: string;
  mergeStatus?: string;
  errorMessage?: string | null;
  progressPercentage?: number;
  model?: string | null;
  stages?: ExecutionStageStep[];
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
  pushStatus?: string;
  remoteBranchName?: string | null;
  remoteCommitSha?: string | null;
  pushedAt?: string | null;
  canRequestPush?: boolean;
  pullRequestStatus?: string;
  pullRequestNumber?: number | null;
  pullRequestUrl?: string | null;
  pullRequestCreatedAt?: string | null;
  canRequestPullRequest?: boolean;
  pullRequestRemoteState?: string;
  pullRequestIntegrityStatus?: string;
  pullRequestLastSyncedAt?: string | null;
  ciStatus?: string;
  ciChecks?: ExecutionCiCheck[];
  mergeStatus?: string;
  mergeCommitSha?: string | null;
  mergedAt?: string | null;
  canRequestMerge?: boolean;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  errorMessage: string | null;
  progressPercentage?: number;
  model?: string | null;
  stages?: ExecutionStageStep[];
}

export interface ExecutionActivityMetadata {
  branchName?: string | null;
  modifiedFileCount?: number | null;
  buildPassed?: boolean | null;
  testPassed?: boolean | null;
  model?: string | null;
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

export interface ExecutionCiCheck {
  id: string;
  externalId: number;
  name: string;
  source: string;
  checkType: string;
  status: string;
  conclusion?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
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
  pushStatus: string;
  remoteBranchName: string | null;
  remoteCommitSha: string | null;
  pushedAt: string | null;
  canRequestPush: boolean;
  pullRequestStatus: string;
  pullRequestNumber: number | null;
  pullRequestUrl: string | null;
  pullRequestCreatedAt: string | null;
  canRequestPullRequest: boolean;
  pullRequestRemoteState?: string;
  pullRequestIntegrityStatus?: string;
  pullRequestLastSyncedAt?: string | null;
  ciStatus?: string;
  ciChecks?: ExecutionCiCheck[];
  mergeStatus?: string;
  mergeCommitSha?: string | null;
  mergedAt?: string | null;
  mergeMethod?: string | null;
  canRequestMerge?: boolean;
  mergeBlockedReason?: string | null;
  repositoryWorkspaceId?: string;
  repositoryOwner?: string;
  repositoryName?: string;
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

export interface PushExecutionResult {
  executionId: string;
  branchName: string;
  pushStatus: string;
  remoteCommitSha: string;
  pushedAt: string;
}

export interface PullRequestResult {
  executionId: string;
  pullRequestStatus: string;
  pullRequestNumber: number | null;
  pullRequestUrl: string | null;
  baseBranch: string;
  headBranch: string;
  headCommitSha: string;
  createdAt: string | null;
}

export interface SyncPullRequestResult {
  executionId: string;
  pullRequestNumber?: number | null;
  pullRequestUrl?: string | null;
  pullRequestRemoteState: string;
  pullRequestIntegrityStatus: string;
  pullRequestLastSyncedAt?: string | null;
  ciStatus: string;
  ciChecks: ExecutionCiCheck[];
  lastSyncedAt?: string | null;
  syncError?: string | null;
  canRequestMerge?: boolean;
  mergeBlockedReason?: string | null;
}

export interface MergeExecutionResult {
  executionId: string;
  mergeStatus: string;
  pullRequestNumber?: number | null;
  pullRequestUrl?: string | null;
  baseBranch: string;
  headBranch: string;
  approvedHeadSha: string;
  mergeCommitSha?: string | null;
  mergedAt?: string | null;
  mergeMethod?: string | null;
}

// ---------------------------------------------------------------------------
// Workspace Analysis — DTOs
// ---------------------------------------------------------------------------
export interface WorkspaceRepositoryInfo {
  owner: string;
  repository: string;
  fullName: string;
  branch: string;
  commitSha: string;
}

export interface WorkspaceAnalysisStep {
  label: string;
  done: boolean;
}

export interface WorkspaceAnalysisSummary {
  status: string;
  engine: string;
  symbolsCount: number;
  typesCount: number;
  referencesCount: number;
  analyzedAt: string;
  steps: WorkspaceAnalysisStep[];
}

export interface WorkspaceFileNode {
  name: string;
  path: string;
  type: "folder" | "file";
  lang?: string;
  children?: WorkspaceFileNode[];
}

export interface WorkspaceProjectReference {
  name: string;
  path: string;
}

export interface WorkspaceProject {
  name: string;
  path: string;
  projectType: string;
  layer: string;
  fileCount: number;
  targetFramework: string | null;
  projectReferences: WorkspaceProjectReference[];
  compilationSucceeded: boolean;
  compilationErrors: string[];
  warnings: string[];
}

export interface WorkspaceTechnology {
  name: string;
  version: string | null;
  kind: string;
}

export interface WorkspaceEndpoint {
  method: string;
  route: string;
  controller: string;
  action: string;
  auth: boolean;
  sourcePath: string;
}

export interface WorkspaceAnalysis {
  repository: WorkspaceRepositoryInfo;
  summary: WorkspaceAnalysisSummary;
  fileTree: WorkspaceFileNode[];
  projects: WorkspaceProject[];
  technologies: WorkspaceTechnology[];
  endpoints: WorkspaceEndpoint[];
  warnings: string[];
}

// ---------------------------------------------------------------------------
// Workspace Architecture Graph — DTOs
// ---------------------------------------------------------------------------
export interface WorkspaceArchitectureNode {
  id: string;
  label: string;
  sub: string;
  layer: string;
  projectType: string;
  path: string;
  keyFiles: string[];
  incoming: string[];
  outgoing: string[];
  impacted: boolean;
  why: string;
}

export interface WorkspaceArchitectureEdge {
  from: string;
  to: string;
  type: string;
}

export interface WorkspaceArchitectureSummary {
  status: string;
  nodesCount: number;
  edgesCount: number;
  analyzedAt: string;
}

export interface WorkspaceArchitecture {
  repository: WorkspaceRepositoryInfo;
  summary: WorkspaceArchitectureSummary;
  nodes: WorkspaceArchitectureNode[];
  edges: WorkspaceArchitectureEdge[];
}

// ---------------------------------------------------------------------------
// Project Brain — DTOs
// ---------------------------------------------------------------------------
export interface BrainIndexStep {
  label: string;
  done: boolean;
}

export interface BrainSourceGroup {
  project: string;
  layer: string;
  files: number;
  symbols: number;
  indexed: boolean;
}

export interface BrainStatus {
  workspaceId: string;
  state: 'ready' | 'indexing' | 'unindexed' | 'stale' | 'failed';
  totalFiles: number;
  totalTypes: number;
  totalSymbols: number;
  totalChunks: number;
  lastIndexedAt?: string | null;
  lastIndexedRelative?: string | null;
  engine: string;
  steps: BrainIndexStep[];
  sourceGroups: BrainSourceGroup[];
  suggestedQuestions: string[];
}

export interface BrainCitation {
  file: string;
  path: string;
  lines: string;
  startLine?: number;
  endLine?: number;
  symbol?: string | null;
  lang?: string | null;
  snippet: string;
}

export interface BrainContextFile {
  file: string;
  path: string;
  relevance: number;
}

export interface BrainMessage {
  role: 'user' | 'assistant';
  content: string;
  citations?: BrainCitation[];
  confidence?: number;
  elapsed?: string;
}

export interface BrainChatResponse {
  success: boolean;
  errorMessage?: string | null;
  conversationId?: string | null;
  role: 'assistant';
  content: string;
  confidence?: number | null;
  elapsed: string;
  citations: BrainCitation[];
  contextFiles: BrainContextFile[];
  retrievalMode: string;
  isUnindexed?: boolean;
  isStale?: boolean;
}

export interface BrainConversation {
  id: string;
  repositoryWorkspaceId: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
}

export interface BrainConversationDetail {
  id: string;
  repositoryWorkspaceId: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messages: BrainPersistedMessage[];
}

export interface BrainPersistedMessage {
  id: string;
  conversationId: string;
  role: 'user' | 'assistant';
  content: string;
  confidence?: number | null;
  elapsed?: string | null;
  citations?: BrainCitation[] | null;
  contextFiles?: BrainContextFile[] | null;
  createdAt: string;
}

export interface BrainIndexResponse {
  jobId: string;
  success: boolean;
  filesIndexed: number;
  chunksIndexed: number;
  chunksUpdated: number;
  chunksSkipped: number;
  chunksDeleted: number;
  duration: string;
  errorMessage?: string | null;
}

// ---------------------------------------------------------------------------
// Workspace Overview — Dashboard DTOs
// ---------------------------------------------------------------------------
export type WorkspaceAttentionKind =
  | 'ExecutionFailed'
  | 'ReviewPending'
  | 'PlanApprovalRequired'
  | 'ReviewRejected'
  | 'TaskRejected'
  | 'BuildFailed'
  | 'TestFailed'
  | 'DeveloperAgentFailed'
  | 'PullRequestFailed'
  | 'CiFailed'
  | number;

export interface WorkspaceAttentionItem {
  id: string;
  kind: WorkspaceAttentionKind;
  taskId?: string | null;
  executionId?: string | null;
  taskDisplayId: string;
  title: string;
  reason: string;
  metaDetail?: string | null;
  occurredAt: string;
}

export type WorkspaceStageState =
  | 'Todo'
  | 'Active'
  | 'Done'
  | 'Failed'
  | 'Blocked'
  | number;

export interface WorkspaceStageStep {
  stageKey: string;
  state: WorkspaceStageState;
}

export interface WorkspaceActiveExecution {
  executionId: string;
  taskId: string;
  taskDisplayId: string;
  taskTitle: string;
  currentStageKey: string;
  stages: WorkspaceStageStep[];
  startedAt?: string | null;
  completedAt?: string | null;
  elapsedSeconds?: number | null;
  tokensUsed?: number | null;
  estimatedCost?: number | null;
  modifiedFileCount?: number | null;
}

export type WorkspaceApprovalKind =
  | 'PlanApproval'
  | 'CodeReviewApproval'
  | number;

export interface WorkspaceApprovalItem {
  id: string;
  kind: WorkspaceApprovalKind;
  taskId: string;
  executionId?: string | null;
  taskDisplayId: string;
  title: string;
  branch: string;
  filesTouched?: number | null;
  requestedAt: string;
}

export type WorkspaceFailureKind =
  | 'BuildFailed'
  | 'TestFailed'
  | 'DeveloperAgentFailed'
  | 'ExecutionFailed'
  | 'ReviewRejected'
  | 'TaskRejected'
  | 'PullRequestFailed'
  | 'CiFailed'
  | number;

export interface WorkspaceFailedOrBlockedItem {
  id: string;
  kind: WorkspaceFailureKind;
  taskId: string;
  executionId?: string | null;
  taskDisplayId: string;
  title: string;
  summary: string;
  failedAt: string;
}

export type WorkspaceActivityKind =
  | 'ExecutionStageCompleted'
  | 'ExecutionStageFailed'
  | 'ReviewApproved'
  | 'ReviewRejected'
  | 'PullRequestCreated'
  | 'CiPassed'
  | 'CiFailed'
  | 'MergeCompleted'
  | 'RepositoryIndexed'
  | number;

export type WorkspaceActivityActor =
  | 'Planner'
  | 'Developer'
  | 'Reviewer'
  | 'System'
  | 'User'
  | number;

export interface WorkspaceActivityItem {
  id: string;
  kind: WorkspaceActivityKind;
  actor: WorkspaceActivityActor;
  action: string;
  target: string;
  taskId?: string | null;
  executionId?: string | null;
  occurredAt: string;
}

export interface WorkspaceAnalysisOverview {
  repositoryFullName: string;
  language?: string | null;
  loc?: number | null;
  symbolsCount: number;
  typesCount: number;
  referencesCount?: number | null;
  lastIndexedAt?: string | null;
  isIndexed: boolean;
}

export interface WorkspaceShippedItem {
  id: string;
  taskId: string;
  executionId: string;
  taskDisplayId: string;
  title: string;
  pullRequestNumber?: number | null;
  mergeCommitSha?: string | null;
  mergedAt: string;
}

export interface WorkspaceHeader {
  workspaceId: string;
  repositoryFullName: string;
  branch: string;
  fileCount: number;
  lastIndexedAt?: string | null;
  isIndexed: boolean;
}

export interface WorkspaceOverview {
  header: WorkspaceHeader;
  needsAttention: WorkspaceAttentionItem[];
  activeExecution?: WorkspaceActiveExecution | null;
  activeAgentExecution?: WorkspaceActiveExecution | null;
  awaitingApproval: WorkspaceApprovalItem[];
  failedOrBlocked: WorkspaceFailedOrBlockedItem[];
  recentActivity: WorkspaceActivityItem[];
  recentlyAnalyzed: WorkspaceAnalysisOverview;
  shippedRecently: WorkspaceShippedItem[];
}

// ---------------------------------------------------------------------------
// GitHub App Integration & Repository Picker — DTOs
// ---------------------------------------------------------------------------
export interface GitHubInstallationSummary {
  id: string;
  externalInstallationId: number;
  accountLogin: string;
  accountType: string;
  targetAvatarUrl?: string | null;
  status: string;
  connectedAt: string;
  manageUrl: string;
}

export interface GitHubConnectionStatus {
  isConfigured: boolean;
  isConnected: boolean;
  installations: GitHubInstallationSummary[];
}

export interface GitHubDiscoveredRepository {
  id: number;
  fullName: string;
  name: string;
  owner: string;
  isPrivate: boolean;
  defaultBranch: string;
  url: string;
  description?: string | null;
  externalInstallationId: number;
  isConnectedToDevPilot: boolean;
  devPilotWorkspaceId?: string | null;
}

export interface GitHubBranch {
  name: string;
  commitSha: string;
  isProtected: boolean;
}
