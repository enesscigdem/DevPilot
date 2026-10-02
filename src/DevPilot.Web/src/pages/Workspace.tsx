import { useEffect, useState } from "react"
import { useTranslation } from "react-i18next"
import i18n, { currentLocale, fmt, relativeTime, srv } from "@/i18n"
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
import { PipelineStrip } from "@/components/PipelineStrip"
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
  { key: "analyze", label: "overview.stages.analyze" },
  { key: "plan", label: "overview.stages.plan" },
  { key: "approved", label: "overview.stages.approved" },
  { key: "implement", label: "overview.stages.implement" },
  { key: "build", label: "overview.stages.build" },
  { key: "review", label: "overview.stages.review" },
  { key: "pr", label: "overview.stages.pr" },
]

const formatRelativeTime = relativeTime

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
      cta: i18n.t("overview.cta.inspectFailure"),
      href: item.executionId ? `/executions/${item.executionId}` : item.taskId ? `/tasks/${item.taskId}` : "/executions",
    }
  }

  if (kind === "reviewpending" || kind === "1") {
    return {
      tone: "amber",
      cta: i18n.t("overview.cta.openReview"),
      href: item.executionId ? `/review/${item.executionId}` : "/executions",
    }
  }

  if (kind === "planapprovalrequired" || kind === "2") {
    return {
      tone: "amber",
      cta: i18n.t("overview.cta.reviewPlan"),
      href: item.taskId ? `/tasks/${item.taskId}` : "/tasks",
    }
  }

  if (kind === "reviewrejected" || kind === "3") {
    return {
      tone: "red",
      cta: i18n.t("overview.cta.openReview"),
      href: item.executionId ? `/review/${item.executionId}` : "/executions",
    }
  }

  if (kind === "taskrejected" || kind === "4") {
    return {
      tone: "red",
      cta: i18n.t("overview.cta.viewTask"),
      href: item.taskId ? `/tasks/${item.taskId}` : "/tasks",
    }
  }

  return {
    tone: "neutral",
    cta: i18n.t("overview.cta.view"),
    href: item.taskId ? `/tasks/${item.taskId}` : "/tasks",
  }
}

function formatActorName(actor: WorkspaceActivityActor | string | number): string {
  const a = String(actor).toLowerCase()
  if (a === "developer" || a === "1") return i18n.t("overview.actors.developer")
  if (a === "reviewer" || a === "2") return i18n.t("overview.actors.reviewer")
  if (a === "system" || a === "3") return i18n.t("overview.actors.system")
  if (a === "planner" || a === "0") return i18n.t("overview.actors.planner")
  if (a === "user" || a === "4") return i18n.t("overview.actors.you")
  return i18n.t("overview.actors.system")
}

function mapActivityPresentation(item: WorkspaceActivityItem): {
  tone: Tone
  actor: string
  action: string
} {
  const kind = String(item.kind).toLowerCase()
  const actor = formatActorName(item.actor)
  const action = srv((item.action || "").trim())

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
  if (k === "buildfailed" || k === "0") return i18n.t("overview.badge.buildFailed")
  if (k === "testfailed" || k === "1") return i18n.t("overview.badge.testFailed")
  if (k === "developeragentfailed" || k === "2") return i18n.t("overview.badge.agentFailed")
  if (k === "reviewrejected" || k === "4") return i18n.t("overview.badge.rejected")
  if (k === "taskrejected" || k === "5") return i18n.t("overview.badge.blocked")
  if (k === "pullrequestfailed" || k === "6") return i18n.t("overview.badge.prFailed")
  if (k === "cifailed" || k === "7") return i18n.t("overview.badge.ciFailed")
  return i18n.t("overview.badge.failed")
}

