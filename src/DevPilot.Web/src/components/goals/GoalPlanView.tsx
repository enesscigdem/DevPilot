import { useMemo, useState } from "react"
import { AlertCircle, ArrowLeft, Check, GitBranch, Loader2, Pencil, Rocket, Trash2, TriangleAlert } from "lucide-react"
import { useTranslation } from "react-i18next"
import { arrangeGoal, startGoal } from "@/api"
import { AutomationBanner } from "@/components/goals/AutomationBanner"
import { Badge, Button, Panel } from "@/components/ui/primitives"
import { formatTokens, formatUsd, shortKey } from "@/lib/goals"
import { cn } from "@/lib/utils"
import type { GoalPlan, GoalTaskPlan } from "@/types"

const fieldClass =
  "w-full rounded-[var(--radius-md)] border border-border-strong bg-surface px-3 py-2 text-[13px] text-foreground outline-none placeholder:text-subtle-foreground focus:border-primary focus:ring-2 focus:ring-ring/30"

interface Props {
  workspaceId: string
  goalText: string
  plan: GoalPlan
  onPlanChange: (plan: GoalPlan) => void
  onBack: () => void
  onStarted: (goalId: string) => void
}

export function GoalPlanView({ workspaceId, goalText, plan, onPlanChange, onBack, onStarted }: Props) {
  const { t } = useTranslation()
  const [editing, setEditing] = useState<string | null>(null)
  const [arranging, setArranging] = useState(false)
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const byKey = useMemo(() => new Map(plan.tasks.map((task) => [task.key, task])), [plan.tasks])
  const blockers = useMemo(() => {
    const map = new Map<string, { keys: string[]; file: string }>()
    for (const c of plan.conflicts) {
      const current = map.get(c.secondKey)
      map.set(c.secondKey, { keys: [...(current?.keys ?? []), c.firstKey], file: current?.file ?? c.shared[0] ?? "" })
    }
    return map
  }, [plan.conflicts])

  const update = (key: string, patch: Partial<GoalTaskPlan>) =>
    onPlanChange({ ...plan, tasks: plan.tasks.map((task) => (task.key === key ? { ...task, ...patch } : task)) })

  // Removing a task can free others to run earlier, which only the server can work out exactly.
  const remove = async (key: string) => {
    const rest = plan.tasks.filter((task) => task.key !== key)
    if (rest.length === 0) {
      onBack()
      return
    }
    setArranging(true)
    setError(null)
    try {
      onPlanChange({ ...(await arrangeGoal(workspaceId, rest, plan.estimate)), source: plan.source, planningTokens: plan.planningTokens })
      setEditing(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : t("goals.errArrange"))
    } finally {
      setArranging(false)
    }
  }

  const start = async () => {
    setStarting(true)
    setError(null)
    try {
      const goal = await startGoal(workspaceId, {
        text: goalText,
        planSource: plan.source,
        tasks: plan.tasks.map(({ key, title, description, areas, dependsOn, size, areasGuessed }) => ({
          key,
          title: title.trim(),
          description,
          areas,
          dependsOn,
          size,
          areasGuessed,
        })),
        estimatedInputTokens: plan.estimate.inputTokens,
        estimatedOutputTokens: plan.estimate.outputTokens,
        estimatedUsd: plan.estimate.usd,
      })
      onStarted(goal.id)
    } catch (err) {
      setError(err instanceof Error ? err.message : t("goals.errStart"))
      setStarting(false)
    }
  }

  const untitled = plan.tasks.some((task) => !task.title.trim())
  const { estimate } = plan

  return (
    <div className="space-y-6 pb-28">
      <button
        type="button"
        onClick={onBack}
        className="inline-flex cursor-pointer items-center gap-1.5 text-[12.5px] font-medium text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft className="h-3.5 w-3.5" /> {t("goals.plan.back")}
      </button>

      {/* Summary: what will happen and roughly what it costs, before anything is created. */}
      <Panel className="grid gap-px overflow-hidden bg-border sm:grid-cols-3">
        <Stat label={t("goals.plan.summary")}>
          <span className="text-[22px] font-semibold tracking-tight text-foreground">{t("goals.plan.tasks", { count: plan.tasks.length })}</span>
          <span className="text-[12.5px] text-muted-foreground">{t("goals.plan.waves", { count: plan.waves.length })}</span>
        </Stat>
        <Stat label={t("goals.plan.estimate")}>
          <span className="text-[22px] font-semibold tracking-tight text-foreground">
            {formatTokens(estimate.inputTokens + estimate.outputTokens)}
          </span>
          <span className="font-mono text-[11.5px] text-muted-foreground">
            {t("goals.plan.tokens", { input: formatTokens(estimate.inputTokens), output: formatTokens(estimate.outputTokens) })}
          </span>
        </Stat>
        <Stat label={t("goals.plan.cost")}>
          {estimate.usd !== null ? (
            <span className="text-[22px] font-semibold tracking-tight text-foreground">{formatUsd(estimate.usd)}</span>
          ) : (
            <span className="text-[12.5px] leading-snug text-muted-foreground">{t("goals.plan.costUnknown")}</span>
          )}
          <span className="text-[11.5px] text-subtle-foreground">
            {estimate.basis === "history"
              ? t("goals.plan.basisHistory", { count: estimate.samples })
              : t("goals.plan.basisDefault")}
          </span>
        </Stat>
      </Panel>

      {(plan.source === "parser" || plan.planningTokens > 0 || plan.warnings.length > 0) && (
        <div className="flex flex-wrap items-center gap-2 text-[12px] text-subtle-foreground">
          {plan.planningTokens > 0 && <span>{t("goals.plan.planningUsed", { tokens: formatTokens(plan.planningTokens) })}</span>}
          {plan.source === "parser" && <span>{t("goals.plan.sourceParser")}</span>}
          {[...new Set(plan.warnings.filter((w) => w.taskKey === null || w.code === "AiUnavailable").map((w) => w.code))].map((code) => (
            <Badge key={code} tone="amber">
              <TriangleAlert className="h-3 w-3" /> {t(`goals.warn.${code}`)}
            </Badge>
          ))}
        </div>
      )}

      {error && (
        <div className="flex items-center gap-1.5 text-[12.5px] font-medium text-danger">
          <AlertCircle className="h-3.5 w-3.5" /> {error}
        </div>
      )}

      {/* Waves: a timeline of what starts together and what waits for what. */}
      <ol className={cn("relative space-y-7 transition-opacity", arranging && "opacity-60")}>
        {plan.waves.map((wave, index) => (
          <li key={wave.number} className="relative pl-11">
            {index < plan.waves.length - 1 && <span className="absolute left-[15px] top-9 -bottom-7 w-px bg-border-strong" aria-hidden />}
            <span className="absolute left-0 top-0 flex h-[31px] w-[31px] items-center justify-center rounded-full border border-primary-ring bg-primary-soft font-mono text-[12px] font-semibold text-primary">
              {wave.number}
            </span>
            <div className="mb-3 min-h-[31px]">
              <div className="flex flex-wrap items-baseline gap-x-2">
                <h3 className="text-[14px] font-semibold text-foreground">{t("goals.plan.waveTitle", { n: wave.number })}</h3>
                <span className="text-[12.5px] text-muted-foreground">
                  {t("goals.plan.waveRunsTogether", { count: wave.taskKeys.length })}
                </span>
              </div>
              <div className="text-[12px] text-subtle-foreground">{index === 0 ? t("goals.plan.waveFirst") : t("goals.plan.waveAfter")}</div>
            </div>

            <div className="grid gap-3 lg:grid-cols-2 2xl:grid-cols-3">
              {wave.taskKeys.map((key) => {
                const task = byKey.get(key)
                if (!task) return null
                return (
                  <PlanTaskCard
                    key={key}
                    task={task}
                    waitsFor={blockers.get(key)?.keys ?? []}
                    sharedFile={blockers.get(key)?.file ?? ""}
                    editing={editing === key}
                    disabled={arranging}
                    onEdit={() => setEditing(editing === key ? null : key)}
                    onChange={(patch) => update(key, patch)}
                    onRemove={() => void remove(key)}
                  />
                )
              })}
            </div>
          </li>
        ))}
      </ol>

      {/* The start bar stays in view however long the plan is. */}
      <div className="sticky bottom-4 z-10 space-y-3">
        <AutomationBanner workspaceId={workspaceId} />
        <Panel className="flex flex-wrap items-center gap-3 px-4 py-3 shadow-[var(--shadow-lg)]">
          <div className="min-w-[200px] flex-1">
            <div className="text-[13px] font-semibold text-foreground">{t("goals.plan.startBar")}</div>
            <div className="text-[12px] text-muted-foreground">{t("goals.plan.startNote")}</div>
          </div>
          <Button variant="primary" size="lg" onClick={() => void start()} disabled={starting || arranging || untitled || plan.tasks.length === 0}>
            {starting ? (
              <>
                <Loader2 className="h-4 w-4 animate-spin" /> {t("goals.plan.starting")}
              </>
            ) : (
              <>
                <Rocket className="h-4 w-4" /> {t("goals.plan.start", { count: plan.tasks.length })}
              </>
            )}
          </Button>
        </Panel>
      </div>
    </div>
  )
}

