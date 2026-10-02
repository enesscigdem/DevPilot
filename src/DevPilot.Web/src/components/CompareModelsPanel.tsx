import { useEffect, useState } from "react"
import { Link, useNavigate } from "react-router-dom"
import { useTranslation } from "react-i18next"
import { AlertCircle, GitCompareArrows, Loader2 } from "lucide-react"
import { getAiModels, getModelComparisonsForTask, startModelComparison } from "@/api"
import { fmt } from "@/i18n"
import { Badge, Button } from "@/components/ui/primitives"
import type { AiModel, ModelComparison } from "@/types"

const MIN_MODELS = 2
const MAX_MODELS = 3

/** Starts a model comparison for a task whose plan is approved, and lists earlier ones. */
export function CompareModelsPanel({ taskId }: { taskId: string }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [models, setModels] = useState<AiModel[] | null>(null)
  const [previous, setPrevious] = useState<ModelComparison[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    void Promise.all([getAiModels(), getModelComparisonsForTask(taskId, { signal: controller.signal })])
      .then(([list, comparisons]) => {
        if (controller.signal.aborted) return
        setModels(list.filter((m) => m.isEnabled))
        setPrevious(comparisons)
      })
      .catch(() => {
        if (!controller.signal.aborted) setModels([])
      })
    return () => controller.abort()
  }, [taskId])

  const toggle = (id: string) =>
    setSelected((current) =>
      current.includes(id) ? current.filter((x) => x !== id) : current.length < MAX_MODELS ? [...current, id] : current,
    )

  const start = async () => {
    setStarting(true)
    setError(null)
    try {
      const comparison = await startModelComparison(taskId, selected)
      navigate(`/comparisons/${comparison.id}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : t("modelCompare.panel.errStart"))
      setStarting(false)
    }
  }

  if (models === null) return null

  return (
    <div className="space-y-3 border-t border-border pt-4">
      <div className="flex items-center gap-2">
        <GitCompareArrows className="h-4 w-4 text-subtle-foreground" strokeWidth={2} />
        <div className="text-[13px] font-semibold text-foreground">{t("modelCompare.panel.title")}</div>
      </div>
      <p className="text-[12px] leading-relaxed text-muted-foreground">{t("modelCompare.panel.description")}</p>

      {models.length < MIN_MODELS ? (
        <div className="space-y-2">
          <p className="text-[12px] text-subtle-foreground">{t("modelCompare.panel.needModels")}</p>
          <Link to="/models" className="text-[12px] font-medium text-primary hover:underline">
            {t("modelCompare.panel.manageModels")}
          </Link>
        </div>
      ) : (
        <>
          <div className="tech-label">{t("modelCompare.panel.pick")}</div>
          <div className="space-y-1.5">
            {models.map((m) => {
              const checked = selected.includes(m.id)
              const order = selected.indexOf(m.id) + 1
              return (
                <label
                  key={m.id}
                  className="flex cursor-pointer items-center gap-2 rounded-[var(--radius-md)] border border-border px-2.5 py-2 text-[12.5px] hover:bg-surface-3"
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => toggle(m.id)}
                    className="h-4 w-4 rounded border-border-strong accent-primary"
                  />
                  <span className="min-w-0 flex-1 truncate text-foreground">{m.name}</span>
                  {checked && <Badge mono>{order}</Badge>}
                </label>
              )
            })}
          </div>
          <p className="text-[11.5px] text-subtle-foreground">{t("modelCompare.panel.cost")}</p>

          {error && (
            <div className="flex items-start gap-1.5 text-[12px] font-medium text-danger">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <span className="break-words">{error}</span>
            </div>
          )}

          <Button
            variant="default"
            className="w-full"
            disabled={starting || selected.length < MIN_MODELS}
            onClick={() => void start()}
          >
            {starting ? <Loader2 className="h-4 w-4 animate-spin" /> : <GitCompareArrows className="h-4 w-4" />}
            {starting ? t("modelCompare.panel.starting") : t("modelCompare.panel.start")}
          </Button>
        </>
      )}

      {previous.length > 0 && (
        <div className="space-y-1.5 pt-1">
          <div className="tech-label">{t("modelCompare.panel.previous")}</div>
          {previous.map((c) => (
            <Link
              key={c.id}
              to={`/comparisons/${c.id}`}
              className="flex items-center justify-between gap-2 rounded-[var(--radius-md)] px-2 py-1.5 text-[12px] hover:bg-surface-3"
            >
              <span className="truncate text-muted-foreground">
                {fmt.dateTime(c.createdAt)} · {t("modelCompare.panel.modelsCount", { count: c.runs.length })}
              </span>
              <Badge tone={c.status === "Running" ? "blue" : c.status === "Completed" ? "green" : "gray"}>
                {t(`modelCompare.status.${c.status}`)}
              </Badge>
            </Link>
          ))}
        </div>
      )}
    </div>
  )
}
