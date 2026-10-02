import i18n from "@/i18n"
import type { ExecutionDetail } from "@/types"

/** Wall-clock run time in seconds, or null while the run has not finished. */
export function durationSeconds(e: ExecutionDetail): number | null {
  if (!e.startedAt || !e.completedAt) return null
  return Math.max(0, Math.round((new Date(e.completedAt).getTime() - new Date(e.startedAt).getTime()) / 1000))
}

export function stageMs(e: ExecutionDetail, stage: string): number | null {
  return e.usage?.stageTimings.find((t) => t.stage === stage)?.durationMs ?? null
}

/** Human readable list of the reliability signals raised on a run. */
export function flagList(e: ExecutionDetail): string {
  const v = e.verdict
  if (!v) return "—"
  const flags = [
    v.baselineUnverified && i18n.t("compare.flags.baselineUnverified"),
    v.flakeConfirmed && i18n.t("compare.flags.flake"),
    v.testWeakeningSuspected && i18n.t("compare.flags.weakening"),
    v.staleBase && i18n.t("compare.flags.staleBase"),
  ].filter(Boolean) as string[]
  return flags.length ? flags.join(", ") : i18n.t("compare.flags.none")
}
