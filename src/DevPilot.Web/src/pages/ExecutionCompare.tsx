import { useEffect, useMemo, useState, type ReactNode } from "react"
import { Link, useSearchParams } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { fmt, srv } from "@/i18n"
import { AlertCircle, ArrowLeft, GitCompareArrows, Loader2 } from "lucide-react"
import { getExecution, getExecutionReview, getExecutions } from "@/api"
import { PageContainer, PageHeading } from "@/components/shared"
import { Badge, Panel } from "@/components/ui/primitives"
import { useWorkspace } from "@/lib/workspace"
import { cn } from "@/lib/utils"
import { formatCost, formatMs, formatSeconds, formatTokenCount, getOutcomeMeta } from "@/lib/outcomes"
import { durationSeconds, flagList, stageMs } from "@/lib/executionCompareMetrics"
import type { ExecutionDetail, ExecutionListItem, ExecutionReview } from "@/types"

interface Side {
  execution: ExecutionDetail
  review: ExecutionReview | null
}

type Better = "lower" | "none"

function Row({
  label,
  a,
  b,
  better = "none",
  render,
}: {
  label: string
  a: number | null | undefined
  b: number | null | undefined
  better?: Better
  render: (value: number | null | undefined) => ReactNode
}) {
  const bothNumbers = a != null && b != null && a !== b
  const aWins = better === "lower" && bothNumbers && a! < b!
  const bWins = better === "lower" && bothNumbers && b! < a!
  return (
    <tr className="border-b border-border/60 last:border-b-0">
      <td className="px-4 py-2.5 text-[12px] text-subtle-foreground">{label}</td>
      <td className={cn("px-4 py-2.5 font-mono text-[12px]", aWins ? "font-semibold text-success" : "text-foreground")}>{render(a)}</td>
      <td className={cn("px-4 py-2.5 font-mono text-[12px]", bWins ? "font-semibold text-success" : "text-foreground")}>{render(b)}</td>
    </tr>
  )
}

function TextRow({ label, a, b }: { label: string; a: ReactNode; b: ReactNode }) {
  return (
    <tr className="border-b border-border/60 last:border-b-0">
      <td className="px-4 py-2.5 align-top text-[12px] text-subtle-foreground">{label}</td>
      <td className="px-4 py-2.5 align-top text-[12px] text-foreground">{a}</td>
      <td className="px-4 py-2.5 align-top text-[12px] text-foreground">{b}</td>
    </tr>
  )
}

function OutcomeCell({ side }: { side: Side }) {
  const meta = getOutcomeMeta(side.execution.verdict?.outcome ?? side.execution.verificationOutcome)
  return (
    <div className="space-y-1">
      <Badge tone={meta.tone}>{meta.label}</Badge>
      {side.execution.verdict && <div className="text-[11.5px] leading-snug text-muted-foreground">{srv(side.execution.verdict.headline)}</div>}
    </div>
  )
}

