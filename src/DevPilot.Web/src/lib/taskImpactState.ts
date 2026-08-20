import {
  TaskStatus,
  TaskExecutionStatus,
  ImpactAnalysisStatus,
  type Task,
  type ImpactAnalysis,
  type ExecutionListItem,
  type Tone,
} from "@/types"

export type TaskImpactLifecycle = "idle" | "analyzing" | "succeeded" | "failed"

export interface TaskImpactLifecycleState {
  lifecycle: TaskImpactLifecycle
  statusTone: Tone
  statusLabel: string
  canRun: boolean
  canRetry: boolean
  canApprove: boolean
  isAnalyzing: boolean
  isSucceeded: boolean
  isFailed: boolean
  elapsedSeconds: number
  durationFormatted: string | null
  sanitizedErrorMessage: string | null
}

export type TaskImpactActionKind =
  | "active-execution"
  | "syncing-execution"
  | "awaiting-approval"
  | "approved"
  | "failed"
  | "rejected"
  | "none"

export interface TaskImpactActionState {
  kind: TaskImpactActionKind
  canStart: boolean
  canRetry: boolean
  canApprove: boolean
  activeExecutionId: string | null
  message: string | null
}

export function formatDurationSeconds(totalSeconds: number): string {
  if (totalSeconds < 0 || isNaN(totalSeconds)) return "0s"
  if (totalSeconds < 60) return `${totalSeconds}s`
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return seconds > 0 ? `${minutes}m ${seconds}s` : `${minutes}m`
}

export function sanitizeErrorMessage(rawError?: string | null): string {
  if (!rawError || !rawError.trim()) return "Impact analysis failed."
  let sanitized = rawError.trim()
  sanitized = sanitized.replace(/bearer\s+[a-zA-Z0-9_\-\.]+/gi, "Bearer [REDACTED]")
  sanitized = sanitized.replace(/(api[_-]?key|secret|password)\s*[:=]\s*["']?[^"'\s]+["']?/gi, "$1=[REDACTED]")
  return sanitized
}

/**
 * Deterministically derives the unified server-authoritative lifecycle for TaskImpact.
 *
 * Invariant:
 * - Analyzing: analysis InProgress or task Analyzing (and not terminal failed/succeeded).
 *   Run/Retry disabled, spinner mounted, authoritative elapsed timer running.
 * - Succeeded: analysis Completed with structuredResult.
 * - Failed: analysis Failed or task Failed with terminal reason. Retry available if no active analysis/execution.
 * - Idle: initial unanalyzed task state. Run button available.
 */
export function deriveTaskImpactLifecycle(
  task: Pick<Task, "status" | "createdAt" | "updatedAt"> | null,
  analysis: Pick<ImpactAnalysis, "status" | "createdAt" | "completedAt" | "errorMessage" | "structuredResult"> | null,
  activeExecution: Pick<ExecutionListItem, "id" | "status"> | null = null,
  isMockView: boolean = false,
  mockStatus?: string,
  nowMs: number = Date.now(),
): TaskImpactLifecycleState {
  if (isMockView) {
    if (mockStatus === "analyzing") {
      return {
        lifecycle: "analyzing",
        statusTone: "blue",
        statusLabel: "Analyzing",
        canRun: false,
        canRetry: false,
        canApprove: false,
        isAnalyzing: true,
        isSucceeded: false,
        isFailed: false,
        elapsedSeconds: 12,
        durationFormatted: null,
        sanitizedErrorMessage: null,
      }
    }
    return {
      lifecycle: "succeeded",
      statusTone: mockStatus === "approved" ? "blue" : mockStatus === "rejected" ? "red" : "amber",
      statusLabel: mockStatus === "approved" ? "Approved" : mockStatus === "rejected" ? "Rejected" : "Awaiting approval",
      canRun: false,
      canRetry: false,
      canApprove: mockStatus !== "approved" && mockStatus !== "rejected",
      isAnalyzing: false,
      isSucceeded: true,
      isFailed: false,
      elapsedSeconds: 0,
      durationFormatted: "24s",
      sanitizedErrorMessage: null,
    }
  }

  // 1. Check if an analysis is actively in progress
  const isAnalysisInProgress =
    analysis?.status === ImpactAnalysisStatus.InProgress ||
    (task?.status === TaskStatus.Analyzing && analysis?.status !== ImpactAnalysisStatus.Completed && analysis?.status !== ImpactAnalysisStatus.Failed)

  if (isAnalysisInProgress) {
    const startedTime = analysis?.createdAt || task?.updatedAt || task?.createdAt
    let elapsed = 0
    if (startedTime) {
      const parsed = new Date(startedTime).getTime()
      if (!isNaN(parsed)) {
        elapsed = Math.max(0, Math.floor((nowMs - parsed) / 1000))
      }
    }

    return {
      lifecycle: "analyzing",
      statusTone: "blue",
      statusLabel: "Analyzing",
      canRun: false,
      canRetry: false,
      canApprove: false,
      isAnalyzing: true,
      isSucceeded: false,
      isFailed: false,
      elapsedSeconds: elapsed,
      durationFormatted: null,
      sanitizedErrorMessage: null,
    }
  }

  // 2. Check if a completed analysis exists
  const isCompleted =
    analysis !== null &&
    analysis.status === ImpactAnalysisStatus.Completed &&
    analysis.structuredResult !== null

  if (isCompleted) {
    let durationStr: string | null = null
    if (analysis.createdAt && analysis.completedAt) {
      const start = new Date(analysis.createdAt).getTime()
      const end = new Date(analysis.completedAt).getTime()
      if (!isNaN(start) && !isNaN(end) && end >= start) {
        durationStr = formatDurationSeconds(Math.round((end - start) / 1000))
      }
    }

    let statusLabel = "Awaiting approval"
    let statusTone: Tone = "amber"
    if (task?.status === TaskStatus.Approved) {
      statusLabel = "Approved"
      statusTone = "blue"
    } else if (task?.status === TaskStatus.Executing) {
      statusLabel = "Executing"
      statusTone = "blue"
    } else if (task?.status === TaskStatus.Completed) {
      statusLabel = "Merged"
      statusTone = "green"
    } else if (task?.status === TaskStatus.Rejected) {
      statusLabel = "Rejected"
      statusTone = "red"
    } else if (task?.status === TaskStatus.Failed) {
      statusLabel = "Failed"
      statusTone = "red"
    }

    return {
      lifecycle: "succeeded",
      statusTone,
      statusLabel,
      canRun: false,
      canRetry: false,
      canApprove: task?.status === TaskStatus.AwaitingApproval,
      isAnalyzing: false,
      isSucceeded: true,
      isFailed: false,
      elapsedSeconds: 0,
      durationFormatted: durationStr,
      sanitizedErrorMessage: null,
    }
  }

  // 3. Check if analysis is in terminal Failed state
  const isFailed =
    analysis?.status === ImpactAnalysisStatus.Failed ||
    (task?.status === TaskStatus.Failed && !isAnalysisInProgress)

  if (isFailed) {
    let durationStr: string | null = null
    if (analysis?.createdAt && analysis?.completedAt) {
      const start = new Date(analysis.createdAt).getTime()
      const end = new Date(analysis.completedAt).getTime()
      if (!isNaN(start) && !isNaN(end) && end >= start) {
        durationStr = formatDurationSeconds(Math.round((end - start) / 1000))
      }
    }

    const hasActiveExec = activeExecution != null
    return {
      lifecycle: "failed",
      statusTone: "red",
      statusLabel: "Failed",
      canRun: false,
      canRetry: !hasActiveExec,
      canApprove: false,
      isAnalyzing: false,
      isSucceeded: false,
      isFailed: true,
      elapsedSeconds: 0,
      durationFormatted: durationStr,
      sanitizedErrorMessage: sanitizeErrorMessage(analysis?.errorMessage),
    }
  }

  // 4. Otherwise task is in Idle un-analyzed state
  let initialLabel = "Draft"
  let initialTone: Tone = "gray"
  if (task?.status === TaskStatus.ReadyForAnalysis) {
    initialLabel = "Ready for Analysis"
    initialTone = "neutral"
  }

  return {
    lifecycle: "idle",
    statusTone: initialTone,
    statusLabel: initialLabel,
    canRun: true,
    canRetry: false,
    canApprove: false,
    isAnalyzing: false,
    isSucceeded: false,
    isFailed: false,
    elapsedSeconds: 0,
    durationFormatted: null,
    sanitizedErrorMessage: null,
  }
}

