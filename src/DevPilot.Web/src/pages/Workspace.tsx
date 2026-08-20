import { useEffect, useState } from "react"
import { Link } from "react-router-dom"
import {
  ArrowRight,
  GitBranch,
  Boxes,
  FileCode2,
  Clock,
  Coins,
  Cpu,
  CircleStop,
  GitPullRequest,
  Loader2,
  AlertCircle,
} from "lucide-react"
import { PageContainer, SectionHead } from "@/components/shared"
import { Badge, Button, Meter, Panel, StatusDot } from "@/components/ui/primitives"
import { useWorkspace } from "@/lib/workspace"
import { cn } from "@/lib/utils"
import type {
  WorkspaceAttentionItem,
  WorkspaceActivityItem,
  WorkspaceActivityActor,
  Tone,
} from "@/types"

const stageDefinitions = [
  { key: "analyze", label: "Analyze" },
  { key: "plan", label: "Plan" },
  { key: "approved", label: "Approved" },
  { key: "implement", label: "Implement" },
  { key: "build", label: "Build & Test" },
  { key: "review", label: "Review" },
  { key: "pr", label: "Pull Request" },
]

function formatRelativeTime(dateStr?: string | null): string {
  if (!dateStr) return "never"
  try {
    const d = new Date(dateStr)
    const now = new Date()
    const diffSec = Math.floor((now.getTime() - d.getTime()) / 1000)
    if (diffSec < 60) return "just now"
    const diffMin = Math.floor(diffSec / 60)
    if (diffMin < 60) return `${diffMin}m ago`
    const diffHours = Math.floor(diffMin / 60)
    if (diffHours < 24) return `${diffHours}h ago`
    const diffDays = Math.floor(diffHours / 24)
    if (diffDays < 30) return `${diffDays}d ago`
    return d.toLocaleDateString()
  } catch {
    return dateStr
  }
}

