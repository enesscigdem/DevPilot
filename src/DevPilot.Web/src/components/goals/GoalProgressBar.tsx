import { cn } from "@/lib/utils"
import type { GoalProgress } from "@/types"

/**
 * One bar for the whole goal: how much is merged, how much is waiting as a pull request, how much is being
 * worked on, and what went wrong. The same bar is used on the list and on the board so they always agree.
 */
export function GoalProgressBar({ progress, className }: { progress: GoalProgress; className?: string }) {
  const total = Math.max(1, progress.total)
  const segments = [
    { value: progress.merged, className: "bg-success" },
    { value: progress.pullRequests, className: "bg-primary" },
    { value: progress.running, className: "bg-primary/45 animate-pulse" },
    { value: progress.failed, className: "bg-danger" },
    { value: progress.stopped, className: "bg-subtle-foreground/50" },
  ]

  return (
    <div
      className={cn("flex h-2 w-full gap-px overflow-hidden rounded-full bg-surface-3", className)}
      role="progressbar"
      aria-valuemin={0}
      aria-valuemax={progress.total}
      aria-valuenow={progress.merged}
    >
      {segments
        .filter((s) => s.value > 0)
        .map((s, i) => (
          <div key={i} className={cn("h-full transition-all duration-700", s.className)} style={{ width: `${(s.value / total) * 100}%` }} />
        ))}
    </div>
  )
}
