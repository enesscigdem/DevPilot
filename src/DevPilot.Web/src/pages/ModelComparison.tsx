import { useCallback, useEffect, useRef, useState, type ReactNode } from "react"
import { Link, useParams } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { srv } from "@/i18n"
import { AlertCircle, ArrowLeft, Check, Clock, Cpu, Loader2 } from "lucide-react"
import { cancelModelComparison, getExecution, getExecutionReview, getModelComparison } from "@/api"
import { PageContainer, PageHeading } from "@/components/shared"
import { Badge, Button, Panel, type Tone } from "@/components/ui/primitives"
import { useWorkspace } from "@/lib/workspace"
import { cn } from "@/lib/utils"
import { formatCost, formatSeconds, formatTokenCount, getOutcomeMeta } from "@/lib/outcomes"
import { durationSeconds, flagList } from "@/lib/executionCompareMetrics"
import type { ExecutionDetail, ExecutionReview, ModelComparison, ModelComparisonRun, ModelComparisonRunState } from "@/types"

interface Side {
  execution: ExecutionDetail
  review: ExecutionReview | null
}

const POLL_MS = 4000

const stateTone: Record<ModelComparisonRunState, Tone> = {
  Queued: "gray",
  Running: "blue",
  Finished: "green",
  Skipped: "gray",
}

/** Index of the run(s) holding the lowest value, or an empty set when fewer than two runs have a value. */
function bestIndexes(values: (number | null | undefined)[]): Set<number> {
  const present = values.map((v, i) => [v, i] as const).filter((x): x is readonly [number, number] => x[0] != null)
  if (present.length < 2) return new Set()
  const min = Math.min(...present.map(([v]) => v))
  const winners = present.filter(([v]) => v === min)
  // A tie across every run says nothing, so highlight nothing.
  return winners.length === present.length ? new Set() : new Set(winners.map(([, i]) => i))
}

function NumberRow({
  label,
  runs,
  sides,
  pick,
  render,
}: {
  label: string
  runs: ModelComparisonRun[]
  sides: Record<string, Side>
  pick: (side: Side) => number | null | undefined
  render: (v: number | null | undefined) => ReactNode
}) {
  const values = runs.map((r) => (r.executionId && sides[r.executionId] ? pick(sides[r.executionId]) : null))
  const best = bestIndexes(values)
  return (
    <tr className="border-b border-border/60 last:border-b-0">
      <td className="px-4 py-2.5 text-[12px] text-subtle-foreground">{label}</td>
      {runs.map((r, i) => (
        <td key={r.id} className={cn("px-4 py-2.5 font-mono text-[12px]", best.has(i) ? "font-semibold text-success" : "text-foreground")}>
          {render(values[i])}
        </td>
      ))}
    </tr>
  )
}

function TextRow({ label, cells }: { label: string; cells: ReactNode[] }) {
  return (
    <tr className="border-b border-border/60 last:border-b-0">
      <td className="px-4 py-2.5 align-top text-[12px] text-subtle-foreground">{label}</td>
      {cells.map((cell, i) => (
        <td key={i} className="px-4 py-2.5 align-top text-[12px] text-foreground">
          {cell}
        </td>
      ))}
    </tr>
  )
}

