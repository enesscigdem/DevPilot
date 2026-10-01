import { useEffect, useState } from "react"
import { Link } from "react-router-dom"
import { AlertCircle, ArrowRight, BarChart3, GitCompareArrows, Loader2, ShieldCheck, TrendingUp } from "lucide-react"
import { getWorkspaceInsights } from "@/api"
import { PageContainer, PageHeading, SectionHead } from "@/components/shared"
import { Badge, Panel } from "@/components/ui/primitives"
import { useWorkspace } from "@/lib/workspace"
import { cn } from "@/lib/utils"
import {
  formatCost,
  formatPercent,
  formatSeconds,
  formatTokenCount,
  getOutcomeMeta,
} from "@/lib/outcomes"
import type { InsightSignal, WorkspaceInsights } from "@/types"

const outcomeBarClass: Record<string, string> = {
  green: "bg-success",
  amber: "bg-accent",
  red: "bg-danger",
  gray: "bg-border-strong",
  neutral: "bg-border-strong",
  blue: "bg-primary",
}

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Panel className="p-4">
      <div className="tech-label">{label}</div>
      <div className="mt-1.5 text-[22px] font-semibold tracking-tight text-foreground">{value}</div>
      {hint && <div className="mt-0.5 text-[11.5px] text-subtle-foreground">{hint}</div>}
    </Panel>
  )
}

function SignalCard({ signal }: { signal: InsightSignal }) {
  return (
    <Panel className={cn("p-4", signal.count === 0 && "opacity-70")}>
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-[13px] font-semibold text-foreground">{signal.label}</span>
        <span className="font-mono text-[18px] font-semibold text-foreground">{signal.count}</span>
      </div>
      <p className="mt-1 text-[11.5px] leading-relaxed text-muted-foreground">{signal.description}</p>
    </Panel>
  )
}