export function ExecutionCompare() {
  const { t } = useTranslation()
  const [params, setParams] = useSearchParams()
  const { activeWorkspaceId } = useWorkspace()
  const aId = params.get("a")
  const bId = params.get("b")

  const [candidates, setCandidates] = useState<ExecutionListItem[]>([])
  const [sides, setSides] = useState<{ a: Side; b: Side } | null>(null)
  const [isLoading, setIsLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!aId || !bId) {
      setSides(null)
      return
    }

    const controller = new AbortController()
    setIsLoading(true)
    setError(null)

    const load = async (id: string): Promise<Side> => {
      const [execution, review] = await Promise.all([
        getExecution(id, activeWorkspaceId, { signal: controller.signal }),
        getExecutionReview(id, activeWorkspaceId, { signal: controller.signal }).catch(() => null),
      ])
      return { execution, review }
    }

    Promise.all([load(aId), load(bId)])
      .then(([a, b]) => setSides({ a, b }))
      .catch((err) => {
        if (controller.signal.aborted) return
        setError(err instanceof Error ? err.message : t("compare.errLoad"))
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsLoading(false)
      })

    return () => controller.abort()
  }, [aId, bId, activeWorkspaceId])

  useEffect(() => {
    const controller = new AbortController()
    getExecutions(activeWorkspaceId, { signal: controller.signal })
      .then(setCandidates)
      .catch(() => undefined)
    return () => controller.abort()
  }, [activeWorkspaceId])

  const taskId = sides?.a.execution.developmentTaskId ?? candidates.find((c) => c.id === aId)?.developmentTaskId
  const options = useMemo(() => {
    const finished = candidates.filter((c) => !taskId || c.developmentTaskId === taskId)
    return finished.sort((x, y) => new Date(x.createdAt).getTime() - new Date(y.createdAt).getTime())
  }, [candidates, taskId])

  const optionLabel = (e: ExecutionListItem, index: number) =>
    `${taskId ? t("compare.attemptN", { n: index + 1 }) : e.taskTitle} · ${fmt.dateTime(e.createdAt)} · ${e.id.slice(0, 8)}`

  const select = (key: "a" | "b", value: string) => {
    const next = new URLSearchParams(params)
    next.set(key, value)
    setParams(next)
  }

  const filePaths = (side: Side) => new Set((side.review?.changedFiles ?? []).map((f) => f.path))
  const aFiles = sides ? filePaths(sides.a) : new Set<string>()
  const bFiles = sides ? filePaths(sides.b) : new Set<string>()
  const onlyA = [...aFiles].filter((p) => !bFiles.has(p))
  const onlyB = [...bFiles].filter((p) => !aFiles.has(p))
  const both = [...aFiles].filter((p) => bFiles.has(p))

  return (
    <PageContainer>
      <Link to="/executions" className="mb-3 inline-flex items-center gap-1.5 text-[12px] text-muted-foreground hover:text-foreground">
        <ArrowLeft className="h-3.5 w-3.5" />
        {t("compare.back")}
      </Link>
      <PageHeading
        eyebrow={t("compare.eyebrow")}
        title={t("compare.title")}
        description={t("compare.description")}
      />

      <Panel className="mb-5 grid grid-cols-1 gap-3 p-4 md:grid-cols-2">
        {(["a", "b"] as const).map((key) => (
          <label key={key} className="block min-w-0">
            <span className="tech-label">{key === "a" ? t("compare.baselineRun") : t("compare.comparedRun")}</span>
            <select
              value={(key === "a" ? aId : bId) ?? ""}
              onChange={(e) => select(key, e.target.value)}
              className="mt-1.5 h-9 w-full rounded-[var(--radius-md)] border border-border bg-surface px-2 text-[12.5px] text-foreground outline-none focus:border-primary"
            >
              <option value="" disabled>
                {t("compare.selectPlaceholder")}
              </option>
              {options.map((e, i) => (
                <option key={e.id} value={e.id}>
                  {optionLabel(e, i)}
                </option>
              ))}
            </select>
          </label>
        ))}
      </Panel>

      {!aId || !bId ? (
        <Panel className="flex flex-col items-center gap-2 p-10 text-center">
          <GitCompareArrows className="h-7 w-7 text-subtle-foreground" />
          <p className="text-[13px] text-muted-foreground">{t("compare.choose")}</p>
        </Panel>
      ) : isLoading ? (
        <div className="flex items-center justify-center gap-2 py-16 text-subtle-foreground">
          <Loader2 className="h-5 w-5 animate-spin text-primary" />
          <span className="tech-label">{t("compare.loading")}</span>
        </div>
      ) : error ? (
        <Panel className="flex items-center gap-2 p-4 text-[13px] text-danger">
          <AlertCircle className="h-4 w-4 shrink-0" />
          {error}
        </Panel>
      ) : sides ? (
        <>
          {sides.a.execution.developmentTaskId !== sides.b.execution.developmentTaskId && (
            <Panel className="mb-4 flex items-center gap-2 border-amber-500/30 bg-amber-500/10 p-3 text-[12px] text-amber-600 dark:text-amber-400">
              <AlertCircle className="h-4 w-4 shrink-0" />
              {t("compare.differentTasks")}
            </Panel>
          )}
          <Panel className="overflow-x-auto">
            <table className="w-full min-w-[640px] text-left">
              <thead>
                <tr className="border-b border-border">
                  <th className="w-[200px] px-4 py-3" />
                  {[sides.a, sides.b].map((side, i) => (
                    <th key={i} className="px-4 py-3 align-top">
                      <Link to={`/executions/${side.execution.id}`} className="text-[13px] font-semibold text-foreground hover:text-primary">
                        {i === 0 ? t("compare.baseline") : t("compare.compared")}: {side.execution.taskTitle}
                      </Link>
                      <div className="mt-0.5 font-mono text-[10.5px] font-normal text-subtle-foreground">
                        {side.execution.id.slice(0, 8)} · {fmt.dateTime(side.execution.createdAt)}
                      </div>
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                <TextRow label={t("compare.rows.outcome")} a={<OutcomeCell side={sides.a} />} b={<OutcomeCell side={sides.b} />} />
                <Row label={t("compare.rows.runTime")} a={durationSeconds(sides.a.execution)} b={durationSeconds(sides.b.execution)} better="lower" render={(v) => formatSeconds(v)} />
                <Row label={t("compare.rows.compileRounds")} a={sides.a.execution.verdict?.compileRepairRounds} b={sides.b.execution.verdict?.compileRepairRounds} better="lower" render={(v) => v ?? "—"} />
                <Row label={t("compare.rows.testRounds")} a={sides.a.execution.verdict?.testRepairRounds} b={sides.b.execution.verdict?.testRepairRounds} better="lower" render={(v) => v ?? "—"} />
                <Row label={t("compare.rows.applicability")} a={sides.a.execution.verdict?.applicabilityRepairs} b={sides.b.execution.verdict?.applicabilityRepairs} better="lower" render={(v) => v ?? "—"} />
                <Row label={t("compare.rows.compact")} a={sides.a.execution.verdict?.compactRetries} b={sides.b.execution.verdict?.compactRetries} better="lower" render={(v) => v ?? "—"} />
                <Row label={t("compare.rows.aiCalls")} a={sides.a.execution.usage?.providerCalls} b={sides.b.execution.usage?.providerCalls} better="lower" render={(v) => v ?? "—"} />
                <Row label={t("compare.rows.tokens")} a={sides.a.execution.usage?.totalTokens} b={sides.b.execution.usage?.totalTokens} better="lower" render={(v) => formatTokenCount(v)} />
                <Row label={t("compare.rows.estCost")} a={sides.a.execution.usage?.estimatedCostUsd} b={sides.b.execution.usage?.estimatedCostUsd} better="lower" render={(v) => formatCost(v)} />
                <Row label={t("compare.rows.genTime")} a={stageMs(sides.a.execution, "Generation")} b={stageMs(sides.b.execution, "Generation")} better="lower" render={(v) => formatMs(v)} />
                <Row label={t("compare.rows.buildTime")} a={stageMs(sides.a.execution, "Build")} b={stageMs(sides.b.execution, "Build")} better="lower" render={(v) => formatMs(v)} />
                <Row label={t("compare.rows.testTime")} a={stageMs(sides.a.execution, "Test")} b={stageMs(sides.b.execution, "Test")} better="lower" render={(v) => formatMs(v)} />
                <Row label={t("compare.rows.repairTime")} a={stageMs(sides.a.execution, "Repair")} b={stageMs(sides.b.execution, "Repair")} better="lower" render={(v) => formatMs(v)} />
                <Row label={t("compare.rows.filesChanged")} a={sides.a.review?.changedFileCount} b={sides.b.review?.changedFileCount} render={(v) => v ?? "—"} />
                <TextRow label={t("compare.rows.signals")} a={flagList(sides.a.execution)} b={flagList(sides.b.execution)} />
                <TextRow
                  label={t("compare.rows.checksNotRun")}
                  a={sides.a.execution.verdict?.checksNotRun.join(", ") || "—"}
                  b={sides.b.execution.verdict?.checksNotRun.join(", ") || "—"}
                />
              </tbody>
            </table>
          </Panel>

          {(sides.a.review || sides.b.review) && (
            <Panel className="mt-5 grid grid-cols-1 gap-4 p-4 md:grid-cols-3">
              {[
                { title: t("compare.onlyBaseline"), files: onlyA },
                { title: t("compare.inBoth"), files: both },
                { title: t("compare.onlyCompared"), files: onlyB },
              ].map((group) => (
                <div key={group.title} className="min-w-0">
                  <div className="tech-label mb-2">
                    {group.title} ({group.files.length})
                  </div>
                  {group.files.length === 0 ? (
                    <div className="text-[12px] text-subtle-foreground">—</div>
                  ) : (
                    <ul className="space-y-1">
                      {group.files.map((path) => (
                        <li key={path} className="truncate font-mono text-[11.5px] text-muted-foreground" title={path}>
                          {path}
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              ))}
            </Panel>
          )}
        </>
      ) : null}
    </PageContainer>
  )
}
