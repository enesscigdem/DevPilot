import { TaskStatus, TaskExecutionStatus, type ExecutionListItem } from "@/types"

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

/**
 * Deterministically derives the action state for the TaskImpact page.
 *
 * Rules:
 * 1. An active execution exists ONLY when activeExecution is non-null (server returned Pending/Running execution).
 * 2. If task claims Executing but activeExecution is null, treat as synchronizing: Start/Retry unavailable, no fake live button.
 * 3. Only show Start when activeExecution is null AND task is Approved.
 * 4. Only show Retry when activeExecution is null AND task is Failed.
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