export function Insights() {
  const { activeWorkspaceId, activeWorkspace, isLoading: isWorkspaceLoading } = useWorkspace()
  const [insights, setInsights] = useState<WorkspaceInsights | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (isWorkspaceLoading) return
    if (!activeWorkspaceId) {
      setInsights(null)
      setIsLoading(false)
      return
    }

    const controller = new AbortController()
    setIsLoading(true)
    setError(null)
    getWorkspaceInsights(activeWorkspaceId, { signal: controller.signal })
      .then((data) => setInsights(data))
      .catch((err) => {
        if (controller.signal.aborted) return
        setError(err instanceof Error ? err.message : "Failed to load insights.")
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsLoading(false)
      })
    return () => controller.abort()
  }, [activeWorkspaceId, isWorkspaceLoading])

  const repoName = activeWorkspace ? `${activeWorkspace.owner}/${activeWorkspace.repository}` : "this repository"

  const heading = (
    <PageHeading
      eyebrow="Insights"
      title="Engineering insights"
      description={`How reliably DevPilot turns tasks into verified, reviewable changes in ${repoName}: success, repair, latency and AI cost, computed from recorded executions.`}
    />
  )

  if (isLoading || isWorkspaceLoading) {
    return (
      <PageContainer>
        {heading}
        <div className="flex items-center justify-center gap-2 py-20 text-subtle-foreground">
          <Loader2 className="h-5 w-5 animate-spin text-primary" />
          <span className="tech-label">Loading insights…</span>
        </div>
      </PageContainer>
    )
  }

  if (error) {
    return (
      <PageContainer>
        {heading}
        <Panel className="flex items-center gap-2 p-4 text-[13px] text-danger">
          <AlertCircle className="h-4 w-4 shrink-0" />
          {error}
        </Panel>
      </PageContainer>
    )
  }

  if (!insights || insights.totals.executions === 0) {
    return (
      <PageContainer>
        {heading}
        <Panel className="flex flex-col items-center gap-2 p-10 text-center">
          <BarChart3 className="h-7 w-7 text-subtle-foreground" />
          <h2 className="text-[15px] font-semibold text-foreground">No finished executions yet</h2>
          <p className="max-w-md text-[13px] text-muted-foreground">
            Insights appear after the first execution finishes. Approve a task plan and run it to start measuring.
          </p>
          <Link to="/tasks" className="mt-1 text-[13px] font-medium text-primary hover:underline">
            Go to tasks
          </Link>
        </Panel>
      </PageContainer>
    )
  }

  const { totals } = insights
  const maxOutcome = Math.max(1, ...insights.outcomes.map((o) => o.count))
  const hasCost = totals.totalCostUsd != null

  return (
    <PageContainer>
      {heading}

      <div className="mb-2 text-[11.5px] text-subtle-foreground">
        Based on the last {insights.windowSize} finished execution{insights.windowSize === 1 ? "" : "s"}
        {totals.cancelled > 0 && ` (${totals.cancelled} cancelled, excluded from rates)`}.
      </div>

      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <Stat
          label="Delivery-ready"
          value={formatPercent(totals.deliveryReadyRate)}
          hint={`${totals.deliveryReady} of ${totals.measured} runs`}
        />
        <Stat
          label="Fully verified"
          value={formatPercent(totals.verifiedRate)}
          hint="build + tests, or no new regressions"
        />
        <Stat
          label="First pass"
          value={formatPercent(totals.firstPassRate)}
          hint={`${totals.firstPass} runs needed no repair`}
        />
        <Stat
          label="Repair recovery"
          value={formatPercent(totals.repairRecoveryRate)}
          hint={`${totals.repairedRecovered} of ${totals.repaired} repaired runs`}
        />
        <Stat
          label="Median run time"
          value={formatSeconds(totals.medianDurationSeconds)}
          hint={`avg ${formatSeconds(totals.avgDurationSeconds)}`}
        />
        <Stat
          label={hasCost ? "AI cost" : "AI tokens"}
          value={hasCost ? formatCost(totals.totalCostUsd) : formatTokenCount(totals.totalTokens)}
          hint={
            hasCost
              ? `${formatTokenCount(totals.totalTokens)} tokens`
              : totals.avgTokensPerExecution != null
                ? `~${formatTokenCount(totals.avgTokensPerExecution)} per run · set AiPricing for cost`
                : "no token data recorded"
          }
        />
      </div>

      <section className="mt-8">
        <SectionHead title="What DevPilot caught or absorbed" />
        <p className="mb-3 max-w-3xl text-[12.5px] text-muted-foreground">
          These are the guarantees a plain coding agent does not give you: failures are compared with the base commit,
          repairs are bounded, and risky automatic changes are escalated instead of hidden.
        </p>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-3">
          {insights.signals.map((signal) => (
            <SignalCard key={signal.key} signal={signal} />
          ))}
        </div>
      </section>

      <div className="mt-8 grid grid-cols-1 gap-6 lg:grid-cols-2">
        <section>
          <SectionHead title="Outcomes" />
          <Panel className="space-y-2.5 p-4">
            {insights.outcomes.map((item) => {
              const meta = getOutcomeMeta(item.outcome)
              return (
                <div key={item.outcome}>
                  <div className="mb-1 flex items-center justify-between text-[12px]">
                    <span className="font-medium text-foreground">{meta.label}</span>
                    <span className="font-mono text-subtle-foreground">{item.count}</span>
                  </div>
                  <div className="h-2 overflow-hidden rounded-full bg-surface-3">
                    <div
                      className={cn("h-full rounded-full", outcomeBarClass[meta.tone] ?? "bg-border-strong")}
                      style={{ width: `${Math.max(4, (item.count / maxOutcome) * 100)}%` }}
                    />
                  </div>
                </div>
              )
            })}
          </Panel>
        </section>

        <section>
          <SectionHead title="Why runs needed a human" count={insights.topFailureReasons.length} />
          <Panel className="divide-y divide-border">
            {insights.topFailureReasons.length === 0 ? (
              <div className="flex items-center gap-2 p-4 text-[12.5px] text-muted-foreground">
                <ShieldCheck className="h-4 w-4 text-success" />
                No runs ended in Needs review or Failed.
              </div>
            ) : (
              insights.topFailureReasons.map((reason) => (
                <div key={reason.reason} className="flex items-start justify-between gap-3 p-3.5">
                  <span className="text-[12.5px] leading-relaxed text-foreground break-words">{reason.reason}</span>
                  <Badge tone="amber">{reason.count}×</Badge>
                </div>
              ))
            )}
          </Panel>
        </section>
      </div>

      {insights.retriedTasks.length > 0 && (
        <section className="mt-8">
          <SectionHead title="Retried tasks" count={insights.retriedTasks.length} />
          <Panel className="divide-y divide-border">
            {insights.retriedTasks.map((task) => {
              const first = getOutcomeMeta(task.firstOutcome)
              const last = getOutcomeMeta(task.lastOutcome)
              return (
                <div key={task.taskId} className="flex flex-wrap items-center gap-3 p-3.5">
                  <div className="min-w-0 flex-1">
                    <Link to={`/tasks/${task.taskId}`} className="truncate text-[13px] font-medium text-foreground hover:text-primary">
                      {task.taskTitle}
                    </Link>
                    <div className="mt-1 flex items-center gap-1.5 text-[11.5px] text-subtle-foreground">
                      {task.attempts} attempts
                      <span>·</span>
                      <Badge tone={first.tone}>{first.label}</Badge>
                      <ArrowRight className="h-3 w-3" />
                      <Badge tone={last.tone}>{last.label}</Badge>
                      {task.improved && (
                        <span className="flex items-center gap-1 text-success">
                          <TrendingUp className="h-3 w-3" /> improved
                        </span>
                      )}
                    </div>
                  </div>
                  <Link
                    to={`/executions/compare?a=${task.firstExecutionId}&b=${task.lastExecutionId}`}
                    className="flex items-center gap-1.5 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-1.5 text-[12px] font-medium text-muted-foreground hover:border-border-strong hover:text-foreground"
                  >
                    <GitCompareArrows className="h-3.5 w-3.5" />
                    Compare first vs latest
                  </Link>
                </div>
              )
            })}
          </Panel>
        </section>
      )}

      <section className="mt-8">
        <SectionHead title="Recent executions" count={insights.recent.length} />
        <Panel className="overflow-x-auto">
          <table className="w-full min-w-[760px] text-left text-[12px]">
            <thead>
              <tr className="border-b border-border text-subtle-foreground">
                <th className="px-3.5 py-2.5 font-medium">Task</th>
                <th className="px-3 py-2.5 font-medium">Outcome</th>
                <th className="px-3 py-2.5 text-right font-medium">Time</th>
                <th className="px-3 py-2.5 text-right font-medium">Repairs</th>
                <th className="px-3 py-2.5 text-right font-medium">AI calls</th>
                <th className="px-3 py-2.5 text-right font-medium">Tokens</th>
                <th className="px-3.5 py-2.5 text-right font-medium">Cost</th>
              </tr>
            </thead>
            <tbody>
              {insights.recent.map((row) => {
                const meta = getOutcomeMeta(row.outcome)
                return (
                  <tr key={row.executionId} className="border-b border-border/60 last:border-b-0 hover:bg-surface-2">
                    <td className="max-w-[340px] px-3.5 py-2.5">
                      <Link to={`/executions/${row.executionId}`} className="block truncate font-medium text-foreground hover:text-primary" title={row.headline}>
                        {row.taskTitle}
                      </Link>
                      {row.attemptNumber > 1 && (
                        <span className="font-mono text-[10.5px] text-subtle-foreground">attempt {row.attemptNumber}</span>
                      )}
                    </td>
                    <td className="px-3 py-2.5">
                      <Badge tone={meta.tone}>{meta.label}</Badge>
                    </td>
                    <td className="px-3 py-2.5 text-right font-mono text-muted-foreground">{formatSeconds(row.durationSeconds)}</td>
                    <td className="px-3 py-2.5 text-right font-mono text-muted-foreground">{row.repairRounds}</td>
                    <td className="px-3 py-2.5 text-right font-mono text-muted-foreground">{row.providerCalls}</td>
                    <td className="px-3 py-2.5 text-right font-mono text-muted-foreground">{formatTokenCount(row.totalTokens)}</td>
                    <td className="px-3.5 py-2.5 text-right font-mono text-muted-foreground">{formatCost(row.estimatedCostUsd)}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </Panel>
      </section>
    </PageContainer>
  )
}
