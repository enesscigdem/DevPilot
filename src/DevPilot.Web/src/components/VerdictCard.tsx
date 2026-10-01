import { AlertTriangle, CheckCircle2, Info, ShieldAlert, XCircle } from "lucide-react"
import { Panel } from "@/components/ui/primitives"
import { cn } from "@/lib/utils"
import type { ExecutionUsage, ExecutionVerdict, VerdictFinding } from "@/types"

const severityStyles: Record<string, { box: string; icon: typeof Info; iconClass: string }> = {
  success: { box: "border-success/30 bg-success-soft", icon: CheckCircle2, iconClass: "text-success" },
  warning: { box: "border-amber-500/30 bg-amber-500/10", icon: AlertTriangle, iconClass: "text-amber-500" },
  danger: { box: "border-danger/30 bg-danger-soft", icon: XCircle, iconClass: "text-danger" },
  neutral: { box: "border-border bg-surface-2", icon: Info, iconClass: "text-muted-foreground" },
}

const findingIcon: Record<string, { icon: typeof Info; className: string }> = {
  success: { icon: CheckCircle2, className: "text-success" },
  info: { icon: Info, className: "text-subtle-foreground" },
  warning: { icon: AlertTriangle, className: "text-amber-500" },
  danger: { icon: ShieldAlert, className: "text-danger" },
}

function FindingRow({ finding }: { finding: VerdictFinding }) {
  const meta = findingIcon[finding.severity] ?? findingIcon.info
  const Icon = meta.icon
  return (
    <li className="flex items-start gap-1.5 text-[11.5px] leading-relaxed text-muted-foreground">
      <Icon className={cn("mt-[2px] h-3 w-3 shrink-0", meta.className)} />
      <span className="break-words">{finding.message}</span>
    </li>
  )
}

/** Explains the verification outcome: what happened, why, and what to do next. */
export function VerdictCard({ verdict }: { verdict: ExecutionVerdict }) {
  const style = severityStyles[verdict.severity] ?? severityStyles.neutral
  const Icon = style.icon

  return (
    <Panel className={cn("space-y-2.5 p-3", style.box)}>
      <div className="flex items-start gap-2">
        <Icon className={cn("mt-[1px] h-4 w-4 shrink-0", style.iconClass)} />
        <div className="min-w-0">
          <div className="text-[12.5px] font-semibold leading-snug text-foreground break-words">{verdict.headline}</div>
          {verdict.recommendedAction && (
            <div className="mt-1 text-[11.5px] leading-relaxed text-muted-foreground break-words">
              <span className="font-medium text-foreground">Next: </span>
              {verdict.recommendedAction}
            </div>
          )}
        </div>
      </div>
      {verdict.findings.length > 0 && (
        <ul className="space-y-1 border-t border-border/60 pt-2">
          {verdict.findings.map((finding, i) => (
            <FindingRow key={`${finding.kind}-${i}`} finding={finding} />
          ))}
        </ul>
      )}
    </Panel>
  )
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  const seconds = Math.round(ms / 1000)
  if (seconds < 60) return `${seconds}s`
  return `${Math.floor(seconds / 60)}m ${seconds % 60}s`
}

function formatTokens(tokens: number): string {
  return tokens >= 1000 ? `${(tokens / 1000).toFixed(1)}K` : String(tokens)
}

/** AI usage, latency and (when a price table is configured) estimated cost for one execution. */
export function UsagePanel({ usage }: { usage: ExecutionUsage }) {
  if (usage.providerCalls === 0 && usage.stageTimings.length === 0) {
    return null
  }

  return (
    <Panel className="space-y-2 p-3 font-mono text-[11px]">
      <div className="tech-label">AI usage &amp; latency</div>
      <div className="flex items-center justify-between text-subtle-foreground">
        <span>Provider calls</span>
        <span className="text-foreground">
          {usage.providerCalls}
          {usage.failedProviderCalls > 0 && <span className="text-danger"> ({usage.failedProviderCalls} failed)</span>}
        </span>
      </div>
      <div className="flex items-center justify-between text-subtle-foreground">
        <span>Tokens (in / out)</span>
        <span className="text-foreground">
          {usage.totalTokens > 0 ? `${formatTokens(usage.inputTokens)} / ${formatTokens(usage.outputTokens)}` : "—"}
        </span>
      </div>
      {usage.callsWithoutTokenData > 0 && (
        <div className="text-[10.5px] text-subtle-foreground">
          {usage.callsWithoutTokenData} call(s) did not report token usage.
        </div>
      )}
      <div className="flex items-center justify-between text-subtle-foreground">
        <span>Est. cost</span>
        <span className="text-foreground">
          {usage.estimatedCostUsd != null ? `$${usage.estimatedCostUsd.toFixed(3)}` : "not configured"}
        </span>
      </div>
      <div className="flex items-center justify-between text-subtle-foreground">
        <span>Provider time</span>
        <span className="text-foreground">{formatDuration(usage.providerTimeMs)}</span>
      </div>
      {usage.stageTimings.length > 0 && (
        <div className="space-y-1 border-t border-border/60 pt-2">
          {usage.stageTimings.map((timing) => (
            <div key={timing.stage} className="flex items-center justify-between text-subtle-foreground">
              <span>{timing.stage}</span>
              <span className="text-foreground">{formatDuration(timing.durationMs)}</span>
            </div>
          ))}
        </div>
      )}
    </Panel>
  )
}
