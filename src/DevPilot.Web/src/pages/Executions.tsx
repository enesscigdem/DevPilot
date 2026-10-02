import { useCallback, useEffect, useRef, useState } from "react"
import { Link } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { fmt } from "@/i18n"
import { Activity, Clock, Coins, Cpu, ArrowUpRight, Search, Loader2, AlertCircle, Plus } from "lucide-react"
import { PageContainer, PageHeading } from "@/components/shared"
import { Panel, Badge, StatusDot, Meter, Button } from "@/components/ui/primitives"
import { getExecutions } from "@/api"
import { useWorkspace } from "@/lib/workspace"
import {
  TaskExecutionStatus,
  getExecutionStatusMeta,
  type ExecutionListItem,
  type Tone,
} from "@/types"
import { stages as defaultStages } from "@/lib/executionStages"

type FilterKey = "all" | "pending" | "running" | "completed" | "failed" | "cancelled"

const filterTabs: { key: FilterKey; label: string; tone: Tone }[] = [
  { key: "all", label: "executions.filters.all", tone: "neutral" },
  { key: "running", label: "executions.filters.running", tone: "blue" },
  { key: "pending", label: "executions.filters.pending", tone: "amber" },
  { key: "completed", label: "executions.filters.completed", tone: "green" },
  { key: "failed", label: "executions.filters.failed", tone: "red" },
  { key: "cancelled", label: "executions.filters.cancelled", tone: "gray" },
]

function matchesFilter(item: ExecutionListItem, filter: FilterKey): boolean {
  switch (filter) {
    case "all":
      return true
    case "running":
      return item.status === TaskExecutionStatus.Running
    case "pending":
      return item.status === TaskExecutionStatus.Pending
    case "completed":
      return item.status === TaskExecutionStatus.Completed
    case "failed":
      return item.status === TaskExecutionStatus.Failed
    case "cancelled":
      return item.status === TaskExecutionStatus.Cancelled
    default:
      return true
  }
}

function getProgressPercentage(status: number): number {
  switch (status) {
    case TaskExecutionStatus.Pending:
      return 0
    case TaskExecutionStatus.Running:
      return 50
    case TaskExecutionStatus.Completed:
      return 100
    case TaskExecutionStatus.Failed:
      return 71
    case TaskExecutionStatus.Cancelled:
      return 43
    default:
      return 0
  }
}

function formatDate(dateStr: string): string {
  try {
    return fmt.time(dateStr, { hour: "2-digit", minute: "2-digit" }) + " · " + fmt.date(dateStr)
  } catch {
    return dateStr
  }
}

