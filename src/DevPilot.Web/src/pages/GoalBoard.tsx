import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { Link, useParams } from "react-router-dom"
import { AlertCircle, ArrowLeft, ExternalLink, GitPullRequest, Hand, Loader2 } from "lucide-react"
import { useTranslation } from "react-i18next"
import { cancelGoal, getGoal } from "@/api"
import { AutomationBanner } from "@/components/goals/AutomationBanner"
import { GoalProgressBar } from "@/components/goals/GoalProgressBar"
import { PageContainer } from "@/components/shared"
import { Badge, Button, Panel, StatusDot } from "@/components/ui/primitives"
import { relativeTime, srv } from "@/i18n"
import { formatUsd, phaseView, shortKey, STATUS_TONE } from "@/lib/goals"
import { cn } from "@/lib/utils"
import { useWorkspace } from "@/lib/workspace"
import type { GoalDetail, GoalTaskView } from "@/types"

const POLL_MS = 4000

export function GoalBoard() {
  const { t } = useTranslation()
  const { id } = useParams<{ id: string }>()
  const { activeWorkspaceId } = useWorkspace()
  const [goal, setGoal] = useState<GoalDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [missing, setMissing] = useState(false)
  const [cancelling, setCancelling] = useState(false)
  const active = useRef(true)

  const load = useCallback(async () => {
    if (!id || !activeWorkspaceId) return
    try {
      const loaded = await getGoal(activeWorkspaceId, id)
      if (!active.current) return
      setGoal(loaded)
      setError(null)
      setMissing(false)
    } catch (err) {
      if (!active.current) return
      const message = err instanceof Error ? err.message : t("goals.errLoad")
      // A goal of another repository (or a deleted one) answers 404: say so instead of retrying forever.
      if (message.includes("404") || /not found/i.test(message)) setMissing(true)
      else setError(message)
    }
  }, [id, activeWorkspaceId, t])

  useEffect(() => {
    active.current = true
    void load()
    return () => {
      active.current = false
    }
  }, [load])

  // The board is live while the goal is: a finished or cancelled goal stops polling.
  useEffect(() => {
    if (goal && goal.status !== "Active") return
    const timer = setInterval(() => void load(), POLL_MS)
    return () => clearInterval(timer)
  }, [goal, load])

  const waves = useMemo(() => {
    const groups = new Map<number, GoalTaskView[]>()
    for (const task of goal?.tasks ?? []) groups.set(task.wave, [...(groups.get(task.wave) ?? []), task])
    return [...groups.entries()].sort((a, b) => a[0] - b[0])
  }, [goal])

  const byKey = useMemo(() => new Map((goal?.tasks ?? []).map((task) => [task.key, task])), [goal])
  // A ready plan waits for a person only when DevPilot does not approve plans itself; otherwise it is held by the
  // system (for example behind a task that changes the same file) and will start on its own.
  const needsYou = (goal?.tasks ?? []).filter((task) => task.needsPerson)
  const planWaiting = needsYou.filter((task) => task.phase === "PlanReady").length
  const openPrs = goal?.progress.pullRequests ?? 0

  const cancel = async () => {
    if (!goal || !activeWorkspaceId) return
    setCancelling(true)
    try {
      setGoal(await cancelGoal(activeWorkspaceId, goal.id))
    } catch (err) {
      setError(err instanceof Error ? err.message : t("goals.errCancel"))
    } finally {
      setCancelling(false)
    }
  }

  if (missing) {
    return (
      <PageContainer>
        <BackLink />
        <Panel className="px-6 py-10 text-center text-[13px] text-muted-foreground">{t("goals.board.notFound")}</Panel>
      </PageContainer>
    )
  }

  if (!goal) {
    return (
      <PageContainer>
        <BackLink />
        {error ? (
          <div className="flex items-center gap-1.5 text-[13px] font-medium text-danger">
            <AlertCircle className="h-4 w-4" /> {error}
          </div>
        ) : (
          <div className="flex items-center gap-2 text-[13px] text-muted-foreground">
            <Loader2 className="h-4 w-4 animate-spin" /> {t("goals.recent.loading")}
          </div>
        )}
      </PageContainer>
    )
  }

  const { progress } = goal
  const done = goal.status === "Completed"

  return (
    <PageContainer>
      <BackLink />

      <header className="mb-6">
        <div className="flex flex-wrap items-start gap-3">
          <div className="min-w-0 flex-1">
            <div className="mb-1.5 flex items-center gap-2">
              <Badge tone={STATUS_TONE[goal.status]}>
                {goal.status === "Active" && <StatusDot tone="blue" pulse />}
                {t(`goals.board.status${goal.status}`)}
              </Badge>
              <span className="text-[12px] text-subtle-foreground">
                {done && goal.completedAt
                  ? t("goals.board.finished", { when: relativeTime(goal.completedAt) })
                  : t("goals.board.started", { when: relativeTime(goal.createdAt) })}
              </span>
            </div>
            <h1 className="text-[22px] font-semibold leading-tight tracking-tight text-foreground">{goal.title}</h1>
          </div>
          {goal.status === "Active" && (
            <div className="text-right">
              <Button variant="danger" size="sm" onClick={() => void cancel()} disabled={cancelling}>
                {cancelling ? t("goals.board.cancelling") : t("goals.board.cancel")}
              </Button>
              <div className="mt-1 text-[11px] text-subtle-foreground">{t("goals.board.cancelHint")}</div>
            </div>
          )}
        </div>

        <div className="mt-5 space-y-2.5">
          <GoalProgressBar progress={progress} />
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[12.5px] text-muted-foreground">
            <span className="font-medium text-foreground">
              {t("goals.board.progress", { merged: progress.merged, total: progress.total })}
            </span>
            {progress.pullRequests > 0 && <span>{t("goals.board.openPrs", { count: progress.pullRequests })}</span>}
            {progress.running > 0 && <span>{t("goals.board.running", { count: progress.running })}</span>}
            {progress.failed > 0 && <span className="text-danger">{t("goals.board.failed", { count: progress.failed })}</span>}
            {progress.stopped > 0 && <span>{t("goals.board.stopped", { count: progress.stopped })}</span>}
            {goal.estimatedUsd !== null && (
              <span className="ml-auto text-subtle-foreground">{t("goals.board.estimatedCost", { cost: formatUsd(goal.estimatedUsd) })}</span>
            )}
          </div>
        </div>
      </header>

      {error && (
        <div className="mb-4 flex items-center gap-1.5 text-[12.5px] font-medium text-danger">
          <AlertCircle className="h-3.5 w-3.5" /> {error}
        </div>
      )}

      {goal.status === "Active" && planWaiting > 0 && activeWorkspaceId && (
        <div className="mb-6">
          <AutomationBanner workspaceId={activeWorkspaceId} />
        </div>
      )}

      {needsYou.length > 0 && (
        <Panel className="mb-6 overflow-hidden border-accent-line/60">
          <div className="flex items-center gap-2 border-b border-accent-line/40 bg-accent-soft/60 px-4 py-2.5 text-[13px] font-semibold text-foreground">
            <Hand className="h-4 w-4 text-accent" /> {t("goals.board.failedTitle")}
            <Badge tone="amber">{needsYou.length}</Badge>
          </div>
          <ul className="divide-y divide-border">
            {needsYou.map((task) => (
              <li key={task.key} className="flex flex-wrap items-center gap-3 px-4 py-2.5">
                <PhaseBadge task={task} />
                <span className="min-w-0 flex-1 truncate text-[13px] text-foreground">{task.title}</span>
                <Link
                  to={task.phase === "InReview" && task.executionId ? `/review/${task.executionId}` : task.executionId && task.phase === "Failed" ? `/executions/${task.executionId}` : `/tasks/${task.taskId}`}
                  className="inline-flex items-center gap-1 text-[12.5px] font-medium text-primary hover:underline"
                >
                  {task.phase === "PlanReady" ? t("goals.board.reviewTasks") : task.executionId ? t("goals.board.openRun") : t("goals.board.openTask")}
                </Link>
              </li>
            ))}
          </ul>
        </Panel>
      )}

      {openPrs > 0 && goal.status === "Active" && (
        <p className="mb-6 flex items-center gap-2 text-[12.5px] text-muted-foreground">
          <GitPullRequest className="h-3.5 w-3.5 shrink-0" /> {t("goals.board.prsWaiting")}
        </p>
      )}

      {done && <p className="mb-6 text-[13px] font-medium text-success">{t("goals.board.allDone")}</p>}

      <div className="space-y-8">
        {waves.map(([wave, tasks]) => (
          <section key={wave}>
            <div className="mb-3 flex items-center gap-2.5">
              <span className="flex h-6 w-6 items-center justify-center rounded-full border border-primary-ring bg-primary-soft font-mono text-[11px] font-semibold text-primary">
                {wave}
              </span>
              <h2 className="text-[13px] font-semibold text-foreground">{t("goals.board.waveTitle", { n: wave })}</h2>
              <span className="text-[12px] text-subtle-foreground">{tasks.length}</span>
            </div>
            <div className="grid gap-3 lg:grid-cols-2 2xl:grid-cols-3">
              {tasks.map((task) => (
                <TaskCard key={task.key} task={task} byKey={byKey} />
              ))}
            </div>
          </section>
        ))}
      </div>
    </PageContainer>
  )
}