function formatElapsed(elapsedSeconds?: number | null, startedAt?: string | null, completedAt?: string | null): string {
  if (completedAt && startedAt) {
    const start = new Date(startedAt).getTime()
    const end = new Date(completedAt).getTime()
    if (!isNaN(start) && !isNaN(end) && end >= start) {
      const diffSec = Math.floor((end - start) / 1000)
      const mins = Math.floor(diffSec / 60)
      const secs = diffSec % 60
      if (mins >= 60) {
        const hrs = Math.floor(mins / 60)
        const remMins = mins % 60
        return `${String(hrs).padStart(2, "0")}:${String(remMins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
      }
      return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
    }
  }
  if (completedAt && elapsedSeconds != null) {
    const mins = Math.floor(elapsedSeconds / 60)
    const secs = elapsedSeconds % 60
    if (mins >= 60) {
      const hrs = Math.floor(mins / 60)
      const remMins = mins % 60
      return `${String(hrs).padStart(2, "0")}:${String(remMins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
    }
    return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
  }
  if (startedAt && !completedAt) {
    const start = new Date(startedAt).getTime()
    if (!isNaN(start)) {
      const diffSec = Math.max(0, Math.floor((Date.now() - start) / 1000))
      const mins = Math.floor(diffSec / 60)
      const secs = diffSec % 60
      if (mins >= 60) {
        const hrs = Math.floor(mins / 60)
        const remMins = mins % 60
        return `${String(hrs).padStart(2, "0")}:${String(remMins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
      }
      return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
    }
  }
  if (elapsedSeconds != null) {
    const mins = Math.floor(elapsedSeconds / 60)
    const secs = elapsedSeconds % 60
    if (mins >= 60) {
      const hrs = Math.floor(mins / 60)
      const remMins = mins % 60
      return `${String(hrs).padStart(2, "0")}:${String(remMins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
    }
    return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
  }
  return "—"
}

function mapAttentionPresentation(item: WorkspaceAttentionItem): {
  tone: Tone
  cta: string
  href: string
} {
  const kind = String(item.kind).toLowerCase()

  if (
    kind === "executionfailed" ||
    kind === "buildfailed" ||
    kind === "testfailed" ||
    kind === "developeragentfailed" ||
    kind === "pullrequestfailed" ||
    kind === "cifailed" ||
    kind === "0" ||
    kind === "5" ||
    kind === "6" ||
    kind === "7" ||
    kind === "8" ||
    kind === "9"
  ) {
    return {
      tone: "red",
      cta: "Inspect failure",
      href: item.executionId ? `/executions/${item.executionId}` : item.taskId ? `/tasks/${item.taskId}` : "/executions",
    }
  }

  if (kind === "reviewpending" || kind === "1") {
    return {
      tone: "amber",
      cta: "Open review",
      href: item.executionId ? `/review/${item.executionId}` : "/executions",
    }
  }

  if (kind === "planapprovalrequired" || kind === "2") {
    return {
      tone: "amber",
      cta: "Review plan",
      href: item.taskId ? `/tasks/${item.taskId}` : "/tasks",
    }
  }

  if (kind === "reviewrejected" || kind === "3") {
    return {
      tone: "red",
      cta: "Open review",
      href: item.executionId ? `/review/${item.executionId}` : "/executions",
    }
  }

  if (kind === "taskrejected" || kind === "4") {
    return {
      tone: "red",
      cta: "View task",
      href: item.taskId ? `/tasks/${item.taskId}` : "/tasks",
    }
  }

  return {
    tone: "neutral",
    cta: "View",
    href: item.taskId ? `/tasks/${item.taskId}` : "/tasks",
  }
}

function formatActorName(actor: WorkspaceActivityActor | string | number): string {
  const a = String(actor).toLowerCase()
  if (a === "developer" || a === "1") return "Developer"
  if (a === "reviewer" || a === "2") return "Reviewer"
  if (a === "system" || a === "3") return "System"
  if (a === "planner" || a === "0") return "Planner"
  if (a === "user" || a === "4") return "You"
  return "System"
}

function mapActivityPresentation(item: WorkspaceActivityItem): {
  tone: Tone
  actor: string
  action: string
} {
  const kind = String(item.kind).toLowerCase()
  const actor = formatActorName(item.actor)
  const action = (item.action || "").trim()

  let tone: Tone = "neutral"
  if (kind.includes("failed") || kind === "1" || kind === "3" || kind === "6") {
    tone = "red"
  } else if (kind.includes("approved") || kind.includes("completed") || kind.includes("passed") || kind === "0" || kind === "2" || kind === "5" || kind === "7") {
    tone = "green"
  } else if (kind.includes("created") || kind === "4") {
    tone = "blue"
  }

  return { tone, actor, action }
}

function mapFailureBadge(kindVal: string | number): string {
  const k = String(kindVal).toLowerCase()
  if (k === "buildfailed" || k === "0") return "Build failed"
  if (k === "testfailed" || k === "1") return "Test failed"
  if (k === "developeragentfailed" || k === "2") return "Agent failed"
  if (k === "reviewrejected" || k === "4") return "Rejected"
  if (k === "taskrejected" || k === "5") return "Blocked"
  if (k === "pullrequestfailed" || k === "6") return "PR failed"
  if (k === "cifailed" || k === "7") return "CI failed"
  return "Failed"
}

export function Workspace() {
  const {
    activeWorkspace,
    overview,
    isLoadingOverview: isLoading,
    overviewError: error,
    refreshOverview: fetchOverview,
  } = useWorkspace()
  const [, setTimerTick] = useState(0)

  // 1-second client timer for live active execution elapsed duration
  const isExecutionRunning = Boolean(
    overview?.activeAgentExecution && !overview.activeAgentExecution.completedAt,
  )
  useEffect(() => {
    if (!isExecutionRunning) return
    const interval = setInterval(() => {
      setTimerTick((t) => t + 1)
    }, 1000)
    return () => clearInterval(interval)
  }, [isExecutionRunning])

  const now = new Date()
  const dayName = now.toLocaleDateString(undefined, { weekday: "long" })
  const timeStr = now.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })

  const repoFullName = overview?.header.repositoryFullName ||
    (activeWorkspace ? `${activeWorkspace.owner}/${activeWorkspace.repository}` : "No workspace selected")

  const branchName = overview?.header.branch || (activeWorkspace ? activeWorkspace.branch : "main")
  const fileCountDisplay = overview?.header.fileCount ?? 0
  const lastIndexedRelative = overview?.header.lastIndexedAt
    ? formatRelativeTime(overview.header.lastIndexedAt)
    : "never"

  const attention = overview?.needsAttention ?? []
  const activeAgentExecution = overview?.activeAgentExecution ?? null
  const awaiting = overview?.awaitingApproval ?? []
  const trouble = overview?.failedOrBlocked ?? []
  const recentActivity = overview?.recentActivity ?? []
  const recentlyAnalyzed = overview?.recentlyAnalyzed
  const shipped = overview?.shippedRecently ?? []

  if (isLoading && !overview) {
    return (
      <PageContainer>
        <div className="flex min-h-[400px] flex-col items-center justify-center gap-3 text-center">
          <Loader2 className="h-6 w-6 animate-spin text-subtle-foreground" />
          <p className="text-[13.5px] font-medium text-foreground">Loading workspace dashboard…</p>
        </div>
      </PageContainer>
    )
  }

  if (error && !overview) {
    return (
      <PageContainer>
        <Panel className="my-8 flex flex-col items-center justify-center gap-3 p-8 text-center">
          <AlertCircle className="h-7 w-7 text-danger" />
          <div>
            <h2 className="text-[15px] font-semibold text-foreground">Failed to Load Dashboard</h2>
            <p className="mt-1 text-[13px] text-muted-foreground">{error}</p>
          </div>
          <Button variant="default" size="sm" onClick={() => fetchOverview()}>
            Retry
          </Button>
        </Panel>
      </PageContainer>
    )
  }

  return (
    <PageContainer>
      {/* Top dashboard identity bar */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4 border-b border-border pb-5">
        <div className="flex min-w-0 items-center gap-3">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-[var(--radius-lg)] border border-primary-ring/40 bg-primary-soft text-primary">
            <Boxes className="h-5 w-5" />
          </div>
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <h1 className="truncate text-[18px] font-semibold tracking-tight text-foreground">{repoFullName}</h1>
              <Badge tone="neutral" mono>
                <GitBranch className="h-3 w-3" />
                {branchName}
              </Badge>
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11.5px] text-subtle-foreground">
              <span>{fileCountDisplay} files</span>
              <span>·</span>
              <span>indexed {lastIndexedRelative}</span>
              <span>·</span>
              <span>{dayName} {timeStr}</span>
            </div>
          </div>
        </div>
      </div>

      {/* Needs Attention */}
      <div className="mb-8 grid grid-cols-1 gap-3 md:grid-cols-3">
        {attention.map((item) => {
          const { tone, cta, href } = mapAttentionPresentation(item)
          return (
            <Link
              key={item.id}
              to={href}
              className="group relative overflow-hidden rounded-[var(--radius-lg)] border border-border bg-surface p-4 transition-all hover:border-border-strong hover:bg-surface-2"
            >
              <div
                className={cn(
                  "absolute inset-x-0 top-0 h-[2px]",
                  tone === "red" && "bg-danger",
                  tone === "amber" && "bg-accent",
                  tone === "blue" && "bg-primary",
                )}
              />
              <div className="flex items-center gap-2">
                <StatusDot tone={tone} pulse={tone === "red"} />
                <span className="text-[13.5px] font-semibold text-foreground">{item.title}</span>
              </div>
              <p className="mt-2 text-[12.5px] leading-relaxed text-muted-foreground">{item.reason}</p>
              <div className="mt-3 flex items-center justify-between">
                <span className="font-mono text-[11px] text-subtle-foreground">{item.metaDetail || formatRelativeTime(item.occurredAt)}</span>
                <span className="flex items-center gap-1 text-[12px] font-medium text-primary opacity-0 transition-opacity group-hover:opacity-100">
                  {cta}
                  <ArrowRight className="h-3.5 w-3.5" />
                </span>
              </div>
            </Link>
          )
        })}
        {attention.length === 0 && (
          <div className="col-span-full rounded-[var(--radius-lg)] border border-border bg-surface p-6 text-center text-[13px] text-subtle-foreground">
            No items require your attention.
          </div>
        )}
      </div>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1fr)_360px]">
        <div className="flex min-w-0 flex-col gap-8">
          {/* Active execution */}
          <section>
            <SectionHead
              title="Active agent execution"
              action={
                <Link to="/executions" className="text-[12px] font-medium text-primary hover:underline">
                  View all
                </Link>
              }
            />
            {activeAgentExecution ? (
              <Panel className="overflow-hidden">
                <div className="flex items-center justify-between gap-3 border-b border-border bg-surface-2 px-4 py-3">
                  <div className="flex min-w-0 flex-1 items-center gap-2.5">
                    <StatusDot tone="blue" pulse className="shrink-0" />
                    <span className="shrink-0 font-mono text-[12px] font-medium text-foreground">{activeAgentExecution.taskDisplayId}</span>
                    <span className="truncate text-[13px] text-foreground" title={activeAgentExecution.taskTitle}>{activeAgentExecution.taskTitle}</span>
                  </div>
                  <Button
                    variant="danger"
                    size="sm"
                    className="shrink-0 gap-1.5 opacity-60 cursor-not-allowed"
                    disabled
                    title="Active execution cancellation is currently unavailable"
                  >
                    <CircleStop className="h-3.5 w-3.5" />
                    Cancel
                  </Button>
                </div>

                {/* stage rail */}
                <div className="overflow-x-auto px-4 py-4 scrollbar-none">
                  <div className="flex min-w-0 items-center gap-1">
                    {stageDefinitions.map((stageDef, i) => {
                      const foundStep = activeAgentExecution.stages?.find((s) => s.stageKey === stageDef.key)
                      const stateStr = foundStep ? String(foundStep.state).toLowerCase() : "todo"
                      const state = stateStr === "done" || stateStr === "2"
                        ? "done"
                        : stateStr === "active" || stateStr === "1"
                          ? "active"
                          : stateStr === "failed" || stateStr === "3"
                            ? "failed"
                            : stateStr === "blocked" || stateStr === "4"
                              ? "blocked"
                              : "todo"

                      return (
                        <div key={stageDef.key} className="flex min-w-0 flex-1 items-center gap-1">
                          <div className="flex shrink-0 flex-col items-center gap-1.5">
                            <div
                              className={cn(
                                "flex h-6 w-6 items-center justify-center rounded-full border text-[10px] font-semibold transition-colors",
                                state === "done" && "border-success bg-success-soft text-success",
                                state === "active" && "border-primary bg-primary text-primary-foreground",
                                state === "failed" && "border-danger bg-danger text-primary-foreground",
                                state === "blocked" && "border-accent bg-accent text-primary-foreground",
                                state === "todo" && "border-border bg-surface text-subtle-foreground",
                              )}
                            >
                              {state === "active" ? (
                                <span className="h-1.5 w-1.5 rounded-full bg-primary-foreground animate-pulse-dot" />
                              ) : (
                                i + 1
                              )}
                            </div>
                            <span
                              className={cn(
                                "whitespace-nowrap text-[10.5px] font-medium",
                                state === "active" ? "text-foreground" : "text-subtle-foreground",
                              )}
                            >
                              {stageDef.label}
                            </span>
                          </div>
                          {i < stageDefinitions.length - 1 && (
                            <div
                              className={cn(
                                "mb-4 h-[2px] min-w-[8px] flex-1 rounded-full",
                                state === "done" ? "bg-success" : "bg-border",
                              )}
                            />
                          )}
                        </div>
                      )
                    })}
                  </div>
                </div>

                <div className="grid grid-cols-2 gap-px border-t border-border bg-border sm:grid-cols-4">
                  <Metric
                    icon={<Clock className="h-3.5 w-3.5" />}
                    label="Elapsed"
                    value={formatElapsed(activeAgentExecution.elapsedSeconds, activeAgentExecution.startedAt, activeAgentExecution.completedAt)}
                  />
                  <Metric
                    icon={<Cpu className="h-3.5 w-3.5" />}
                    label="Tokens"
                    value={activeAgentExecution.tokensUsed != null ? `${(activeAgentExecution.tokensUsed / 1000).toFixed(0)}K` : "—"}
                  />
                  <Metric
                    icon={<Coins className="h-3.5 w-3.5" />}
                    label="Est. cost"
                    value={activeAgentExecution.estimatedCost != null ? `$${activeAgentExecution.estimatedCost.toFixed(2)}` : "—"}
                  />
                  <Metric
                    icon={<FileCode2 className="h-3.5 w-3.5" />}
                    label="Files"
                    value={activeAgentExecution.modifiedFileCount != null ? String(activeAgentExecution.modifiedFileCount) : "—"}
                  />
                </div>
              </Panel>
            ) : (
              <Panel className="overflow-hidden">
                <Empty text="No active agent execution running for this workspace." />
              </Panel>
            )}
          </section>

          {/* Awaiting approval */}
          <section>
            <SectionHead title="Awaiting your approval" count={awaiting.length} />
            <Panel className="overflow-hidden">
              {awaiting.map((item) => (
                <ApprovalRow
                  key={item.id}
                  id={item.taskDisplayId}
                  title={item.title}
                  branch={item.branch}
                  files={item.filesTouched}
                  href={item.kind === "CodeReviewApproval" || item.kind === 1 ? `/review/${item.executionId || item.taskId}` : `/tasks/${item.taskId}`}
                  actionLabel={item.kind === "CodeReviewApproval" || item.kind === 1 ? "Review code" : "Review plan"}
                />
              ))}
              {awaiting.length === 0 && <Empty text="Nothing waiting on you." />}
            </Panel>
          </section>

          {/* Blocked / failed */}
          <section>
            <SectionHead title="Failed or blocked" count={trouble.length} />
            <Panel className="divide-y divide-border overflow-hidden">
              {trouble.map((item) => (
                <Link
                  key={item.id}
                  to={item.executionId ? `/executions/${item.executionId}` : `/tasks/${item.taskId}`}
                  className="flex items-center gap-3 px-4 py-3 transition-colors hover:bg-surface-2"
                >
                  <StatusDot tone="red" pulse className="shrink-0" />
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span className="shrink-0 font-mono text-[11px] text-subtle-foreground">{item.taskDisplayId}</span>
                      <span className="truncate text-[13px] font-medium text-foreground" title={item.title}>{item.title}</span>
                    </div>
                    <p className="mt-0.5 truncate text-[12px] text-muted-foreground">{item.summary}</p>
                  </div>
                  <Badge tone="red" className="shrink-0">{mapFailureBadge(item.kind)}</Badge>
                </Link>
              ))}
              {trouble.length === 0 && <Empty text="No failed or blocked tasks." />}
            </Panel>
          </section>
        </div>

        {/* Right rail */}
        <div className="flex min-w-0 flex-col gap-8">
          <section>
            <SectionHead title="Recent engineering activity" />
            <Panel className="p-4">
              <ol className="relative ml-1.5 border-l border-border">
                {recentActivity.map((item) => {
                  const { tone, actor, action } = mapActivityPresentation(item)
                  return (
                    <li key={item.id} className="relative mb-4 pl-4 last:mb-0">
                      <span
                        className={cn(
                          "absolute -left-[5px] top-1 h-2.5 w-2.5 rounded-full border-2 border-surface",
                          tone === "green" && "bg-success",
                          tone === "red" && "bg-danger",
                          tone === "blue" && "bg-primary",
                          tone === "amber" && "bg-accent",
                          tone === "neutral" && "bg-subtle-foreground",
                          tone === "gray" && "bg-subtle-foreground",
                        )}
                      />
                      <p className="text-[12.5px] leading-snug text-foreground">
                        <span className="font-medium">{actor}</span>{" "}
                        <span className="text-muted-foreground">{action}</span>{" "}
                        <span className="font-mono text-[11.5px] text-foreground">{item.target}</span>
                      </p>
                      <span className="font-mono text-[10.5px] text-subtle-foreground">{formatRelativeTime(item.occurredAt)}</span>
                    </li>
                  )
                })}
                {recentActivity.length === 0 && (
                  <li className="text-center text-[12.5px] text-subtle-foreground py-4">
                    No recent activity recorded.
                  </li>
                )}
              </ol>
            </Panel>
          </section>

          <section>
            <SectionHead title="Recently analyzed" />
            <Panel className="p-4">
              <div className="flex items-center gap-2.5">
                <div className="flex h-9 w-9 items-center justify-center rounded-[var(--radius-md)] bg-foreground text-canvas">
                  <Boxes className="h-4.5 w-4.5" strokeWidth={2} />
                </div>
                <div className="min-w-0">
                  <div className="truncate font-mono text-[12.5px] font-medium text-foreground">
                    {repoFullName}
                  </div>
                  <div className="font-mono text-[11px] text-subtle-foreground">
                    {recentlyAnalyzed?.language || "Unknown"} · {recentlyAnalyzed?.loc != null ? `${recentlyAnalyzed.loc.toLocaleString()} LOC` : "— LOC"}
                  </div>
                </div>
              </div>
              <div className="mt-3.5 space-y-2.5">
                <MiniStat
                  label="Symbols indexed"
                  value={recentlyAnalyzed ? recentlyAnalyzed.symbolsCount.toLocaleString() : "0"}
                  pct={recentlyAnalyzed?.isIndexed ? 100 : 0}
                />
                <MiniStat
                  label="Types resolved"
                  value={recentlyAnalyzed ? recentlyAnalyzed.typesCount.toLocaleString() : "0"}
                  pct={recentlyAnalyzed?.isIndexed ? 100 : 0}
                />
                <MiniStat
                  label="References mapped"
                  value={recentlyAnalyzed?.referencesCount != null ? recentlyAnalyzed.referencesCount.toLocaleString() : "—"}
                  pct={recentlyAnalyzed?.referencesCount != null ? (recentlyAnalyzed.isIndexed ? 100 : 0) : null}
                />
              </div>
              <Link
                to="/projects"
                className="mt-4 flex items-center justify-center gap-1.5 rounded-[var(--radius-md)] border border-border bg-surface-2 py-2 text-[12.5px] font-medium text-foreground transition-colors hover:bg-surface-3"
              >
                Open project workspace
                <ArrowRight className="h-3.5 w-3.5" />
              </Link>
            </Panel>
          </section>

          <section>
            <SectionHead title="Shipped recently" />
            <Panel className="divide-y divide-border overflow-hidden">
              {shipped.map((item) => (
                <div key={item.id} className="flex items-center gap-2.5 px-4 py-3">
                  <GitPullRequest className="h-4 w-4 text-success" />
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-[12.5px] font-medium text-foreground">{item.title}</div>
                    <div className="font-mono text-[11px] text-subtle-foreground">
                      {item.pullRequestNumber != null ? `#${item.pullRequestNumber} · ` : ""}
                      merged {formatRelativeTime(item.mergedAt)}
                    </div>
                  </div>
                  <Badge tone="green">Merged</Badge>
                </div>
              ))}
              {shipped.length === 0 && (
                <div className="px-4 py-4 text-center text-[12.5px] text-subtle-foreground">
                  No shipped changes yet.
                </div>
              )}
            </Panel>
          </section>
        </div>
      </div>
    </PageContainer>
  )
}

function Metric({ icon, label, value }: { icon: React.ReactNode; label: string; value: string }) {
  return (
    <div className="min-w-0 bg-surface px-4 py-3">
      <div className="flex items-center gap-1.5 text-subtle-foreground">
        <span className="shrink-0">{icon}</span>
        <span className="tech-label truncate">{label}</span>
      </div>
      <div className="mt-1 truncate font-mono text-[15px] font-semibold text-foreground">{value}</div>
    </div>
  )
}

function ApprovalRow({
  id,
  title,
  branch,
  files,
  href,
  actionLabel,
}: {
  id: string
  title: string
  branch: string
  files?: number | null
  href: string
  actionLabel: string
}) {
  return (
    <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-3 last:border-b-0">
      <div className="flex min-w-0 flex-1 items-center gap-3">
        <StatusDot tone="amber" className="shrink-0" />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="shrink-0 font-mono text-[11px] text-subtle-foreground">{id}</span>
            <span className="truncate text-[13px] font-medium text-foreground" title={title}>{title}</span>
          </div>
          <div className="truncate font-mono text-[11px] text-subtle-foreground">
            {branch} · {files != null ? `${files} files` : "— files"}
          </div>
        </div>
      </div>
      <Link to={href} className="shrink-0">
        <Button variant="default" size="sm" className="gap-1.5">
          {actionLabel}
          <ArrowRight className="h-3.5 w-3.5" />
        </Button>
      </Link>
    </div>
  )
}

function MiniStat({ label, value, pct }: { label: string; value: string; pct?: number | null }) {
  const isAvailable = pct !== null && pct !== undefined
  return (
    <div>
      <div className="mb-1 flex items-center justify-between text-[12px]">
        <span className="text-muted-foreground">{label}</span>
        <span className="font-mono text-foreground">{value}</span>
      </div>
      <Meter
        value={isAvailable ? pct : 0}
        tone={isAvailable ? "blue" : "neutral"}
        className={!isAvailable ? "opacity-35" : undefined}
      />
    </div>
  )
}

function Empty({ text }: { text: string }) {
  return <div className="px-4 py-6 text-center text-[13px] text-subtle-foreground">{text}</div>
}
