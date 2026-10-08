import { currentLocale } from "@/i18n"
import type { Tone } from "@/components/ui/primitives"
import type { GoalPhase, GoalProgress, GoalStatus, GoalTaskView } from "@/types"

export const STATUS_TONE: Record<GoalStatus, Tone> = {
  Active: "blue",
  Completed: "green",
  Cancelled: "gray",
}

/** 1_240_000 becomes "1.2M", 38_000 becomes "38K": estimates are rough, so extra digits would only lie. */
export function formatTokens(tokens: number): string {
  if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(1).replace(/\.0$/, "")}M`
  if (tokens >= 1_000) return `${Math.round(tokens / 1_000)}K`
  return String(tokens)
}

export function formatUsd(usd: number): string {
  return new Intl.NumberFormat(currentLocale(), {
    style: "currency",
    currency: "USD",
    maximumFractionDigits: usd < 10 ? 2 : 0,
  }).format(usd)
}

/** Colour and motion of each phase, so the whole screen reads at a glance. */
export const PHASE_STYLE: Record<GoalPhase, { tone: Tone; pulse: boolean }> = {
  Waiting: { tone: "gray", pulse: false },
  Analyzing: { tone: "blue", pulse: true },
  PlanReady: { tone: "amber", pulse: false },
  Queued: { tone: "blue", pulse: false },
  Running: { tone: "blue", pulse: true },
  InReview: { tone: "amber", pulse: false },
  Delivering: { tone: "blue", pulse: true },
  PullRequest: { tone: "blue", pulse: false },
  Merged: { tone: "green", pulse: false },
  Failed: { tone: "red", pulse: false },
  Stopped: { tone: "gray", pulse: false },
}

/**
 * How a task is shown. The server decides whether a person is needed (`needsPerson`); a plan or a finished run that
 * automation is still going to handle is shown as work in progress, not as a call for attention.
 */
export function phaseView(task: Pick<GoalTaskView, "phase" | "needsPerson">): { label: string; tone: Tone; pulse: boolean } {
  const { phase, needsPerson } = task
  if (!needsPerson && phase === "InReview") return { label: "Verifying", tone: "blue", pulse: true }
  if (!needsPerson && phase === "PlanReady") return { label: phase, tone: "blue", pulse: false }
  return { label: phase, ...PHASE_STYLE[phase] }
}

export function progressPercent(progress: GoalProgress): number {
  return progress.total === 0 ? 0 : Math.round((progress.merged / progress.total) * 100)
}

/** "t3" to "#3": the short label shown on a task card. */
export function shortKey(key: string): string {
  return key.replace(/^t/i, "#")
}