function BackLink() {
  const { t } = useTranslation()
  return (
    <Link to="/goals" className="mb-4 inline-flex items-center gap-1.5 text-[12.5px] font-medium text-muted-foreground hover:text-foreground">
      <ArrowLeft className="h-3.5 w-3.5" /> {t("goals.board.back")}
    </Link>
  )
}

function PhaseBadge({ task }: { task: GoalTaskView }) {
  const { t } = useTranslation()
  const view = phaseView(task)
  return (
    <Badge tone={view.tone}>
      <StatusDot tone={view.tone} pulse={view.pulse} />
      {t(`goals.phase.${view.label}`)}
    </Badge>
  )
}

function TaskCard({ task, byKey }: { task: GoalTaskView; byKey: Map<string, GoalTaskView> }) {
  const { t } = useTranslation()
  const style = phaseView(task)
  const target = task.executionId ? `/executions/${task.executionId}` : `/tasks/${task.taskId}`
  const settled = task.phase === "Merged" || task.phase === "Stopped"

  return (
    <Panel
      className={cn(
        "flex flex-col gap-2 border-l-[3px] p-3.5 transition-colors hover:bg-surface-2",
        style.tone === "green" && "border-l-success",
        style.tone === "blue" && "border-l-primary",
        style.tone === "amber" && "border-l-accent",
        style.tone === "red" && "border-l-danger",
        style.tone === "gray" && "border-l-border-strong",
        settled && "opacity-80",
      )}
    >
      <div className="flex items-center gap-2">
        <span className="rounded-[var(--radius-sm)] bg-surface-3 px-1.5 py-0.5 font-mono text-[11px] font-medium text-muted-foreground">
          {shortKey(task.key)}
        </span>
        <PhaseBadge task={task} />
      </div>

      <Link to={target} className="text-[13.5px] font-semibold leading-snug text-foreground hover:text-primary hover:underline">
        {task.title}
      </Link>

      {task.waitingFor.length > 0 && (
        <p className="text-[12px] text-muted-foreground">
          {t("goals.board.waitingFor", {
            keys: task.waitingFor.map((key) => `${shortKey(key)} ${byKey.get(key)?.title ?? ""}`.trim()).join(", "),
          })}
        </p>
      )}
      {task.note && <p className="text-[12px] leading-snug text-accent">{srv(task.note)}</p>}
      {task.errorMessage && <p className="line-clamp-3 text-[12px] leading-snug text-danger">{task.errorMessage}</p>}

      {(task.pullRequestNumber || task.impactedFileCount > 0) && (
        <div className="mt-auto flex flex-wrap items-center gap-3 pt-0.5 text-[11.5px] text-subtle-foreground">
          {task.pullRequestNumber && task.pullRequestUrl && (
            <a
              href={task.pullRequestUrl}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1 font-medium text-primary hover:underline"
            >
              <GitPullRequest className="h-3 w-3" />
              {t("goals.board.pullRequest", { n: task.pullRequestNumber })}
              <ExternalLink className="h-3 w-3" />
            </a>
          )}
          {task.impactedFileCount > 0 && <span>{t("goals.board.filesCount", { count: task.impactedFileCount })}</span>}
        </div>
      )}
    </Panel>
  )
}