/**
 * Deterministically derives the action state for the TaskImpact page execution panel.
 */
export function deriveTaskImpactActionState(
  taskStatus: number | null | undefined,
  activeExecution: Pick<ExecutionListItem, "id" | "status"> | null,
  isMockView: boolean = false,
  mockStatus?: string,
): TaskImpactActionState {
  if (isMockView) {
    if (mockStatus === "approved") {
      return {
        kind: "approved",
        canStart: true,
        canRetry: false,
        canApprove: false,
        activeExecutionId: null,
        message: null,
      }
    }
    if (mockStatus === "rejected") {
      return {
        kind: "rejected",
        canStart: false,
        canRetry: false,
        canApprove: false,
        activeExecutionId: null,
        message: null,
      }
    }
    return {
      kind: "awaiting-approval",
      canStart: false,
      canRetry: false,
      canApprove: true,
      activeExecutionId: null,
      message: null,
    }
  }

  // A) Server reports an actual active execution (Pending or Running)
  if (activeExecution != null) {
    return {
      kind: "active-execution",
      canStart: false,
      canRetry: false,
      canApprove: false,
      activeExecutionId: activeExecution.id,
      message:
        activeExecution.status === TaskExecutionStatus.Running
          ? "Agent is currently executing the task."
          : "Execution is queued and will begin processing shortly.",
    }
  }

  // B) Task status claims Executing, but server execution query returned no active execution (syncing state)
  if (taskStatus === TaskStatus.Executing) {
    return {
      kind: "syncing-execution",
      canStart: false,
      canRetry: false,
      canApprove: false,
      activeExecutionId: null,
      message: "Execution state is syncing with the server…",
    }
  }

  // C) Task is Approved and no active execution exists
  if (taskStatus === TaskStatus.Approved) {
    return {
      kind: "approved",
      canStart: true,
      canRetry: false,
      canApprove: false,
      activeExecutionId: null,
      message: null,
    }
  }

  // D) Task is Failed and no active execution exists
  if (taskStatus === TaskStatus.Failed) {
    return {
      kind: "failed",
      canStart: false,
      canRetry: true,
      canApprove: false,
      activeExecutionId: null,
      message: null,
    }
  }

  // E) Task is AwaitingApproval and no active execution exists
  if (taskStatus === TaskStatus.AwaitingApproval) {
    return {
      kind: "awaiting-approval",
      canStart: false,
      canRetry: false,
      canApprove: true,
      activeExecutionId: null,
      message: null,
    }
  }

  // F) Task is Rejected
  if (taskStatus === TaskStatus.Rejected) {
    return {
      kind: "rejected",
      canStart: false,
      canRetry: false,
      canApprove: false,
      activeExecutionId: null,
      message: null,
    }
  }

  return {
    kind: "none",
    canStart: false,
    canRetry: false,
    canApprove: false,
    activeExecutionId: null,
    message: null,
  }
}
