import { useCallback, useEffect, useState } from "react"
import { Link, useNavigate } from "react-router-dom"
import { AlertCircle, ArrowRight, Loader2, Sparkles } from "lucide-react"
import { useTranslation } from "react-i18next"
import { getGoals, planGoal } from "@/api"
import { GoalPlanView } from "@/components/goals/GoalPlanView"
import { GoalProgressBar } from "@/components/goals/GoalProgressBar"
import { PageContainer, PageHeading, SectionHead } from "@/components/shared"
import { Badge, Button, Kbd, Panel } from "@/components/ui/primitives"
import { relativeTime } from "@/i18n"
import { useWorkspace } from "@/lib/workspace"
import { STATUS_TONE } from "@/lib/goals"
import type { GoalPlan, GoalSummary } from "@/types"

const MAX_LENGTH = 20000

export function Goals() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { activeWorkspace, activeWorkspaceId } = useWorkspace()
  const [text, setText] = useState("")
  const [planning, setPlanning] = useState(false)
  const [plan, setPlan] = useState<GoalPlan | null>(null)
  const [plannedText, setPlannedText] = useState("")
  const [error, setError] = useState<string | null>(null)
  const [recent, setRecent] = useState<GoalSummary[] | null>(null)

  const loadRecent = useCallback(() => {
    if (!activeWorkspaceId) {
      setRecent([])
      return
    }
    getGoals(activeWorkspaceId)
      .then(setRecent)
      .catch(() => setRecent([]))
  }, [activeWorkspaceId])

  useEffect(() => {
    setPlan(null)
    setRecent(null)
    loadRecent()
  }, [loadRecent])

  const makePlan = async () => {
    if (!activeWorkspaceId || !text.trim() || planning) return
    setPlanning(true)
    setError(null)
    try {
      setPlan(await planGoal(activeWorkspaceId, text))
      setPlannedText(text)
    } catch (err) {
      setError(err instanceof Error ? err.message : t("goals.errPlan"))
    } finally {
      setPlanning(false)
    }
  }

  const examples = t("goals.composer.examples", { returnObjects: true }) as unknown as string[]

  return (
    <PageContainer>
      <PageHeading eyebrow={t("goals.eyebrow")} title={t("goals.title")} description={t("goals.description")} />

      {!activeWorkspaceId && (
        <Panel className="px-6 py-10 text-center text-[13px] text-muted-foreground">{t("goals.noWorkspace")}</Panel>
      )}

      {activeWorkspaceId && plan && (
        <GoalPlanView
          workspaceId={activeWorkspaceId}
          goalText={plannedText}
          plan={plan}
          onPlanChange={setPlan}
          onBack={() => setPlan(null)}
          onStarted={(id) => navigate(`/goals/${id}`)}
        />
      )}

      {activeWorkspaceId && !plan && (
        <div className="space-y-10">
          <Panel className="overflow-hidden">
            <div className="p-5">
              <label htmlFor="goal-text" className="mb-2 block text-[13px] font-semibold text-foreground">
                {t("goals.composer.label")}
              </label>
              <textarea
                id="goal-text"
                value={text}
                onChange={(e) => setText(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault()
                    void makePlan()
                  }
                }}
                maxLength={MAX_LENGTH}
                rows={9}
                disabled={planning}
                placeholder={t("goals.composer.placeholder")}
                className="w-full resize-y rounded-[var(--radius-md)] border border-border-strong bg-surface-2 px-4 py-3 text-[14px] leading-relaxed text-foreground outline-none placeholder:text-subtle-foreground focus:border-primary focus:bg-surface focus:ring-2 focus:ring-ring/30 disabled:opacity-60"
              />

              {!text.trim() && (
                <div className="mt-3 flex flex-wrap items-center gap-2">
                  <span className="text-[12px] text-subtle-foreground">{t("goals.composer.examplesLabel")}</span>
                  {examples.map((example) => (
                    <button
                      key={example}
                      type="button"
                      onClick={() => setText(example)}
                      className="max-w-full cursor-pointer truncate rounded-full border border-border-strong px-3 py-1 text-left text-[12px] text-muted-foreground transition-colors hover:border-primary-ring hover:bg-primary-soft hover:text-primary"
                    >
                      {example}
                    </button>
                  ))}
                </div>
              )}
            </div>

            <div className="flex flex-wrap items-center gap-3 border-t border-border bg-surface-2 px-5 py-3">
              {planning ? (
                <div className="flex items-center gap-2.5 text-[13px] text-muted-foreground" role="status">
                  <Loader2 className="h-4 w-4 animate-spin text-primary" />
                  <span>
                    <span className="font-medium text-foreground">{t("goals.composer.planning")}</span>{" "}
                    <span className="text-subtle-foreground">{t("goals.composer.planningHint")}</span>
                  </span>
                </div>
              ) : (
                <>
                  <span className="text-[12px] text-subtle-foreground">{t("goals.composer.hint")}</span>
                  {activeWorkspace && (
                    <Badge tone="neutral" mono>
                      {activeWorkspace.owner}/{activeWorkspace.repository}
                    </Badge>
                  )}
                </>
              )}
              <Button variant="primary" size="lg" className="ml-auto" onClick={() => void makePlan()} disabled={!text.trim() || planning}>
                <Sparkles className="h-4 w-4" />
                {t("goals.composer.plan")}
                <Kbd>Ctrl ↵</Kbd>
              </Button>
            </div>
          </Panel>

          {error && (
            <div className="-mt-6 flex items-start gap-1.5 text-[12.5px] font-medium text-danger">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" /> {error}
            </div>
          )}

          <section>
            <SectionHead title={t("goals.recent.title")} count={recent?.length ?? undefined} />
            {recent === null ? (
              <div className="flex items-center gap-2 text-[13px] text-muted-foreground">
                <Loader2 className="h-4 w-4 animate-spin" /> {t("goals.recent.loading")}
              </div>
            ) : recent.length === 0 ? (
              <p className="text-[13px] text-subtle-foreground">{t("goals.recent.empty")}</p>
            ) : (
              <div className="grid gap-3 lg:grid-cols-2">
                {recent.map((goal) => (
                  <GoalCard key={goal.id} goal={goal} />
                ))}
              </div>
            )}
          </section>
        </div>
      )}
    </PageContainer>
  )
}

function GoalCard({ goal }: { goal: GoalSummary }) {
  const { t } = useTranslation()
  const { progress } = goal
  return (
    <Link to={`/goals/${goal.id}`} className="group block cursor-pointer">
      <Panel className="space-y-3 p-4 transition-colors group-hover:border-primary-ring group-hover:bg-surface-2">
        <div className="flex items-start gap-3">
          <h3 className="min-w-0 flex-1 truncate text-[13.5px] font-semibold text-foreground">{goal.title}</h3>
          <Badge tone={STATUS_TONE[goal.status]}>{t(`goals.board.status${goal.status}`)}</Badge>
        </div>
        <GoalProgressBar progress={progress} />
        <div className="flex items-center justify-between gap-3 text-[12px] text-muted-foreground">
          <span>{t("goals.board.progress", { merged: progress.merged, total: progress.total })}</span>
          <span className="flex items-center gap-1 text-subtle-foreground">
            {relativeTime(goal.createdAt)}
            <ArrowRight className="h-3 w-3 transition-transform group-hover:translate-x-0.5" />
          </span>
        </div>
      </Panel>
    </Link>
  )
}
