import { useCallback, useEffect, useState } from "react"
import { Link, useNavigate, useParams } from "react-router-dom"
import {
  ArrowLeft,
  Check,
  CircleDot,
  Clock,
  Eye,
  FileText,
  GitBranch,
  Hammer,
  FlaskConical,
  CircleCheck,
  Terminal,
  X,
  OctagonAlert,
  Loader2,
  AlertCircle,
} from "lucide-react"
import { Button, Badge, Panel, StatusDot } from "@/components/ui/primitives"
import { cn } from "@/lib/utils"
import { getExecution } from "@/api"
import {
  TaskExecutionStatus,
  getExecutionStatusMeta,
  type ExecutionDetail,
} from "@/types"
import { stages } from "@/data/mock"

function getStageState(stageIndex: number, status: number): "done" | "active" | "todo" | "failed" | "blocked" {
  if (status === TaskExecutionStatus.Completed) {
    return "done"
  }
  if (status === TaskExecutionStatus.Failed) {
    if (stageIndex < 4) return "done"
    if (stageIndex === 4) return "failed"
    return "todo"
  }
  if (status === TaskExecutionStatus.Cancelled) {
    if (stageIndex < 3) return "done"
    if (stageIndex === 3) return "blocked"
    return "todo"
  }
  if (status === TaskExecutionStatus.Running) {
    if (stageIndex < 3) return "done"
    if (stageIndex === 3) return "active"
    return "todo"
  }
  // Pending (0) or default: execution has not started, all stages todo/pending
  return "todo"
}

function formatDateTime(dateStr: string | null): string {
  if (!dateStr) return "Not available"
  try {
    const d = new Date(dateStr)
    return d.toLocaleString()
  } catch {
    return dateStr
  }
}

