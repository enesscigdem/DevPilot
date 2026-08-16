import { useCallback, useEffect, useState } from "react"
import { Link } from "react-router-dom"
import { Activity, Clock, Coins, Cpu, ArrowUpRight, Search, Loader2, AlertCircle, Plus } from "lucide-react"
import { PageContainer, PageHeading } from "@/components/shared"
import { Panel, Badge, StatusDot, Meter, Button } from "@/components/ui/primitives"
import { getExecutions } from "@/api"
import {
  TaskExecutionStatus,
  getExecutionStatusMeta,
  type ExecutionListItem,
  type Tone,
} from "@/types"
import { stages } from "@/data/mock"

type FilterKey = "all" | "pending" | "running" | "completed" | "failed" | "cancelled"

const filterTabs: { key: FilterKey; label: string; tone: Tone }[] = [
  { key: "all", label: "All", tone: "neutral" },
  { key: "running", label: "Running", tone: "blue" },
  { key: "pending", label: "Pending", tone: "amber" },
  { key: "completed", label: "Completed", tone: "green" },
  { key: "failed", label: "Failed", tone: "red" },
  { key: "cancelled", label: "Cancelled", tone: "gray" },
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
      return 100
    case TaskExecutionStatus.Cancelled:
      return 0
    default:
      return 0
  }
}

function formatDate(dateStr: string): string {
  try {
    const d = new Date(dateStr)
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) + ' · ' + d.toLocaleDateString()
  } catch {
    return dateStr
  }
}

export function Executions() {
  const [executions, setExecutions] = useState<ExecutionListItem[]>([])
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [activeFilter, setActiveFilter] = useState<FilterKey>("all")
  const [searchQuery, setSearchQuery] = useState("")

  const fetchExecutions = useCallback(async () => {
    setIsLoading(true)
    setError(null)
    try {
      const data = await getExecutions()
      setExecutions(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load executions.")
    } finally {
      setIsLoading(false)
    }
  }, [])

  useEffect(() => {
    fetchExecutions()
  }, [fetchExecutions])

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
        eyebrow="Executions"
        title="Execution runs"
        description="Autonomous runs of approved plans. Each run streams the agents' activity, build and test results, and stops for review before opening a pull request."
      />

      {/* Live summary strip */}
      <div className="mb-6 grid grid-cols-2 gap-3 md:grid-cols-4">
        {[
          { icon: Activity, label: "Running", value: String(runningCount), tone: "blue" as const },
          { icon: Clock, label: "Total runs", value: String(executions.length), tone: "neutral" as const },
          { icon: Cpu, label: "Model", value: "Not assigned", tone: "neutral" as const, mono: true },
          { icon: Coins, label: "Spend today", value: "—", tone: "neutral" as const },
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
              {f.label}
              <span className="font-mono text-[11px] opacity-60">{count}</span>
            </button>
          )
        })}
        <div className="ml-auto flex items-center gap-2 rounded-[var(--radius-md)] border border-border bg-surface px-2.5 py-1.5">
          <Search className="h-3.5 w-3.5 text-subtle-foreground" />
          <input
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            placeholder="Filter executions"
            className="w-36 bg-transparent text-[12.5px] text-foreground outline-none placeholder:text-subtle-foreground"
          />
        </div>
      </div>

      {isLoading ? (
        <Panel className="flex flex-col items-center justify-center gap-2 px-4 py-16 text-center">
          <Loader2 className="h-5 w-5 animate-spin text-subtle-foreground" />
          <p className="text-[13px] text-muted-foreground">Loading executions from API…</p>
        </Panel>
      ) : error ? (
        <Panel className="flex flex-col items-center justify-center gap-3 px-4 py-12 text-center">
          <AlertCircle className="h-6 w-6 text-danger" />
          <div>
            <p className="text-[13.5px] font-medium text-foreground">Failed to load executions</p>
            <p className="mt-0.5 text-[12.5px] text-muted-foreground">{error}</p>
          </div>
          <Button variant="default" size="sm" onClick={fetchExecutions}>
            Retry
          </Button>
        </Panel>
      ) : filteredExecutions.length === 0 ? (
        <Panel className="flex flex-col items-center gap-2 px-4 py-12 text-center">
          <Plus className="h-5 w-5 text-subtle-foreground" />
          <p className="text-[13px] text-muted-foreground">
            {searchQuery.trim() || activeFilter !== "all"
              ? "No executions match the selected filter."
              : "No executions found."}
          </p>
        </Panel>
      ) : (
        <div className="space-y-3">
          {filteredExecutions.map((run) => {
            const meta = getExecutionStatusMeta(run.status)
            const progress = getProgressPercentage(run.status)
            const isLive = run.status === TaskExecutionStatus.Running

            return (
              <Link key={run.id} to={`/executions/${run.id}`}>
                <Panel className="group p-4 transition-colors hover:border-border-strong hover:bg-surface-2">
                  <div className="flex items-center gap-3">
                    <StatusDot tone={meta.tone} pulse={isLive} />
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2">
                        <span className="font-mono text-[11px] text-subtle-foreground">{run.id}</span>
                        <span className="truncate text-[13.5px] font-medium text-foreground">{run.taskTitle}</span>
                        {isLive && <Badge tone="blue">live</Badge>}
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
                    {stages.map((st, i) => {
                      const reached = run.status === TaskExecutionStatus.Completed
                        ? true
                        : run.status === TaskExecutionStatus.Failed
                          ? i <= 4
                          : run.status === TaskExecutionStatus.Running
                            ? i < 3
                            : false
                      return (
                        <div key={st.key} className="flex flex-1 items-center gap-1">
                          <div
                            className={
                              "h-1 flex-1 rounded-full " +
                              (reached
                                ? meta.tone === "red"
                                  ? "bg-danger"
                                  : meta.tone === "amber"
                                    ? "bg-accent"
                                    : meta.tone === "green"
                                      ? "bg-success"
                                      : "bg-primary"
                                : "bg-surface-3")
                            }
                          />
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
