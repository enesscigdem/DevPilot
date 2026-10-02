import { useEffect, useState } from "react"
import { Link } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { GitCompareArrows } from "lucide-react"
import { getExecutions } from "@/api"
import { useWorkspace } from "@/lib/workspace"
import { cn } from "@/lib/utils"
import { TaskExecutionStatus } from "@/types"

type TabKey = "run" | "review"

/**
 * Shared Run / Review & delivery navigation for one execution, plus a one-click comparison with the
 * previous attempt of the same task (the retry story).
 */
export function ExecutionTabs({
  executionId,
  taskId,
  active,
  reviewAvailable,
}: {
  executionId: string
  taskId: string
  active: TabKey
  reviewAvailable: boolean
}) {
  const { activeWorkspaceId } = useWorkspace()
  const { t } = useTranslation()
  const [previousAttempt, setPreviousAttempt] = useState<{ id: string; number: number } | null>(null)
  const [attemptNumber, setAttemptNumber] = useState<number | null>(null)

  useEffect(() => {
    if (!taskId) return
    const controller = new AbortController()
    getExecutions(activeWorkspaceId, { signal: controller.signal })
      .then((all) => {
        const attempts = all
          .filter((e) => e.developmentTaskId === taskId)
          .sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime())
        const index = attempts.findIndex((e) => e.id === executionId)
        if (index < 0) return
        setAttemptNumber(index + 1)
        const previous = attempts
          .slice(0, index)
          .reverse()
          .find((e) => e.status !== TaskExecutionStatus.Pending && e.status !== TaskExecutionStatus.Running)
        setPreviousAttempt(previous ? { id: previous.id, number: attempts.indexOf(previous) + 1 } : null)
      })
      .catch(() => undefined)
    return () => controller.abort()
  }, [activeWorkspaceId, executionId, taskId])

  const tabClass = (isActive: boolean, disabled = false) =>
    cn(
      "relative px-3 py-2 text-[12.5px] font-medium transition-colors",
      isActive ? "text-foreground" : "text-muted-foreground hover:text-foreground",
      disabled && "pointer-events-none opacity-40",
    )

  return (
    <div className="flex items-center gap-1 border-b border-border px-6">
      <div className="mx-auto flex w-full max-w-[1600px] items-center gap-1">
        <Link to={`/executions/${executionId}`} className={tabClass(active === "run")}>
          {t("executions.tabs.run")}
          {active === "run" && <span className="absolute inset-x-2 -bottom-px h-[2px] rounded-full bg-primary" />}
        </Link>
        <Link
          to={`/review/${executionId}`}
          aria-disabled={!reviewAvailable}
          title={reviewAvailable ? undefined : t("executions.tabs.reviewLocked")}
          className={tabClass(active === "review", !reviewAvailable)}
        >
          {t("executions.tabs.review")}
          {active === "review" && <span className="absolute inset-x-2 -bottom-px h-[2px] rounded-full bg-primary" />}
        </Link>
        <div className="ml-auto flex items-center gap-3 font-mono text-[11px] text-subtle-foreground">
          {attemptNumber != null && attemptNumber > 1 && <span>{t("executions.tabs.attempt", { n: attemptNumber })}</span>}
          {previousAttempt && (
            <Link
              to={`/executions/compare?a=${previousAttempt.id}&b=${executionId}`}
              className="flex items-center gap-1.5 rounded-[var(--radius-md)] border border-border bg-surface px-2 py-1 font-sans text-[12px] font-medium text-muted-foreground hover:border-border-strong hover:text-foreground"
            >
              <GitCompareArrows className="h-3.5 w-3.5" />
              {t("executions.tabs.compare", { n: previousAttempt.number })}
            </Link>
          )}
        </div>
      </div>
    </div>
  )
}
