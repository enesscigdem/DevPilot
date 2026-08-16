import type { ReactNode } from "react"
import { Link } from "react-router-dom"
import { ArrowUpRight } from "lucide-react"
import { cn } from "@/lib/utils"
import { Badge, StatusDot } from "@/components/ui/primitives"
import { statusMeta, riskMeta, type Task as MockTask, type TaskStatus as MockTaskStatus, type RiskLevel, type Tone } from "@/data/mock"
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
  if (!dateString) return 'just now'
  const date = new Date(dateString)
  if (isNaN(date.getTime())) return dateString

  const now = new Date()
  const diffMs = now.getTime() - date.getTime()
  const diffSec = Math.floor(diffMs / 1000)
  const diffMin = Math.floor(diffSec / 60)
  const diffHours = Math.floor(diffMin / 60)
  const diffDays = Math.floor(diffHours / 24)

  if (diffSec < 60) return 'just now'
  if (diffMin < 60) return `${diffMin} min ago`
  if (diffHours < 24) return `${diffHours} hour${diffHours > 1 ? 's' : ''} ago`
  if (diffDays === 1) return 'yesterday'
  if (diffDays < 7) return `${diffDays} days ago`
  return date.toLocaleDateString()
}

export function TaskRow({ task }: { task: MockTask | TaskListItem | ApiTask | any }) {
  const isReal = typeof task.status === "number"

  let statusTone: Tone = "gray"
  let statusLabel = ""
  let isExecuting = false
  let displayId = task.id
  let title = task.title
  let metaLeft = ""
  let updatedText = ""
  let riskTone: Tone = "neutral"
  let riskLabel = ""

  if (isReal) {
    displayId = task.id.length > 12 ? `TASK-${task.id.slice(0, 8)}` : task.id
    metaLeft = task.repositoryName || task.repositoryWorkspaceName || "master"
    updatedText = formatRelativeTime(task.updatedAt)

    switch (task.status) {
      case TaskStatus.Draft:
        statusTone = "gray"; statusLabel = "Draft"; break;
      case TaskStatus.ReadyForAnalysis:
        statusTone = "neutral"; statusLabel = "Ready for Analysis"; break;
      case TaskStatus.Analyzing:
        statusTone = "blue"; statusLabel = "Analyzing"; isExecuting = true; break;
      case TaskStatus.AwaitingApproval:
        statusTone = "amber"; statusLabel = "Awaiting approval"; break;
      case TaskStatus.Approved:
        statusTone = "blue"; statusLabel = "Approved"; break;
      case TaskStatus.Executing:
        statusTone = "blue"; statusLabel = "Executing"; isExecuting = true; break;
      case TaskStatus.Completed:
        statusTone = "green"; statusLabel = "Merged"; break;
      case TaskStatus.Failed:
        statusTone = "red"; statusLabel = "Failed"; break;
      case TaskStatus.Rejected:
        statusTone = "red"; statusLabel = "Rejected"; break;
      default:
        statusTone = "gray"; statusLabel = "Unknown";
    }

    switch (task.priority) {
      case TaskPriority.Low:
        riskTone = "green"; riskLabel = "Low priority"; break;
      case TaskPriority.Medium:
        riskTone = "amber"; riskLabel = "Medium priority"; break;
      case TaskPriority.High:
      case TaskPriority.Critical:
        riskTone = "red"; riskLabel = "High priority"; break;
      default:
        riskTone = "neutral"; riskLabel = "Normal";
    }
  } else {
    const s = statusMeta[task.status as MockTaskStatus] || { label: String(task.status), tone: "neutral" }
    const r = riskMeta[task.risk as RiskLevel] || { label: String(task.risk), tone: "neutral" }
    statusTone = s.tone
    statusLabel = s.label
    isExecuting = task.status === "executing"
    displayId = task.id
    metaLeft = task.branch
    updatedText = task.updated
    riskTone = r.tone
    riskLabel = r.label
  }

  return (
    <Link
      to={`/tasks/${task.id}`}
      className="group flex items-center gap-3 border-b border-border px-3.5 py-3 transition-colors last:border-b-0 hover:bg-surface-2"
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
          {task.filesTouched !== undefined && (
            <>
              <span>{task.filesTouched} files</span>
              <span>·</span>
            </>
          )}
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