export function ModelComparisonPage() {
  const { t } = useTranslation()
  const { id } = useParams<{ id: string }>()
  const { activeWorkspaceId } = useWorkspace()

  const [comparison, setComparison] = useState<ModelComparison | null>(null)
  const [sides, setSides] = useState<Record<string, Side>>({})
  const [error, setError] = useState<string | null>(null)
  const [stopping, setStopping] = useState(false)
  const requested = useRef<Set<string>>(new Set())

  const load = useCallback(async () => {
    if (!id) return
    try {
      setComparison(await getModelComparison(id))
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : t("modelCompare.errLoad"))
    }
  }, [id, t])

  useEffect(() => {
    void load()
  }, [load])

  // Keep refreshing while any run is still queued or running.
  const isRunning = comparison?.status === "Running"
  useEffect(() => {
    if (!isRunning) return
    const timer = setInterval(() => void load(), POLL_MS)
    return () => clearInterval(timer)
  }, [isRunning, load])

  // Fetch each finished run's details once; they no longer change.
  useEffect(() => {
    if (!comparison) return
    for (const run of comparison.runs) {
      const executionId = run.executionId
      if (run.state !== "Finished" || !executionId || requested.current.has(executionId)) continue
      requested.current.add(executionId)
      void Promise.all([
        getExecution(executionId, activeWorkspaceId),
        getExecutionReview(executionId, activeWorkspaceId).catch(() => null),
      ])
        .then(([execution, review]) => setSides((s) => ({ ...s, [executionId]: { execution, review } })))
        .catch(() => requested.current.delete(executionId))
    }
  }, [comparison, activeWorkspaceId])

  const stop = async () => {
    if (!id) return
    setStopping(true)
    try {
      setComparison(await cancelModelComparison(id))
    } catch (err) {
      setError(err instanceof Error ? err.message : t("modelCompare.errCancel"))
    } finally {
      setStopping(false)
    }
  }

  const runs = comparison?.runs ?? []
  const hasFinished = runs.some((r) => r.executionId && sides[r.executionId])

  const outcomeCell = (run: ModelComparisonRun): ReactNode => {
    const side = run.executionId ? sides[run.executionId] : undefined
    if (!side) return <span className="text-subtle-foreground">—</span>
    const meta = getOutcomeMeta(side.execution.verdict?.outcome ?? side.execution.verificationOutcome)
    return (
      <div className="space-y-1">
        <Badge tone={meta.tone}>{meta.label}</Badge>
        {side.execution.verdict ? (
          <div className="text-[11.5px] leading-snug text-muted-foreground">{srv(side.execution.verdict.headline)}</div>
        ) : (
          side.execution.errorMessage && (
            <div className="text-[11.5px] leading-snug text-danger">{t("modelCompare.failedWith", { message: side.execution.errorMessage })}</div>
          )
        )}
      </div>
    )
  }

  return (
    <PageContainer>
      <Link
        to={comparison ? `/tasks/${comparison.taskId}` : "/tasks"}
        className="mb-3 inline-flex items-center gap-1.5 text-[12px] text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft className="h-3.5 w-3.5" />
        {t("modelCompare.back")}
      </Link>

      <PageHeading
        eyebrow={t("modelCompare.eyebrow")}
        title={comparison?.taskTitle || t("modelCompare.title")}
        description={t("modelCompare.description")}
        actions={
          comparison && (
            <>
              <Badge tone={comparison.status === "Running" ? "blue" : comparison.status === "Completed" ? "green" : "gray"}>
                {comparison.status === "Running" && <Loader2 className="h-3 w-3 animate-spin" />}
                {t(`modelCompare.status.${comparison.status}`)}
              </Badge>
              {comparison.status === "Running" && runs.some((r) => r.state === "Queued") && (
                <Button variant="default" size="sm" onClick={() => void stop()} disabled={stopping}>
                  {stopping ? t("modelCompare.stopping") : t("modelCompare.stopRemaining")}
                </Button>
              )}
            </>
          )
        }
      />

      {error && (
        <Panel className="mb-4 flex items-center gap-2 p-3 text-[13px] text-danger">
          <AlertCircle className="h-4 w-4 shrink-0" />
          {error}
        </Panel>
      )}

      {!comparison && !error && (
        <div className="flex items-center gap-2 py-12 text-[13px] text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          {t("modelCompare.loading")}
        </div>
      )}

      {comparison && (
        <>
          <div className="mb-5 grid gap-3" style={{ gridTemplateColumns: `repeat(${Math.max(runs.length, 1)}, minmax(0, 1fr))` }}>
            {runs.map((run) => (
              <Panel key={run.id} className="p-4">
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="tech-label">{t("modelCompare.runLabel", { n: run.position + 1 })}</div>
                    <div className="mt-1 flex items-center gap-1.5">
                      <Cpu className="h-4 w-4 shrink-0 text-subtle-foreground" />
                      <span className="truncate text-[14px] font-semibold text-foreground">{run.modelName}</span>
                    </div>
                  </div>
                  <Badge tone={stateTone[run.state]}>
                    {run.state === "Running" && <Loader2 className="h-3 w-3 animate-spin" />}
                    {run.state === "Finished" && <Check className="h-3 w-3" />}
                    {run.state === "Queued" && <Clock className="h-3 w-3" />}
                    {t(`modelCompare.runState.${run.state}`)}
                  </Badge>
                </div>

                <div className="mt-3 text-[12px] text-muted-foreground">
                  {run.state === "Queued" && t("modelCompare.queuedHint")}
                  {run.state === "Skipped" && t("modelCompare.skippedHint")}
                  {run.executionId && (
                    <div className="flex gap-3">
                      <Link to={`/executions/${run.executionId}`} className="font-medium text-primary hover:underline">
                        {t("modelCompare.openExecution")}
                      </Link>
                      {run.state === "Finished" && (
                        <Link to={`/review/${run.executionId}`} className="font-medium text-primary hover:underline">
                          {t("modelCompare.openReview")}
                        </Link>
                      )}
                    </div>
                  )}
                </div>
              </Panel>
            ))}
          </div>

          {!hasFinished ? (
            <Panel className="p-8 text-center text-[13px] text-muted-foreground">
              {t("modelCompare.waitingForFirst")}
              <div className="mt-2 text-[12px] text-subtle-foreground">{t("modelCompare.sequentialNote")}</div>
            </Panel>
          ) : (
            <>
              <Panel className="overflow-x-auto">
                <table className="w-full min-w-[560px] text-left">
                  <thead>
                    <tr className="border-b border-border">
                      <th className="w-[200px] px-4 py-3" />
                      {runs.map((run) => (
                        <th key={run.id} className="px-4 py-3 text-[13px] font-semibold text-foreground">
                          {run.modelName}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    <TextRow label={t("compare.rows.outcome")} cells={runs.map(outcomeCell)} />
                    <NumberRow label={t("compare.rows.runTime")} runs={runs} sides={sides} pick={(s) => durationSeconds(s.execution)} render={(v) => formatSeconds(v)} />
                    <NumberRow label={t("compare.rows.compileRounds")} runs={runs} sides={sides} pick={(s) => s.execution.verdict?.compileRepairRounds} render={(v) => v ?? "—"} />
                    <NumberRow label={t("compare.rows.testRounds")} runs={runs} sides={sides} pick={(s) => s.execution.verdict?.testRepairRounds} render={(v) => v ?? "—"} />
                    <NumberRow label={t("compare.rows.aiCalls")} runs={runs} sides={sides} pick={(s) => s.execution.usage?.providerCalls} render={(v) => v ?? "—"} />
                    <NumberRow label={t("compare.rows.tokens")} runs={runs} sides={sides} pick={(s) => s.execution.usage?.totalTokens} render={(v) => formatTokenCount(v)} />
                    <NumberRow label={t("compare.rows.estCost")} runs={runs} sides={sides} pick={(s) => s.execution.usage?.estimatedCostUsd} render={(v) => formatCost(v)} />
                    <TextRow
                      label={t("compare.rows.filesChanged")}
                      cells={runs.map((r) => {
                        const side = r.executionId ? sides[r.executionId] : undefined
                        return side?.review?.changedFileCount ?? "—"
                      })}
                    />
                    <TextRow
                      label={t("compare.rows.signals")}
                      cells={runs.map((r) => (r.executionId && sides[r.executionId] ? flagList(sides[r.executionId].execution) : "—"))}
                    />
                  </tbody>
                </table>
              </Panel>
              <div className="mt-3 space-y-1 text-[12px] text-subtle-foreground">
                <p>{t("modelCompare.bestNote")}</p>
                <p>{t("modelCompare.winnerHint")}</p>
                {isRunning && <p>{t("modelCompare.sequentialNote")}</p>}
              </div>
            </>
          )}
        </>
      )}
    </PageContainer>
  )
}
