import { useEffect, useState } from "react"
import { useTranslation } from "react-i18next"
import {
  AlertTriangle,
  ArrowRight,
  Check,
  ChevronDown,
  ChevronRight,
  CircleDot,
  FileCode2,
  FileMinus2,
  FilePlus2,
  FlaskConical,
  Hammer,
  Loader2,
  MessageSquareWarning,
  Minus,
  X,
} from "lucide-react"
import { getExecutionRevisionDiff } from "@/api"
import { DiffView } from "@/components/DiffView"
import { Badge, Button, Panel, type Tone } from "@/components/ui/primitives"
import { cn } from "@/lib/utils"
import { formatDuration } from "@/lib/duration"
import type {
  ExecutionReviewStageStatus,
  ExecutionRevision,
  ExecutionRevisionDiff,
  ExecutionRevisionFile,
  RevisionStepState,
} from "@/types"

const STATE_TONE: Record<ExecutionRevision["state"], Tone> = {
  Running: "blue",
  Applied: "green",
  NoChange: "amber",
  Failed: "red",
  Cancelled: "gray",
}

interface RevisionPanelProps {
  revision: ExecutionRevision
  executionId: string
  workspaceId?: string | null
  /** Narrow layout for the review sidebar. */
  compact?: boolean
  canRequestChanges?: boolean
  onRequestChanges?: () => void
  onOpenReview?: () => void
}

/**
 * The latest requested fix on its own: what is happening now, which files are being worked on or were changed,
 * what the AI fixed and could not fix, this round's build/test, the diff of the fix and what the user does next.
 * Everything here comes from the backend's revision-scoped data; the first run's numbers never appear in it.
 */
