import type { ReactNode } from "react"
import { Link } from "react-router-dom"
import { ArrowUpRight } from "lucide-react"
import { cn } from "@/lib/utils"
import { Badge, StatusDot } from "@/components/ui/primitives"
import { statusMeta, riskMeta, type Task } from "@/data/mock"

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

export function TaskRow({ task }: { task: Task }) {
  const s = statusMeta[task.status]
  const r = riskMeta[task.risk]
  return (
    <Link
      to={`/tasks/${task.id}`}
      className="group flex items-center gap-3 border-b border-border px-3.5 py-3 transition-colors last:border-b-0 hover:bg-surface-2"
    >
      <StatusDot tone={s.tone} pulse={task.status === "executing"} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="font-mono text-[11px] text-subtle-foreground">{task.id}</span>
          <span className="truncate text-[13px] font-medium text-foreground">{task.title}</span>
        </div>
        <div className="mt-0.5 flex items-center gap-2 font-mono text-[11px] text-subtle-foreground">
          <span className="truncate">{task.branch}</span>
          <span>·</span>
          <span>{task.filesTouched} files</span>
          <span>·</span>
          <span>{task.updated}</span>
        </div>
      </div>
      <Badge tone={s.tone}>{s.label}</Badge>
      <Badge tone={r.tone} className="hidden md:inline-flex">
        {r.label}
      </Badge>
      <ArrowUpRight className="h-4 w-4 shrink-0 text-subtle-foreground opacity-0 transition-opacity group-hover:opacity-100" />
    </Link>
  )
}
