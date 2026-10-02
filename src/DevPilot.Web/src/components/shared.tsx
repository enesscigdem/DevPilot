import type { ReactNode } from "react"
import { Link } from "react-router-dom"
import i18n, { fmt } from "@/i18n"
import { ArrowUpRight } from "lucide-react"
import { useTranslation } from "react-i18next"
import { cn } from "@/lib/utils"
import { Badge, StatusDot, type Tone } from "@/components/ui/primitives"
import { TaskStatus, TaskPriority, type TaskListItem, type Task as ApiTask } from "@/types"

export function PageContainer({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("mx-auto w-full max-w-[1360px] px-6 py-6", className)}>{children}</div>
}

export function PageHeading({
  eyebrow,
  title,
  description,
  actions,
}: {
  eyebrow?: string
  title: string
  description?: string
  actions?: ReactNode
}) {
  return (
    <div className="mb-6 flex items-start justify-between gap-4">
      <div>
        {eyebrow && <div className="tech-label mb-1.5">{eyebrow}</div>}
        <h1 className="text-[22px] font-semibold tracking-tight text-foreground text-balance">{title}</h1>
        {description && <p className="mt-1.5 max-w-2xl text-[13.5px] leading-relaxed text-muted-foreground text-pretty">{description}</p>}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
    </div>
  )
}

export function SectionHead({
  title,
  count,
  action,
  className,
}: {
  title: string
  count?: number | string
  action?: ReactNode
  className?: string
}) {
  return (
    <div className={cn("mb-3 flex items-center gap-2.5", className)}>
      <h2 className="text-[13px] font-semibold tracking-tight text-foreground">{title}</h2>
      {count !== undefined && (
        <span className="rounded-full bg-surface-3 px-1.5 py-0.5 font-mono text-[11px] font-medium text-muted-foreground">
          {count}
        </span>
      )}
      {action && <div className="ml-auto">{action}</div>}
    </div>
  )
}

function formatRelativeTime(dateString?: string): string {
  if (!dateString) return i18n.t("common.justNow")
  const date = new Date(dateString)
  if (isNaN(date.getTime())) return dateString

  const diffSec = Math.floor((Date.now() - date.getTime()) / 1000)
  const diffMin = Math.floor(diffSec / 60)
  const diffHours = Math.floor(diffMin / 60)
  const diffDays = Math.floor(diffHours / 24)

  if (diffSec < 60) return i18n.t("common.justNow")
  if (diffMin < 60) return i18n.t("shared.relative.minAgo", { n: diffMin })
  if (diffHours < 24) return i18n.t("shared.relative.hoursAgo", { count: diffHours })
  if (diffDays === 1) return i18n.t("shared.relative.yesterday")
  if (diffDays < 7) return i18n.t("shared.relative.daysAgo", { n: diffDays })
  return fmt.date(date)
}

export function TaskRow({ task }: { task: TaskListItem | ApiTask }) {
  useTranslation()
  let statusTone: Tone = "gray"
  let statusLabel = ""
  let isExecuting = false
  let displayId = task.id
  let title = task.title
  let metaLeft = ""
  let updatedText = ""
  let riskTone: Tone = "neutral"
  let riskLabel = ""

  {
    displayId = task.id.length > 12 ? `TASK-${task.id.slice(0, 8)}` : task.id
    metaLeft = task.repositoryName || "master"
    updatedText = formatRelativeTime(task.updatedAt)

    switch (task.status) {
      case TaskStatus.Draft:
        statusTone = "gray"; statusLabel = i18n.t("shared.taskStatus.draft"); break;
      case TaskStatus.ReadyForAnalysis:
        statusTone = "neutral"; statusLabel = i18n.t("shared.taskStatus.readyForAnalysis"); break;
      case TaskStatus.Analyzing:
        statusTone = "blue"; statusLabel = i18n.t("shared.taskStatus.analyzing"); isExecuting = true; break;
      case TaskStatus.AwaitingApproval:
        statusTone = "amber"; statusLabel = i18n.t("shared.taskStatus.awaitingApproval"); break;
      case TaskStatus.Approved:
        statusTone = "blue"; statusLabel = i18n.t("shared.taskStatus.approved"); break;
      case TaskStatus.Executing:
        statusTone = "blue"; statusLabel = i18n.t("shared.taskStatus.executing"); isExecuting = true; break;
      case TaskStatus.Completed:
        statusTone = "green"; statusLabel = i18n.t("shared.taskStatus.merged"); break;
      case TaskStatus.Failed:
        statusTone = "red"; statusLabel = i18n.t("shared.taskStatus.failed"); break;
      case TaskStatus.Rejected:
        statusTone = "red"; statusLabel = i18n.t("shared.taskStatus.rejected"); break;
      default:
        statusTone = "gray"; statusLabel = i18n.t("shared.taskStatus.unknown");
    }

    switch (task.priority) {
      case TaskPriority.Low:
        riskTone = "green"; riskLabel = i18n.t("shared.priority.low"); break;
      case TaskPriority.Medium:
        riskTone = "amber"; riskLabel = i18n.t("shared.priority.medium"); break;
      case TaskPriority.High:
      case TaskPriority.Critical:
        riskTone = "red"; riskLabel = i18n.t("shared.priority.high"); break;
      default:
        riskTone = "neutral"; riskLabel = i18n.t("shared.priority.normal");
    }
  }

  return (
    <Link
      to={`/tasks/${task.id}`}
      className="group flex items-center gap-3 border-b border-border px-3.5 py-3 transition-colors last:border-b-0 hover:bg-surface-3"
    >
      <StatusDot tone={statusTone} pulse={isExecuting} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="font-mono text-[11px] text-subtle-foreground">{displayId}</span>
          <span className="truncate text-[13px] font-medium text-foreground">{title}</span>
        </div>
        <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
          <span className="truncate">{metaLeft}</span>
          <span>·</span>
          <span>{updatedText}</span>
        </div>
      </div>
      <Badge tone={statusTone}>{statusLabel}</Badge>
      {riskLabel && (
        <Badge tone={riskTone} className="hidden md:inline-flex">
          {riskLabel}
        </Badge>
      )}
      <ArrowUpRight className="h-4 w-4 shrink-0 text-subtle-foreground opacity-0 transition-opacity group-hover:opacity-100" />
    </Link>
  )
}