export function RevisionPanel({
  revision,
  executionId,
  workspaceId,
  compact = false,
  canRequestChanges,
  onRequestChanges,
  onOpenReview,
}: RevisionPanelProps) {
  const { t } = useTranslation()
  const running = revision.state === "Running"

  const [nowMs, setNowMs] = useState(() => Date.now())
  useEffect(() => {
    if (!running) return
    const timer = setInterval(() => setNowMs(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [running])

  const elapsedMs = running
    ? Math.max(0, nowMs - new Date(revision.requestedAt).getTime())
    : revision.durationMs ?? null

  return (
    <div className="space-y-4">
      <Panel className="space-y-3 border-primary/30 bg-primary-soft/20 p-4">
        <div className="flex flex-wrap items-center gap-2">
          <MessageSquareWarning className="h-4 w-4 shrink-0 text-primary" />
          <span className="text-[13.5px] font-semibold text-foreground">{t("revision.title")}</span>
          <Badge tone="blue">{t("revision.number", { n: revision.number })}</Badge>
          <Badge tone={STATE_TONE[revision.state]}>{t(`revision.state.${revision.state}`)}</Badge>
          {elapsedMs != null && (
            <span className="ml-auto font-mono text-[11.5px] text-muted-foreground">
              {running ? `${t("revision.elapsed")} ${formatDuration(elapsedMs)}` : t("revision.tookFor", { time: formatDuration(elapsedMs) })}
            </span>
          )}
        </div>
        <blockquote className="whitespace-pre-wrap break-words rounded-[var(--radius-md)] border border-border bg-surface px-3 py-2 text-[12.5px] text-foreground">
          <span className="mb-0.5 block text-[10.5px] font-semibold uppercase tracking-wider text-subtle-foreground">
            {t("revision.yourRequest")}
          </span>
          {revision.feedback}
        </blockquote>
        {running && <NowLine phase={revision.phase} />}
      </Panel>

      {!compact && <Steps steps={revision.steps} />}

      <div className={cn("grid gap-4", !compact && "lg:grid-cols-2")}>
        <FilesCard revision={revision} compact={compact} />
        <div className="space-y-4">
          <OutcomeCard revision={revision} />
          <ChecksCard revision={revision} />
        </div>
      </div>

      {revision.hasDiff && <DiffToggle executionId={executionId} workspaceId={workspaceId} compact={compact} />}

      <NextCard
        revision={revision}
        canRequestChanges={canRequestChanges}
        onRequestChanges={onRequestChanges}
        onOpenReview={onOpenReview}
      />
    </div>
  )
}

/* -------------------------------- Right now -------------------------------- */

function NowLine({ phase }: { phase: string }) {
  const { t } = useTranslation()
  const key = ["prepare", "apply", "build", "test"].includes(phase) ? phase : "prepare"
  return (
    <div className="flex items-center gap-2.5 rounded-[var(--radius-md)] border border-primary/30 bg-surface px-3 py-2.5">
      <Loader2 className="h-4 w-4 shrink-0 animate-spin text-primary" />
      <div className="min-w-0">
        <div className="tech-label">{t("revision.now.title")}</div>
        <div className="text-[13px] font-medium text-foreground">{t(`revision.now.${key}`)}</div>
      </div>
    </div>
  )
}

/* ---------------------------------- Steps ---------------------------------- */

function StepIcon({ state }: { state: RevisionStepState }) {
  const base = "relative z-10 flex h-[20px] w-[20px] shrink-0 items-center justify-center rounded-full border"
  if (state === "done") {
    return (
      <span className={cn(base, "border-success bg-success text-primary-foreground")}>
        <Check className="h-3 w-3" />
      </span>
    )
  }
  if (state === "active") {
    return (
      <span className={cn(base, "border-primary bg-surface")}>
        <CircleDot className="h-3.5 w-3.5 animate-pulse-dot text-primary" />
      </span>
    )
  }
  if (state === "failed") {
    return (
      <span className={cn(base, "border-danger bg-danger text-primary-foreground")}>
        <X className="h-3 w-3" />
      </span>
    )
  }
  if (state === "skipped") {
    return (
      <span className={cn(base, "border-border bg-surface-2 text-subtle-foreground")}>
        <Minus className="h-3 w-3" />
      </span>
    )
  }
  return <span className={cn(base, "border-border bg-surface")} />
}

function Steps({ steps }: { steps: ExecutionRevision["steps"] }) {
  const { t } = useTranslation()
  return (
    <Panel className="p-4">
      <ol className="grid grid-cols-5 gap-2">
        {steps.map((step, i) => (
          <li key={step.key} className="relative flex flex-col items-center gap-1.5 text-center">
            {i < steps.length - 1 && (
              <span
                className={cn(
                  "absolute left-1/2 top-[10px] h-px w-full",
                  step.state === "done" ? "bg-success/50" : "bg-border",
                )}
              />
            )}
            <StepIcon state={step.state} />
            <span
              className={cn(
                "text-[11.5px] font-medium leading-tight",
                step.state === "todo" || step.state === "skipped" ? "text-subtle-foreground" : "text-foreground",
              )}
            >
              {t(`revision.steps.${step.key}`)}
            </span>
            {step.detail && (
              <span className="font-mono text-[10px] text-muted-foreground">{t(`revision.stepDetail.${step.detail}`, { defaultValue: step.detail })}</span>
            )}
          </li>
        ))}
      </ol>
    </Panel>
  )
}

/* ---------------------------------- Files ---------------------------------- */

function FileRow({ file }: { file: ExecutionRevisionFile }) {
  const { t } = useTranslation()
  const Icon = file.state === "Created" ? FilePlus2 : file.state === "Deleted" ? FileMinus2 : FileCode2
  const muted = file.state === "Considered" || file.state === "Unchanged"
  const slash = file.path.lastIndexOf("/")
  const dir = slash >= 0 ? file.path.slice(0, slash + 1) : ""
  const name = slash >= 0 ? file.path.slice(slash + 1) : file.path

  return (
    <li className="flex items-center gap-2 py-1.5">
      <Icon
        className={cn(
          "h-3.5 w-3.5 shrink-0",
          file.state === "Created" ? "text-success" : file.state === "Deleted" ? "text-danger" : muted ? "text-subtle-foreground" : "text-primary",
        )}
      />
      <span className="min-w-0 flex-1 truncate font-mono text-[12px]" title={file.path}>
        <span className="text-subtle-foreground">{dir}</span>
        <span className={muted ? "text-muted-foreground" : "text-foreground"}>{name}</span>
      </span>
      {file.additions != null || file.deletions != null ? (
        <span className="shrink-0 font-mono text-[11px]">
          <span className="text-success">+{file.additions ?? 0}</span> <span className="text-danger">−{file.deletions ?? 0}</span>
        </span>
      ) : (
        <span className="shrink-0 text-[10.5px] text-muted-foreground">{t(`revision.files.${file.state}`)}</span>
      )}
      {file.additions != null && muted && (
        <span className="shrink-0 text-[10.5px] text-muted-foreground">{t(`revision.files.${file.state}`)}</span>
      )}
    </li>
  )
}

function FilesCard({ revision, compact }: { revision: ExecutionRevision; compact: boolean }) {
  const { t } = useTranslation()
  const changed = revision.files.filter((f) => f.state !== "Considered" && f.state !== "Unchanged")
  const others = revision.files.filter((f) => f.state === "Considered" || f.state === "Unchanged")
  const running = revision.state === "Running"
  const limit = compact ? 8 : 40

  return (
    <Panel className="space-y-2 p-4">
      <div className="flex items-baseline justify-between gap-2">
        <div className="tech-label">{running ? t("revision.files.live") : t("revision.files.title")}</div>
        {revision.filesAreFinal && changed.length > 0 && (
          <span className="font-mono text-[11px] text-muted-foreground">
            {t("revision.files.summary", { count: changed.length })} ·{" "}
            <span className="text-success">+{revision.additions}</span> <span className="text-danger">−{revision.deletions}</span>
          </span>
        )}
      </div>

      {revision.files.length === 0 ? (
        <p className="py-3 text-[12px] text-muted-foreground">
          {running ? t("revision.files.waiting") : t("revision.files.none")}
        </p>
      ) : (
        <ul className="divide-y divide-border/50">
          {[...changed, ...others].slice(0, limit).map((file) => (
            <FileRow key={file.path} file={file} />
          ))}
        </ul>
      )}
      {revision.files.length > limit && (
        <p className="text-[11px] text-muted-foreground">+{revision.files.length - limit}</p>
      )}
      <p className="text-[10.5px] text-subtle-foreground">
        {revision.filesAreFinal ? t("revision.files.finalNote") : running ? t("revision.files.liveNote") : ""}
      </p>
    </Panel>
  )
}

/* --------------------------- What the AI did / not -------------------------- */

function OutcomeCard({ revision }: { revision: ExecutionRevision }) {
  const { t } = useTranslation()
  const { state } = revision

  if (state === "Running" && !revision.summary && !revision.unresolved) return null

  const failed = state === "Failed" || state === "Cancelled"
  const noChange = state === "NoChange"
  const fixedText =
    revision.summary ?? (state === "Applied" ? t("revision.outcome.noSummary") : null)
  const problemText = revision.unresolved ?? (failed || noChange ? revision.result : null)

  return (
    <Panel className="space-y-2.5 p-4">
      <div className="tech-label">{t("revision.outcome.title")}</div>
      {fixedText && (
        <div className="flex items-start gap-2">
          <Check className="mt-0.5 h-3.5 w-3.5 shrink-0 text-success" />
          <div>
            <div className="text-[11px] font-semibold text-success">{t("revision.outcome.changed")}</div>
            <p className="whitespace-pre-wrap break-words text-[12.5px] text-foreground">{fixedText}</p>
          </div>
        </div>
      )}
      {problemText && (
        <div className="flex items-start gap-2 rounded-[var(--radius-md)] border border-amber-500/30 bg-amber-500/10 p-2.5">
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-600 dark:text-amber-400" />
          <div>
            <div className="text-[11px] font-semibold text-amber-600 dark:text-amber-400">
              {failed ? t("revision.outcome.failedTitle") : noChange ? t("revision.outcome.noChangeTitle") : t("revision.outcome.couldNot")}
            </div>
            <p className="whitespace-pre-wrap break-words text-[12.5px] text-foreground">{problemText}</p>
          </div>
        </div>
      )}
      {(failed || noChange) && <p className="text-[11.5px] text-muted-foreground">{t("revision.outcome.previousKept")}</p>}
    </Panel>
  )
}

/* ---------------------------- Build and test (this) --------------------------- */

function CheckCell({
  icon: Icon,
  label,
  stage,
  running,
}: {
  icon: typeof Hammer
  label: string
  stage?: ExecutionReviewStageStatus | null
  running: boolean
}) {
  const { t } = useTranslation()
  const status = stage?.status as string | undefined
  let tone: Tone = "gray"
  let text = running ? t("revision.checks.notYet") : t("revision.checks.unknown")
  if (status === "Passed") {
    tone = "green"
    text = t("revision.checks.passed")
  } else if (status === "NoNewRegressions") {
    tone = "amber"
    text = t("revision.checks.noNew")
  } else if (status === "Failed") {
    tone = "red"
    text = t("revision.checks.failed")
  } else if (status === "Running") {
    tone = "blue"
    text = t("revision.checks.running")
  }

  return (
    <div className="flex items-center justify-between gap-2 rounded-[var(--radius-md)] border border-border bg-surface px-3 py-2">
      <span className="flex items-center gap-2 text-[12.5px] font-medium text-foreground">
        <Icon className="h-3.5 w-3.5 text-subtle-foreground" />
        {label}
      </span>
      <Badge tone={tone}>{text}</Badge>
    </div>
  )
}

function ChecksCard({ revision }: { revision: ExecutionRevision }) {
  const { t } = useTranslation()
  const running = revision.state === "Running"
  const notRun = !running && !revision.build && !revision.test

  return (
    <Panel className="space-y-2 p-4">
      <div className="tech-label">{t("revision.checks.title")}</div>
      {notRun ? (
        <p className="text-[12px] text-muted-foreground">{t("revision.checks.notRun")}</p>
      ) : (
        <div className="space-y-1.5">
          <CheckCell icon={Hammer} label={t("revision.checks.build")} stage={revision.build} running={running} />
          <CheckCell icon={FlaskConical} label={t("revision.checks.test")} stage={revision.test} running={running} />
          {revision.test?.detailSummary && !running && (
            <p className="text-[11.5px] text-muted-foreground">{revision.test.detailSummary}</p>
          )}
        </div>
      )}
    </Panel>
  )
}

/* ----------------------------------- Diff ----------------------------------- */

function DiffToggle({ executionId, workspaceId, compact }: { executionId: string; workspaceId?: string | null; compact: boolean }) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const [diff, setDiff] = useState<ExecutionRevisionDiff | null>(null)
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)

  const toggle = async () => {
    const next = !open
    setOpen(next)
    if (next && !diff && !loading) {
      setLoading(true)
      setFailed(false)
      try {
        setDiff(await getExecutionRevisionDiff(executionId, workspaceId))
      } catch {
        setFailed(true)
      } finally {
        setLoading(false)
      }
    }
  }

  return (
    <Panel className="overflow-hidden">
      <button
        type="button"
        onClick={toggle}
        className="flex w-full cursor-pointer items-center gap-2 px-4 py-3 text-left text-[12.5px] font-medium text-foreground hover:bg-surface-2"
        aria-expanded={open}
      >
        {open ? <ChevronDown className="h-4 w-4 text-subtle-foreground" /> : <ChevronRight className="h-4 w-4 text-subtle-foreground" />}
        {open ? t("revision.diff.hide") : t("revision.diff.show")}
      </button>
      {open && (
        <div className="border-t border-border">
          {loading && (
            <div className="flex items-center gap-2 px-4 py-3 text-[12px] text-muted-foreground">
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
              {t("revision.diff.loading")}
            </div>
          )}
          {failed && <div className="px-4 py-3 text-[12px] text-danger">{t("revision.diff.failed")}</div>}
          {diff && (
            <>
              {diff.truncated && <div className="px-4 py-2 text-[11.5px] text-amber-600 dark:text-amber-400">{t("revision.diff.truncated")}</div>}
              <div className={cn("overflow-auto bg-canvas", compact ? "max-h-[420px]" : "max-h-[560px]")}>
                <DiffView diff={diff.diff} />
              </div>
            </>
          )}
        </div>
      )}
    </Panel>
  )
}

/* -------------------------------- Next action -------------------------------- */

function NextCard({
  revision,
  canRequestChanges,
  onRequestChanges,
  onOpenReview,
}: {
  revision: ExecutionRevision
  canRequestChanges?: boolean
  onRequestChanges?: () => void
  onOpenReview?: () => void
}) {
  const { t } = useTranslation()
  const action = revision.nextAction
  const goesToReview = action === "Review" || action === "FixChecks" || action === "Commit" || action === "Push" || action === "OpenPullRequest"
  const asksAgain = action === "RefineFeedback" || action === "FixChecks" || action === "Review"

  return (
    <Panel className={cn("space-y-3 p-4", action === "Wait" ? "" : "border-primary/40")}>
      <div className="tech-label">{t("revision.next.title")}</div>
      <div>
        <div className="text-[13.5px] font-semibold text-foreground">{t(`revision.next.${action}.title`)}</div>
        <p className="mt-0.5 text-[12.5px] text-muted-foreground">{t(`revision.next.${action}.body`)}</p>
      </div>
      {action !== "Wait" && (
        <div className="flex flex-wrap items-center gap-2">
          {goesToReview && onOpenReview && (
            <Button variant={action === "FixChecks" ? "default" : "primary"} size="sm" onClick={onOpenReview}>
              {t("revision.actions.openReview")}
              <ArrowRight className="h-3.5 w-3.5" />
            </Button>
          )}
          {asksAgain && canRequestChanges && onRequestChanges && (
            <Button variant={action === "RefineFeedback" ? "primary" : "default"} size="sm" onClick={onRequestChanges}>
              <MessageSquareWarning className="h-3.5 w-3.5" />
              {action === "RefineFeedback" ? t("revision.actions.requestAgain") : t("revision.actions.requestChanges")}
            </Button>
          )}
        </div>
      )}
    </Panel>
  )
}
