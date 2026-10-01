import type { Tone } from "@/components/ui/primitives"

const outcomeMeta: Record<string, { label: string; tone: Tone }> = {
  Verified: { label: "Verified", tone: "green" },
  NoNewRegressions: { label: "No new regressions", tone: "green" },
  PartiallyVerified: { label: "Partially verified", tone: "amber" },
  VerificationUnavailable: { label: "Not verified", tone: "gray" },
  VerificationInfrastructureError: { label: "Verification infra error", tone: "red" },
  NeedsReview: { label: "Needs review", tone: "amber" },
  Failed: { label: "Failed", tone: "red" },
  Blocked: { label: "Cancelled", tone: "gray" },
}

export function getOutcomeMeta(outcome?: string | null): { label: string; tone: Tone } {
  return outcomeMeta[outcome ?? ""] ?? { label: outcome || "Unknown", tone: "neutral" }
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
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`
}

export function formatMs(ms?: number | null): string {
  if (ms == null) return "—"
  return ms < 1000 ? `${ms}ms` : formatSeconds(Math.round(ms / 1000))
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