export function ExecutionWorkspace() {
  const navigate = useNavigate()
  const { id } = useParams<{ id: string }>()

  const [execution, setExecution] = useState<ExecutionDetail | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const fetchExecution = useCallback(async () => {
    if (!id) return
    setIsLoading(true)
    setError(null)
    try {
      const data = await getExecution(id)
      setExecution(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load execution detail.")
    } finally {
      setIsLoading(false)
    }
  }, [id])

  useEffect(() => {
    fetchExecution()
  }, [fetchExecution])

  if (isLoading) {
    return (
      <div className="flex h-[calc(100vh-100px)] w-full items-center justify-center">
        <div className="flex flex-col items-center gap-3 text-center">
          <Loader2 className="h-6 w-6 animate-spin text-subtle-foreground" />
          <p className="text-[13.5px] font-medium text-foreground">Loading execution details…</p>
        </div>
      </div>
    )
  }

  if (error || !execution) {
    return (
      <div className="mx-auto max-w-[800px] px-6 py-16">
        <Panel className="flex flex-col items-center justify-center gap-3 p-8 text-center">
          <AlertCircle className="h-8 w-8 text-danger" />
          <div>
            <h2 className="text-[16px] font-semibold text-foreground">Execution Not Found</h2>
            <p className="mt-1 text-[13px] text-muted-foreground">
              {error || `Execution run "${id}" could not be retrieved from the server.`}
            </p>
          </div>
          <div className="mt-2 flex items-center gap-3">
            <Button variant="default" size="sm" onClick={fetchExecution}>
              Retry
            </Button>
            <Link to="/executions">
              <Button variant="primary" size="sm">
                <ArrowLeft className="h-3.5 w-3.5" />
                Back to Executions
              </Button>
            </Link>
          </div>
        </Panel>
      </div>
    )
  }

  const statusMeta = getExecutionStatusMeta(execution.status)
  const isRunning = execution.status === TaskExecutionStatus.Running
  const isFailed = execution.status === TaskExecutionStatus.Failed
  const isCompleted = execution.status === TaskExecutionStatus.Completed
  const isPending = execution.status === TaskExecutionStatus.Pending
  const isCancelled = execution.status === TaskExecutionStatus.Cancelled

  return (
    <div className="w-full">
      {/* Header */}
      <div className="sticky top-0 z-10 border-b border-border bg-canvas/85 px-6 py-3 backdrop-blur-sm">
        <div className="mx-auto flex max-w-[1500px] items-center gap-3">
          <Link
            to="/executions"
            className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-md)] text-muted-foreground hover:bg-surface-3 hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
          </Link>
          <StatusDot tone={statusMeta.tone} pulse={isRunning} />
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="font-mono text-[11px] text-subtle-foreground">{execution.id}</span>
              <h1 className="truncate text-[14.5px] font-semibold text-foreground">{execution.taskTitle}</h1>
              <Badge tone={statusMeta.tone}>{statusMeta.label}</Badge>
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
              <GitBranch className="h-3 w-3" />
              {execution.repositoryOwner}/{execution.repositoryName}
            </div>
          </div>
          <Button
            variant="primary"
            size="sm"
            disabled={!isCompleted}
            onClick={() => navigate(`/tasks/${execution.developmentTaskId}`)}
          >
            <Eye className="h-3.5 w-3.5" />
            View task details
          </Button>
        </div>
      </div>

      <div className="mx-auto grid max-w-[1500px] grid-cols-1 gap-0 lg:grid-cols-[240px_minmax(0,1fr)_320px]">
        {/* LEFT — stage rail */}
        <aside className="border-b border-border p-5 lg:border-b-0 lg:border-r">
          <div className="tech-label mb-3">Pipeline</div>
          <ol className="relative">
            {stages.map((st, i) => {
              const state = getStageState(i, execution.status)

              return (
                <li key={st.key} className="relative flex gap-3 pb-5 last:pb-0">
                  {i < stages.length - 1 && (
                    <span
                      className={cn(
                        "absolute left-[9px] top-5 h-full w-px",
                        state === "done" ? "bg-success/50" : "bg-border",
                      )}
                    />
                  )}
                  <span
                    className={cn(
                      "relative z-10 flex h-[18px] w-[18px] shrink-0 items-center justify-center rounded-full border",
                      state === "done"
                        ? "border-success bg-success text-primary-foreground"
                        : state === "active"
                          ? "border-primary bg-surface"
                          : state === "failed"
                            ? "border-danger bg-danger text-primary-foreground"
                            : state === "blocked"
                              ? "border-accent bg-accent text-primary-foreground"
                              : "border-border bg-surface",
                    )}
                  >
                    {state === "done" ? (
                      <Check className="h-2.5 w-2.5" />
                    ) : state === "active" ? (
                      <CircleDot className="h-3 w-3 animate-pulse-dot text-primary" />
                    ) : state === "failed" ? (
                      <X className="h-2.5 w-2.5" />
                    ) : state === "blocked" ? (
                      <X className="h-2 w-2" />
                    ) : (
                      <span className="h-1.5 w-1.5 rounded-full bg-subtle-foreground" />
                    )}
                  </span>
                  <div className="pt-px">
                    <div
                      className={cn(
                        "text-[12.5px] font-medium",
                        state === "todo" ? "text-subtle-foreground" : "text-foreground",
                      )}
                    >
                      {st.label}
                    </div>
                    {state === "active" && <span className="font-mono text-[10.5px] text-primary">in progress</span>}
                    {state === "failed" && <span className="font-mono text-[10.5px] text-danger">failed here</span>}
                    {state === "blocked" && <span className="font-mono text-[10.5px] text-accent">cancelled</span>}
                  </div>
                </li>
              )
            })}
          </ol>

          <div className="tech-label mb-2 mt-6">Agents</div>
          <div className="rounded-[var(--radius-md)] border border-border bg-surface p-3 text-[11px] text-subtle-foreground">
            No agent instances running for this execution.
          </div>
        </aside>

        {/* CENTER — activity stream */}
        <section className="flex min-h-[calc(100vh-113px)] flex-col border-b border-border lg:border-b-0">
          <div className="flex items-center justify-between border-b border-border px-5 py-3">
            <div className="flex items-center gap-2">
              <Terminal className="h-3.5 w-3.5 text-subtle-foreground" />
              <span className="text-[13px] font-semibold text-foreground">Execution activity</span>
            </div>
            <span className="font-mono text-[11px] text-subtle-foreground">
              0 events
            </span>
          </div>

          <div className="flex-1 overflow-y-auto px-5 py-6">
            {isPending && (
              <div className="flex flex-col items-center justify-center gap-2 py-16 text-center text-subtle-foreground">
                <Clock className="h-6 w-6" />
                <p className="text-[13.5px] font-medium text-foreground">Execution has not started yet</p>
                <p className="font-mono text-[11px]">Created on {formatDateTime(execution.createdAt)}</p>
              </div>
            )}

            {isRunning && (
              <div className="flex flex-col items-center justify-center gap-2 py-16 text-center text-subtle-foreground">
                <Loader2 className="h-6 w-6 animate-spin text-primary" />
                <p className="text-[13.5px] font-medium text-foreground">Execution is currently running…</p>
                <p className="font-mono text-[11px]">Started at {formatDateTime(execution.startedAt)}</p>
              </div>
            )}

            {isCompleted && (
              <div className="flex flex-col items-center justify-center gap-3 py-16 text-center text-subtle-foreground">
                <CircleCheck className="h-8 w-8 text-success" />
                <div>
                  <p className="text-[14px] font-medium text-foreground">Execution completed successfully</p>
                  <p className="mt-0.5 font-mono text-[11px]">Finished at {formatDateTime(execution.completedAt)}</p>
                </div>
              </div>
            )}

            {isFailed && (
              <div className="overflow-hidden rounded-[var(--radius-lg)] border border-danger/40 bg-danger-soft p-4">
                <div className="flex items-start gap-2.5">
                  <OctagonAlert className="mt-0.5 h-4 w-4 shrink-0 text-danger" />
                  <div className="min-w-0 flex-1">
                    <div className="text-[13px] font-semibold text-danger">Execution failed</div>
                    <p className="mt-1 text-[12.5px] leading-relaxed text-foreground">
                      {execution.errorMessage || "An error occurred during execution."}
                    </p>
                  </div>
                </div>
              </div>
            )}

            {isCancelled && (
              <div className="flex flex-col items-center justify-center gap-2 py-16 text-center text-subtle-foreground">
                <X className="h-6 w-6 text-muted-foreground" />
                <p className="text-[13.5px] font-medium text-foreground">Execution was cancelled</p>
              </div>
            )}
          </div>
        </section>

        {/* RIGHT — run telemetry */}
        <aside className="p-5 lg:border-l lg:border-border">
          <div className="tech-label mb-3">Run telemetry</div>
          <div className="space-y-3">
            <Panel className="p-3.5 space-y-2 font-mono text-[11px]">
              <div className="flex items-center justify-between text-subtle-foreground">
                <span>Created</span>
                <span className="text-foreground">{formatDateTime(execution.createdAt)}</span>
              </div>
              <div className="flex items-center justify-between text-subtle-foreground">
                <span>Started</span>
                <span className="text-foreground">{execution.startedAt ? formatDateTime(execution.startedAt) : "—"}</span>
              </div>
              <div className="flex items-center justify-between text-subtle-foreground">
                <span>Completed</span>
                <span className="text-foreground">{execution.completedAt ? formatDateTime(execution.completedAt) : "—"}</span>
              </div>
            </Panel>

            <Panel className="p-3.5">
              <div className="flex items-center justify-between">
                <span className="tech-label">Model</span>
                <span className="font-mono text-[11px] text-muted-foreground">Not assigned</span>
              </div>
            </Panel>
          </div>

          <div className="tech-label mb-2 mt-5">Build &amp; test</div>
          <Panel className="p-3.5">
            <div className="flex items-center gap-2 text-[12.5px]">
              <Hammer className="h-3.5 w-3.5 text-subtle-foreground" />
              <span className="text-foreground">Build</span>
              <Badge tone="neutral" className="ml-auto">
                —
              </Badge>
            </div>
            <div className="mt-1.5 flex items-center gap-2 text-[12.5px]">
              <FlaskConical className="h-3.5 w-3.5 text-subtle-foreground" />
              <span className="text-foreground">Tests</span>
              <Badge tone="neutral" className="ml-auto">
                —
              </Badge>
            </div>
            <p className="mt-3 font-mono text-[10.5px] text-subtle-foreground">
              No build or test telemetry reported.
            </p>
          </Panel>

          <Button
            variant="default"
            size="md"
            className="mt-4 w-full"
            onClick={() => navigate(`/tasks/${execution.developmentTaskId}`)}
          >
            <FileText className="h-3.5 w-3.5" />
            Open task detail
          </Button>
        </aside>
      </div>
    </div>
  )
}
