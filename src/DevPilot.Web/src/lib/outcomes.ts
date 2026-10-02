import i18n from "@/i18n"
import type { Tone } from "@/components/ui/primitives"

const outcomeTones: Record<string, Tone> = {
  Verified: "green",
  NoNewRegressions: "green",
  PartiallyVerified: "amber",
  VerificationUnavailable: "gray",
  VerificationInfrastructureError: "red",
  NeedsReview: "amber",
  Failed: "red",
  Blocked: "gray",
}

export function getOutcomeMeta(outcome?: string | null): { label: string; tone: Tone } {
  const tone = outcomeTones[outcome ?? ""]
  if (!tone) return { label: outcome || i18n.t("shared.outcome.Unknown"), tone: "neutral" }
  return { label: i18n.t(`shared.outcome.${outcome}`), tone }
}

/** Deliverable = a human may commit, push and open a PR (mirrors the server-side delivery eligibility). */
export function isDeliverableOutcome(outcome?: string | null): boolean {
  return (
    outcome === "Verified" ||
    outcome === "NoNewRegressions" ||
    outcome === "PartiallyVerified" ||
    outcome === "VerificationUnavailable" ||
    outcome === "VerificationInfrastructureError"
  )
}

export function formatSeconds(seconds?: number | null): string {
  if (seconds == null) return "—"
  if (seconds < 60) return `${seconds}${i18n.t("shared.units.s")}`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}${i18n.t("shared.units.m")} ${seconds % 60}${i18n.t("shared.units.s")}`
  return `${Math.floor(minutes / 60)}${i18n.t("shared.units.h")} ${minutes % 60}${i18n.t("shared.units.m")}`
}

export function formatMs(ms?: number | null): string {
  if (ms == null) return "—"
  return ms < 1000 ? `${ms}${i18n.t("shared.units.ms")}` : formatSeconds(Math.round(ms / 1000))
}

export function formatPercent(rate?: number | null): string {
  return rate == null ? "—" : `${Math.round(rate * 100)}%`
}

export function formatTokenCount(tokens?: number | null): string {
  if (tokens == null) return "—"
  if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(1)}M`
  if (tokens >= 1000) return `${(tokens / 1000).toFixed(1)}K`
  return String(tokens)
}

export function formatCost(cost?: number | null): string {
  return cost == null ? "—" : `$${cost.toFixed(cost < 1 ? 3 : 2)}`
}