export function Executions() {
  const { t } = useTranslation()
  const { activeWorkspaceId, isLoading: isWorkspaceLoading } = useWorkspace()
  const activeReqWorkspaceIdRef = useRef<string | null>(activeWorkspaceId)

  useEffect(() => {
    activeReqWorkspaceIdRef.current = activeWorkspaceId
  }, [activeWorkspaceId])

  const [executions, setExecutions] = useState<ExecutionListItem[]>([])
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [activeFilter, setActiveFilter] = useState<FilterKey>("all")
  const [searchQuery, setSearchQuery] = useState("")

  const activeRequestIdRef = useRef(0)

  const fetchExecutions = useCallback(async (signal?: AbortSignal) => {
    if (isWorkspaceLoading) return
    const currentRequestId = ++activeRequestIdRef.current

    setIsLoading(true)
    setError(null)
    try {
      const data = await getExecutions(activeWorkspaceId, { signal })
      if (currentRequestId === activeRequestIdRef.current && activeReqWorkspaceIdRef.current === activeWorkspaceId) {
        setExecutions(data)
        setError(null)
      }
    } catch (err) {
      if (signal?.aborted) return
      if (currentRequestId === activeRequestIdRef.current && activeReqWorkspaceIdRef.current === activeWorkspaceId) {
        setError(err instanceof Error ? err.message : t("executions.errLoad"))
      }
    } finally {
      if (currentRequestId === activeRequestIdRef.current && activeReqWorkspaceIdRef.current === activeWorkspaceId) {
        setIsLoading(false)
      }
    }
  }, [isWorkspaceLoading, activeWorkspaceId])

  useEffect(() => {
    if (isWorkspaceLoading) {
      setIsLoading(true)
      return
    }

    setExecutions([])
    const controller = new AbortController()
    fetchExecutions(controller.signal)
    return () => controller.abort()
  }, [isWorkspaceLoading, activeWorkspaceId, fetchExecutions])

  const runningCount = executions.filter((e) => e.status === TaskExecutionStatus.Running).length

  const filteredExecutions = executions.filter((e) => {
    const filterMatch = matchesFilter(e, activeFilter)
    const searchMatch =
      !searchQuery.trim() ||
      e.taskTitle.toLowerCase().includes(searchQuery.toLowerCase()) ||
      e.id.toLowerCase().includes(searchQuery.toLowerCase()) ||
      e.repositoryName.toLowerCase().includes(searchQuery.toLowerCase())
    return filterMatch && searchMatch
  })

  return (
    <PageContainer>
      <PageHeading
        eyebrow={t("executions.eyebrow")}
        title={t("executions.title")}
        description={t("executions.description")}
      />

      {/* Live summary strip */}
      <div className="mb-6 grid grid-cols-2 gap-3 md:grid-cols-4">
        {[
          { icon: Activity, label: t("executions.running"), value: String(runningCount), tone: "blue" as const },
          { icon: Clock, label: t("executions.totalRuns"), value: String(executions.length), tone: "neutral" as const },
          { icon: Cpu, label: t("executions.model"), value: executions.find((e) => e.model)?.model || t("executions.notRecorded"), tone: "neutral" as const, mono: true },
          { icon: Coins, label: t("executions.spendToday"), value: "—", tone: "neutral" as const },
        ].map((m) => (
          <Panel key={m.label} className="p-3.5">
            <div className="flex items-center gap-1.5">
              <m.icon className="h-3.5 w-3.5 text-subtle-foreground" />
              <span className="tech-label">{m.label}</span>
            </div>
            <div className={"mt-1.5 text-[18px] font-semibold text-foreground " + (m.mono ? "font-mono text-[14px]" : "")}>
              {m.value}
            </div>
          </Panel>
        ))}
      </div>

      {/* Filter and Search controls */}
      <div className="mb-3 flex flex-wrap items-center gap-1.5">
        {filterTabs.map((f) => {
          const count = executions.filter((e) => matchesFilter(e, f.key)).length
          const isActive = activeFilter === f.key
          return (
            <button
              key={f.key}
              onClick={() => setActiveFilter(f.key)}
              className={
                "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[12px] font-medium transition-colors " +
                (isActive
                  ? "border-primary-ring/60 bg-primary-soft text-primary"
                  : "border-border bg-surface text-muted-foreground hover:bg-surface-2 hover:text-foreground")
              }
            >
              {f.key !== "all" && (
                <span
                  className="h-1.5 w-1.5 rounded-full"
                  style={{ background: `var(--dot-${f.tone})` }}
                />
              )}
              {t(f.label)}
              <span className="font-mono text-[11px] opacity-60">{count}</span>
            </button>
          )
        })}
        <div className="ml-auto flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-1.5">
          <Search className="h-3.5 w-3.5 text-subtle-foreground" />
          <input
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            placeholder={t("executions.filterPlaceholder")}
            className="w-36 bg-transparent text-[12.5px] text-foreground outline-none placeholder:text-subtle-foreground"
          />
        </div>
      </div>

      {isWorkspaceLoading || isLoading ? (
        <Panel className="flex flex-col items-center justify-center gap-2 px-4 py-16 text-center">
          <Loader2 className="h-5 w-5 animate-spin text-subtle-foreground" />
          <p className="text-[13px] text-muted-foreground">{t("executions.loading")}</p>
        </Panel>
      ) : error ? (
        <Panel className="flex flex-col items-center justify-center gap-3 px-4 py-12 text-center">
          <AlertCircle className="h-6 w-6 text-danger" />
          <div>
            <p className="text-[13.5px] font-medium text-foreground">{t("executions.failedLoad")}</p>
            <p className="mt-0.5 text-[12.5px] text-muted-foreground">{error}</p>
          </div>
          <Button variant="default" size="sm" onClick={() => fetchExecutions()}>
            {t("common.retry")}
          </Button>
        </Panel>
      ) : filteredExecutions.length === 0 ? (
        <Panel className="flex flex-col items-center gap-2 px-4 py-12 text-center">
          <Plus className="h-5 w-5 text-subtle-foreground" />
          <p className="text-[13px] text-muted-foreground">
            {searchQuery.trim() || activeFilter !== "all"
              ? t("executions.noMatch")
              : t("executions.none")}
          </p>
        </Panel>
      ) : (
        <div className="space-y-3">
          {filteredExecutions.map((run) => {
            const meta = getExecutionStatusMeta(run.status)
            const progress = typeof run.progressPercentage === "number"
              ? run.progressPercentage
              : getProgressPercentage(run.status)
            const isLive = run.status === TaskExecutionStatus.Running

            const itemStages = run.stages && run.stages.length === 7
              ? run.stages
              : defaultStages.map((st, i) => ({
                  stageKey: st.key,
                  label: st.label,
                  state: (run.status === TaskExecutionStatus.Completed
                    ? "Done"
                    : run.status === TaskExecutionStatus.Failed
                      ? (i === 4 ? "Failed" : (i < 4 ? "Done" : "Todo"))
                      : run.status === TaskExecutionStatus.Running
                        ? (i === 3 ? "Active" : (i < 3 ? "Done" : "Todo"))
                        : "Todo") as any,
                }))

            return (
              <Link key={run.id} to={`/executions/${run.id}`}>
                <Panel className="group p-4 transition-colors hover:border-border-strong hover:bg-surface-2">
                  <div className="flex items-center gap-3">
                    <StatusDot tone={meta.tone} pulse={isLive} />
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2">
                        <span className="font-mono text-[11px] text-subtle-foreground">{run.id}</span>
                        <span className="truncate text-[13.5px] font-medium text-foreground">{run.taskTitle}</span>
                        {isLive && <Badge tone="blue">{t("executions.live")}</Badge>}
                      </div>
                      <div className="mt-0.5 font-mono text-[11px] text-subtle-foreground">
                        {run.repositoryName} · {meta.label}
                      </div>
                    </div>
                    <div className="hidden items-center gap-1.5 font-mono text-[11px] text-muted-foreground sm:flex">
                      <Clock className="h-3 w-3" />
                      {formatDate(run.createdAt)}
                    </div>
                    <ArrowUpRight className="h-4 w-4 text-subtle-foreground opacity-0 transition-opacity group-hover:opacity-100" />
                  </div>
                  <div className="mt-3 flex items-center gap-3">
                    <Meter value={progress} tone={meta.tone} className="flex-1" />
                    <span className="font-mono text-[10.5px] text-subtle-foreground">{progress}%</span>
                  </div>
                  {/* stage rail */}
                  <div className="mt-3 flex items-center gap-1">
                    {itemStages.map((st) => {
                      const state = String(st.state).toLowerCase()
                      const bgClass =
                        state === "done"
                          ? "bg-success"
                          : state === "failed"
                            ? "bg-danger"
                            : state === "active"
                              ? "bg-primary"
                              : state === "blocked"
                                ? "bg-accent"
                                : "bg-surface-3"
                      return (
                        <div key={st.stageKey || st.label} className="flex flex-1 items-center gap-1">
                          <div className={"h-1 flex-1 rounded-full " + bgClass} />
                        </div>
                      )
                    })}
                  </div>
                </Panel>
              </Link>
            )
          })}
        </div>
      )}
    </PageContainer>
  )
}