export function Workspace() {
  const {
    activeWorkspace,
    overview,
    isLoadingOverview: isLoading,
    overviewError: error,
    refreshOverview: fetchOverview,
  } = useWorkspace()
  const { t } = useTranslation()
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
  const dayName = now.toLocaleDateString(currentLocale(), { weekday: "long" })
  const timeStr = now.toLocaleTimeString(currentLocale(), { hour: "2-digit", minute: "2-digit" })

  const repoFullName = overview?.header.repositoryFullName ||
    (activeWorkspace ? `${activeWorkspace.owner}/${activeWorkspace.repository}` : t("overview.noWorkspace"))

  const branchName = overview?.header.branch || (activeWorkspace ? activeWorkspace.branch : "main")
  const fileCountDisplay = overview?.header.fileCount ?? 0
  const lastIndexedRelative = overview?.header.lastIndexedAt
    ? formatRelativeTime(overview.header.lastIndexedAt)
    : t("common.never")

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
          <p className="text-[13.5px] font-medium text-foreground">{t("overview.loading")}</p>
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
            <h2 className="text-[15px] font-semibold text-foreground">{t("overview.failedLoad")}</h2>
            <p className="mt-1 text-[13px] text-muted-foreground">{error}</p>
          </div>
          <Button variant="default" size="sm" onClick={() => fetchOverview()}>
            {t("common.retry")}
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
            <div className="tech-label mb-1">{t("overview.eyebrow")}</div>
            <div className="flex items-center gap-2">
              <h1 className="truncate text-[18px] font-semibold tracking-tight text-foreground">{repoFullName}</h1>
              <Badge tone="neutral" mono>
                <GitBranch className="h-3 w-3" />
                {branchName}
              </Badge>
            </div>
            <div className="mt-0.5 flex items-center gap-2 font-mono text-[11.5px] text-subtle-foreground">
              <span>{t("common.files", { count: fileCountDisplay })}</span>
              <span>·</span>
              <span>{t("overview.indexedWhen", { when: lastIndexedRelative })}</span>
              <span>·</span>
              <span>{dayName} {timeStr}</span>
            </div>
          </div>
        </div>
      </div>

      <PipelineStrip />

      {/* Needs Attention */}
      <div className="mb-8 grid grid-cols-1 gap-3 md:grid-cols-3">
        {attention.map((item) => {
          const { tone, cta, href } = mapAttentionPresentation(item)
          return (
            <Link
              key={item.id}
              to={href}
              className="group relative overflow-hidden rounded-[var(--radius-lg)] border border-border bg-surface p-4 transition-all hover:border-border-strong hover:bg-surface-3"
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
                <span className="text-[13.5px] font-semibold text-foreground">{srv(item.title)}</span>
              </div>
              <p className="mt-2 text-[12.5px] leading-relaxed text-muted-foreground">{item.reason}</p>
              <div className="mt-3 flex items-center justify-between">
                <span className="font-mono text-[11px] text-subtle-foreground">{srv(item.metaDetail) || formatRelativeTime(item.occurredAt)}</span>
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
            {t("overview.noAttention")}
          </div>
        )}
      </div>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1fr)_360px]">
        <div className="flex min-w-0 flex-col gap-8">
          {/* Active execution */}
          <section>
            <SectionHead
              title={t("overview.activeExecution")}
              action={
                <Link to="/executions" className="text-[12px] font-medium text-primary hover:underline">
                  {t("overview.viewAll")}
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
                    title={t("overview.cancelUnavailable")}
                  >
                    <CircleStop className="h-3.5 w-3.5" />
                    {t("overview.cancel")}
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
                              {t(stageDef.label)}
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
                    label={t("overview.elapsed")}
                    value={formatElapsed(activeAgentExecution.elapsedSeconds, activeAgentExecution.startedAt, activeAgentExecution.completedAt)}
                  />
                  <Metric
                    icon={<Cpu className="h-3.5 w-3.5" />}
                    label={t("overview.tokens")}
                    value={activeAgentExecution.tokensUsed != null ? `${(activeAgentExecution.tokensUsed / 1000).toFixed(0)}K` : "—"}
                  />
                  <Metric
                    icon={<Coins className="h-3.5 w-3.5" />}
                    label={t("overview.estCost")}
                    value={activeAgentExecution.estimatedCost != null ? `$${activeAgentExecution.estimatedCost.toFixed(2)}` : "—"}
                  />
                  <Metric
                    icon={<FileCode2 className="h-3.5 w-3.5" />}
                    label={t("overview.files")}
                    value={activeAgentExecution.modifiedFileCount != null ? String(activeAgentExecution.modifiedFileCount) : "—"}
                  />
                </div>
              </Panel>
            ) : (
              <Panel className="overflow-hidden">
                <Empty text={t("overview.noActive")} />
              </Panel>
            )}
          </section>

          {/* Awaiting approval */}
          <section>
            <SectionHead title={t("overview.awaiting")} count={awaiting.length} />
            <Panel className="overflow-hidden">
              {awaiting.map((item) => (
                <ApprovalRow
                  key={item.id}
                  id={item.taskDisplayId}
                  title={item.title}
                  branch={item.branch}
                  files={item.filesTouched}
                  href={item.kind === "CodeReviewApproval" || item.kind === 1 ? `/review/${item.executionId || item.taskId}` : `/tasks/${item.taskId}`}
                  actionLabel={item.kind === "CodeReviewApproval" || item.kind === 1 ? t("overview.reviewCode") : t("overview.reviewPlan")}
                />
              ))}
              {awaiting.length === 0 && <Empty text={t("overview.nothingWaiting")} />}
            </Panel>
          </section>

          {/* Blocked / failed */}
          <section>
            <SectionHead title={t("overview.failedBlocked")} count={trouble.length} />
            <Panel className="divide-y divide-border overflow-hidden">
              {trouble.map((item) => (
                <Link
                  key={item.id}
                  to={item.executionId ? `/executions/${item.executionId}` : `/tasks/${item.taskId}`}
                  className="flex items-center gap-3 px-4 py-3 transition-colors hover:bg-surface-3"
                >
                  <StatusDot tone="red" pulse className="shrink-0" />
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span className="shrink-0 font-mono text-[11px] text-subtle-foreground">{item.taskDisplayId}</span>
                      <span className="truncate text-[13px] font-medium text-foreground" title={item.title}>{item.title}</span>
                    </div>
                    <p className="mt-0.5 truncate text-[12px] text-muted-foreground">{srv(item.summary)}</p>
                  </div>
                  <Badge tone="red" className="shrink-0">{mapFailureBadge(item.kind)}</Badge>
                </Link>
              ))}
              {trouble.length === 0 && <Empty text={t("overview.noFailed")} />}
            </Panel>
          </section>
        </div>

        {/* Right rail */}
        <div className="flex min-w-0 flex-col gap-8">
          <section>
            <SectionHead title={t("overview.recentActivity")} />
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
                    {t("overview.noActivity")}
                  </li>
                )}
              </ol>
            </Panel>
          </section>

          <section>
            <SectionHead title={t("overview.recentlyAnalyzed")} />
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
                    {recentlyAnalyzed?.language || t("overview.unknown")} · {recentlyAnalyzed?.loc != null ? t("overview.loc", { n: fmt.number(recentlyAnalyzed.loc) }) : t("overview.noLoc")}
                  </div>
                </div>
              </div>
              <div className="mt-3.5 space-y-2.5">
                <MiniStat
                  label={t("overview.symbolsIndexed")}
                  value={recentlyAnalyzed ? fmt.number(recentlyAnalyzed.symbolsCount) : "0"}
                  pct={recentlyAnalyzed?.isIndexed ? 100 : 0}
                />
                <MiniStat
                  label={t("overview.typesResolved")}
                  value={recentlyAnalyzed ? fmt.number(recentlyAnalyzed.typesCount) : "0"}
                  pct={recentlyAnalyzed?.isIndexed ? 100 : 0}
                />
                <MiniStat
                  label={t("overview.referencesMapped")}
                  value={recentlyAnalyzed?.referencesCount != null ? fmt.number(recentlyAnalyzed.referencesCount) : "—"}
                  pct={recentlyAnalyzed?.referencesCount != null ? (recentlyAnalyzed.isIndexed ? 100 : 0) : null}
                />
              </div>
              <Link
                to="/projects"
                className="mt-4 flex items-center justify-center gap-1.5 rounded-[var(--radius-md)] border border-border bg-surface-2 py-2 text-[12.5px] font-medium text-foreground transition-colors hover:bg-surface-3"
              >
                {t("overview.openProject")}
                <ArrowRight className="h-3.5 w-3.5" />
              </Link>
            </Panel>
          </section>

          <section>
            <SectionHead title={t("overview.shipped")} />
            <Panel className="divide-y divide-border overflow-hidden">
              {shipped.map((item) => (
                <div key={item.id} className="flex items-center gap-2.5 px-4 py-3">
                  <GitPullRequest className="h-4 w-4 text-success" />
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-[12.5px] font-medium text-foreground">{item.title}</div>
                    <div className="font-mono text-[11px] text-subtle-foreground">
                      {item.pullRequestNumber != null ? `#${item.pullRequestNumber} · ` : ""}
                      {t("overview.mergedWhen", { when: formatRelativeTime(item.mergedAt) })}
                    </div>
                  </div>
                  <Badge tone="green">{t("overview.merged")}</Badge>
                </div>
              ))}
              {shipped.length === 0 && (
                <div className="px-4 py-4 text-center text-[12.5px] text-subtle-foreground">
                  {t("overview.noShipped")}
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
  const { t } = useTranslation()
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
            {branch} · {files != null ? t("common.files", { count: files }) : t("overview.noFilesCount")}
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