function Stat({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1 bg-surface px-5 py-4">
      <div className="tech-label">{label}</div>
      {children}
    </div>
  )
}

function PlanTaskCard({
  task,
  waitsFor,
  sharedFile,
  editing,
  disabled,
  onEdit,
  onChange,
  onRemove,
}: {
  task: GoalTaskPlan
  waitsFor: string[]
  sharedFile: string
  editing: boolean
  disabled: boolean
  onEdit: () => void
  onChange: (patch: Partial<GoalTaskPlan>) => void
  onRemove: () => void
}) {
  const { t } = useTranslation()
  const shownAreas = task.areas.slice(0, 3)

  return (
    <Panel className={cn("flex flex-col gap-2.5 p-3.5 transition-colors", editing && "border-primary-ring ring-2 ring-ring/15")}>
      <div className="flex items-center gap-2">
        <span className="rounded-[var(--radius-sm)] bg-surface-3 px-1.5 py-0.5 font-mono text-[11px] font-medium text-muted-foreground">
          {shortKey(task.key)}
        </span>
        <Badge tone={task.size === "large" ? "amber" : "gray"}>{t(`goals.plan.size.${task.size}`)}</Badge>
        <span className="ml-auto flex items-center gap-0.5">
          <button
            type="button"
            onClick={onEdit}
            disabled={disabled}
            aria-label={editing ? t("goals.plan.done") : t("goals.plan.edit")}
            title={editing ? t("goals.plan.done") : t("goals.plan.edit")}
            className="cursor-pointer rounded-[var(--radius-md)] p-1.5 text-muted-foreground hover:bg-surface-3 hover:text-foreground disabled:opacity-40"
          >
            {editing ? <Check className="h-3.5 w-3.5" /> : <Pencil className="h-3.5 w-3.5" />}
          </button>
          <button
            type="button"
            onClick={onRemove}
            disabled={disabled}
            aria-label={t("goals.plan.remove")}
            title={t("goals.plan.remove")}
            className="cursor-pointer rounded-[var(--radius-md)] p-1.5 text-muted-foreground hover:bg-danger-soft hover:text-danger disabled:opacity-40"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </button>
        </span>
      </div>

      {editing ? (
        <div className="space-y-2">
          <input
            value={task.title}
            onChange={(e) => onChange({ title: e.target.value })}
            maxLength={200}
            aria-label={t("goals.plan.titleLabel")}
            placeholder={t("goals.plan.titleLabel")}
            className={cn(fieldClass, "font-medium", !task.title.trim() && "border-danger")}
          />
          <textarea
            value={task.description}
            onChange={(e) => onChange({ description: e.target.value })}
            rows={5}
            aria-label={t("goals.plan.descriptionLabel")}
            placeholder={t("goals.plan.descriptionLabel")}
            className={cn(fieldClass, "resize-y")}
          />
        </div>
      ) : (
        <>
          <h4 className="text-[13.5px] font-semibold leading-snug text-foreground">{task.title}</h4>
          {task.description ? (
            <p className="line-clamp-3 text-[12.5px] leading-relaxed text-muted-foreground">{task.description}</p>
          ) : (
            <p className="text-[12px] italic text-subtle-foreground">{t("goals.warn.MissingDescription")}</p>
          )}
        </>
      )}

      <div className="mt-auto flex flex-wrap items-center gap-1.5 pt-0.5">
        {shownAreas.length > 0 ? (
          <>
            {task.areasGuessed && (
              <span className="text-[11px] italic text-subtle-foreground" title={t("goals.plan.guessedHint")}>
                {t("goals.plan.guessed")}
              </span>
            )}
            {shownAreas.map((area) => (
              <span key={area} title={area} className="max-w-[180px] truncate rounded-[var(--radius-sm)] border border-border bg-surface-2 px-1.5 py-0.5 font-mono text-[10.5px] text-muted-foreground">
                {area.split("/").slice(-2).join("/")}
              </span>
            ))}
            {task.areas.length > shownAreas.length && (
              <span className="text-[11px] text-subtle-foreground">{t("goals.plan.areasMore", { count: task.areas.length - shownAreas.length })}</span>
            )}
          </>
        ) : (
          <span className="text-[11.5px] text-subtle-foreground">{t("goals.plan.areasNone")}</span>
        )}
      </div>

      {(waitsFor.length > 0 || task.dependsOn.length > 0) && (
        <div className="flex flex-col gap-1 border-t border-border pt-2 text-[11.5px]">
          {waitsFor.length > 0 && (
            <span className="inline-flex items-center gap-1.5 text-accent" title={t("goals.plan.sameFilesHint")}>
              <GitBranch className="h-3 w-3 shrink-0" />
              {t("goals.plan.sameFiles", { keys: waitsFor.map(shortKey).join(", "), file: sharedFile.split("/").pop() })}
            </span>
          )}
          {task.dependsOn.length > 0 && (
            <span className="text-muted-foreground">{t("goals.plan.needs", { keys: task.dependsOn.map(shortKey).join(", ") })}</span>
          )}
        </div>
      )}
    </Panel>
  )
}
