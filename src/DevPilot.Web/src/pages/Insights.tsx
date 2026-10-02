import { useEffect, useState } from "react"
import { Link } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { srv } from "@/i18n"
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
  useTranslation()
  return (
    <Panel className={cn("p-4", signal.count === 0 && "opacity-70")}>
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-[13px] font-semibold text-foreground">{srv(signal.label)}</span>
        <span className="font-mono text-[18px] font-semibold text-foreground">{signal.count}</span>
      </div>
      <p className="mt-1 text-[11.5px] leading-relaxed text-muted-foreground">{srv(signal.description)}</p>
    </Panel>
  )
}

export function Insights() {
  const { t } = useTranslation()
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
        setError(err instanceof Error ? err.message : t("insights.errLoad"))
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsLoading(false)
      })
    return () => controller.abort()
  }, [activeWorkspaceId, isWorkspaceLoading])

  const repoName = activeWorkspace ? `${activeWorkspace.owner}/${activeWorkspace.repository}` : t("insights.thisRepository")

  const heading = (
    <PageHeading
      eyebrow={t("insights.eyebrow")}
      title={t("insights.title")}
      description={t("insights.description", { repo: repoName })}
    />
  )

  if (isLoading || isWorkspaceLoading) {
    return (
      <PageContainer>
        {heading}
        <div className="flex items-center justify-center gap-2 py-20 text-subtle-foreground">
          <Loader2 className="h-5 w-5 animate-spin text-primary" />
          <span className="tech-label">{t("insights.loading")}</span>
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
          <h2 className="text-[15px] font-semibold text-foreground">{t("insights.emptyTitle")}</h2>
          <p className="max-w-md text-[13px] text-muted-foreground">
            {t("insights.emptyDesc")}
          </p>
          <Link to="/tasks" className="mt-1 text-[13px] font-medium text-primary hover:underline">
            {t("insights.goTasks")}
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
        {t("insights.basedOn", { count: insights.windowSize })}
        {totals.cancelled > 0 && t("insights.cancelledNote", { n: totals.cancelled })}.
      </div>

      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <Stat
          label={t("insights.deliveryReady")}
          value={formatPercent(totals.deliveryReadyRate)}
          hint={t("insights.runsOf", { ready: totals.deliveryReady, total: totals.measured })}
        />
        <Stat
          label={t("insights.fullyVerified")}
          value={formatPercent(totals.verifiedRate)}
          hint={t("insights.fullyVerifiedHint")}
        />
        <Stat
          label={t("insights.firstPass")}
          value={formatPercent(totals.firstPassRate)}
          hint={t("insights.firstPassHint", { n: totals.firstPass })}
        />
        <Stat
          label={t("insights.repairRecovery")}
          value={formatPercent(totals.repairRecoveryRate)}
          hint={t("insights.repairRecoveryHint", { ok: totals.repairedRecovered, total: totals.repaired })}
        />
        <Stat
          label={t("insights.medianRun")}
          value={formatSeconds(totals.medianDurationSeconds)}
          hint={t("insights.avg", { value: formatSeconds(totals.avgDurationSeconds) })}
        />
        <Stat
          label={hasCost ? t("insights.aiCost") : t("insights.aiTokens")}
          value={hasCost ? formatCost(totals.totalCostUsd) : formatTokenCount(totals.totalTokens)}
          hint={
            hasCost
              ? t("insights.tokensHint", { n: formatTokenCount(totals.totalTokens) })
              : totals.avgTokensPerExecution != null
                ? t("insights.perRunHint", { n: formatTokenCount(totals.avgTokensPerExecution) })
                : t("insights.noTokenData")
          }
        />
      </div>

      <section className="mt-8">
        <SectionHead title={t("insights.caught")} />
        <p className="mb-3 max-w-3xl text-[12.5px] text-muted-foreground">
          {t("insights.caughtDesc")}
        </p>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-3">
          {insights.signals.map((signal) => (
            <SignalCard key={signal.key} signal={signal} />
          ))}
        </div>
      </section>

      <div className="mt-8 grid grid-cols-1 gap-6 lg:grid-cols-2">
        <section>
          <SectionHead title={t("insights.outcomes")} />
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
          <SectionHead title={t("insights.whyHuman")} count={insights.topFailureReasons.length} />
          <Panel className="divide-y divide-border">
            {insights.topFailureReasons.length === 0 ? (
              <div className="flex items-center gap-2 p-4 text-[12.5px] text-muted-foreground">
                <ShieldCheck className="h-4 w-4 text-success" />
                {t("insights.noneNeeded")}
              </div>
            ) : (
              insights.topFailureReasons.map((reason) => (
                <div key={reason.reason} className="flex items-start justify-between gap-3 p-3.5">
                  <span className="text-[12.5px] leading-relaxed text-foreground break-words">{srv(reason.reason)}</span>
                  <Badge tone="amber">{reason.count}×</Badge>
                </div>
              ))
            )}
          </Panel>
        </section>
      </div>

      {insights.retriedTasks.length > 0 && (
        <section className="mt-8">
          <SectionHead title={t("insights.retried")} count={insights.retriedTasks.length} />
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
                      {t("insights.attempts", { n: task.attempts })}
                      <span>·</span>
                      <Badge tone={first.tone}>{first.label}</Badge>
                      <ArrowRight className="h-3 w-3" />
                      <Badge tone={last.tone}>{last.label}</Badge>
                      {task.improved && (
                        <span className="flex items-center gap-1 text-success">
                          <TrendingUp className="h-3 w-3" /> {t("insights.improved")}
                        </span>
                      )}
                    </div>
                  </div>
                  <Link
                    to={`/executions/compare?a=${task.firstExecutionId}&b=${task.lastExecutionId}`}
                    className="flex items-center gap-1.5 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-1.5 text-[12px] font-medium text-muted-foreground hover:border-border-strong hover:text-foreground"
                  >
                    <GitCompareArrows className="h-3.5 w-3.5" />
                    {t("insights.compareFirstLatest")}
                  </Link>
                </div>
              )
            })}
          </Panel>
        </section>
      )}

      <section className="mt-8">
        <SectionHead title={t("insights.recent")} count={insights.recent.length} />
        <Panel className="overflow-x-auto">
          <table className="w-full min-w-[760px] text-left text-[12px]">
            <thead>
              <tr className="border-b border-border text-subtle-foreground">
                <th className="px-3.5 py-2.5 font-medium">{t("insights.th.task")}</th>
                <th className="px-3 py-2.5 font-medium">{t("insights.th.outcome")}</th>
                <th className="px-3 py-2.5 text-right font-medium">{t("insights.th.time")}</th>
                <th className="px-3 py-2.5 text-right font-medium">{t("insights.th.repairs")}</th>
                <th className="px-3 py-2.5 text-right font-medium">{t("insights.th.aiCalls")}</th>
                <th className="px-3 py-2.5 text-right font-medium">{t("insights.th.tokens")}</th>
                <th className="px-3.5 py-2.5 text-right font-medium">{t("insights.th.cost")}</th>
              </tr>
            </thead>
            <tbody>
              {insights.recent.map((row) => {
                const meta = getOutcomeMeta(row.outcome)
                return (
                  <tr key={row.executionId} className="border-b border-border/60 last:border-b-0 hover:bg-surface-3">
                    <td className="max-w-[340px] px-3.5 py-2.5">
                      <Link to={`/executions/${row.executionId}`} className="block truncate font-medium text-foreground hover:text-primary" title={srv(row.headline)}>
                        {row.taskTitle}
                      </Link>
                      {row.attemptNumber > 1 && (
                        <span className="font-mono text-[10.5px] text-subtle-foreground">{t("insights.attempt", { n: row.attemptNumber })}</span>
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
