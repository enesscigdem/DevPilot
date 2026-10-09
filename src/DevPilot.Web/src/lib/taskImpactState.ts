import i18n from "@/i18n"
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
  const s = i18n.t("shared.units.s")
  const m = i18n.t("shared.units.m")
  if (totalSeconds < 0 || isNaN(totalSeconds)) return `0${s}`
  if (totalSeconds < 60) return `${totalSeconds}${s}`
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return seconds > 0 ? `${minutes}${m} ${seconds}${s}` : `${minutes}${m}`
}

export function sanitizeErrorMessage(rawError?: string | null): string {
  if (!rawError || !rawError.trim()) return i18n.t("impact.analysisFailedShort")
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
  nowMs: number = Date.now(),
): TaskImpactLifecycleState {
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
      statusLabel: i18n.t("shared.taskStatus.analyzing"),
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

    let statusLabel = i18n.t("shared.taskStatus.awaitingApproval")
    let statusTone: Tone = "amber"
    if (task?.status === TaskStatus.Approved) {
      statusLabel = i18n.t("shared.taskStatus.approved")
      statusTone = "blue"
    } else if (task?.status === TaskStatus.Executing) {
      statusLabel = i18n.t("shared.taskStatus.executing")
      statusTone = "blue"
    } else if (task?.status === TaskStatus.Completed) {
      statusLabel = i18n.t("shared.taskStatus.completed")
      statusTone = "green"
    } else if (task?.status === TaskStatus.Rejected) {
      statusLabel = i18n.t("shared.taskStatus.rejected")
      statusTone = "red"
    } else if (task?.status === TaskStatus.Failed) {
      statusLabel = i18n.t("shared.taskStatus.failed")
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
      statusLabel: i18n.t("shared.taskStatus.failed"),
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
  let initialLabel = i18n.t("shared.taskStatus.draft")
  let initialTone: Tone = "gray"
  if (task?.status === TaskStatus.ReadyForAnalysis) {
    initialLabel = i18n.t("shared.taskStatus.readyForAnalysis")
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
): TaskImpactActionState {
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
          ? i18n.t("impact.state.running")
          : i18n.t("impact.state.queued"),
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
      message: i18n.t("impact.state.syncing"),
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
