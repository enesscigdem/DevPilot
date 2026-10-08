import { useState } from "react"
import { AlertCircle, CheckCircle2, Loader2, Trash2, X } from "lucide-react"
import { useTranslation } from "react-i18next"
import { createTaskBatch, parseTaskBatch } from "@/api"
import { cn } from "@/lib/utils"
import { Badge, Button, Panel } from "@/components/ui/primitives"
import { TaskPriority, type TaskBatchDraft, type TaskBatchWarning } from "@/types"

const fieldClass =
  "w-full rounded-[var(--radius-md)] border border-border-strong bg-surface px-3 py-2 text-[13px] text-foreground outline-none placeholder:text-subtle-foreground focus:border-primary focus:ring-2 focus:ring-ring/30"

const PRIORITIES = [
  { value: TaskPriority.Low, key: "low" },
  { value: TaskPriority.Medium, key: "medium" },
  { value: TaskPriority.High, key: "high" },
  { value: TaskPriority.Critical, key: "critical" },
] as const

interface Row extends TaskBatchDraft {
  id: number
  error: string | null
}

export function BatchTaskPanel({
  workspaceId,
  onClose,
  onCreated,
}: {
  workspaceId: string | null
  onClose: () => void
  onCreated: () => void
}) {
  const { t } = useTranslation()
  const [text, setText] = useState("")
  const [rows, setRows] = useState<Row[] | null>(null)
  const [warnings, setWarnings] = useState<TaskBatchWarning[]>([])
  const [busy, setBusy] = useState<"parse" | "create" | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [createdCount, setCreatedCount] = useState(0)

  const parse = async () => {
    setBusy("parse")
    setError(null)
    try {
      const result = await parseTaskBatch(text)
      setRows(result.drafts.map((d, i) => ({ ...d, id: i, error: null })))
      setWarnings(result.warnings)
    } catch (err) {
      setError(err instanceof Error ? err.message : t("tasks.batch.errParse"))
    } finally {
      setBusy(null)
    }
  }

  const update = (id: number, patch: Partial<Row>) =>
    setRows((current) => current?.map((row) => (row.id === id ? { ...row, ...patch, error: null } : row)) ?? null)

  const remove = (id: number) => setRows((current) => current?.filter((row) => row.id !== id) ?? null)

  const create = async () => {
    if (!workspaceId || !rows || rows.length === 0) return
    setBusy("create")
    setError(null)
    try {
      const result = await createTaskBatch(
        workspaceId,
        rows.map(({ title, description, acceptanceCriteria, priority }) => ({
          title: title.trim(),
          description: description.trim(),
          acceptanceCriteria,
          priority,
        })),
      )
      // Created tasks leave the list; whatever failed stays so it can be fixed and sent again.
      const failed = new Map(result.items.filter((i) => !i.success).map((i) => [i.index, i.errorMessage]))
      const remaining = rows
        .map((row, index) => ({ ...row, error: failed.get(index) ?? null }))
        .filter((_, index) => failed.has(index))
      setCreatedCount((n) => n + (rows.length - remaining.length))
      setRows(remaining)
      setWarnings([])
      onCreated()
      if (remaining.length === 0) {
        onClose()
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : t("tasks.batch.errCreate"))
    } finally {
      setBusy(null)
    }
  }

  const warningsFor = (index: number) => warnings.filter((w) => w.draftIndex === index)
  const general = warnings.filter((w) => w.draftIndex === null)
  const canCreate = !!workspaceId && !!rows && rows.length > 0 && rows.every((r) => r.title.trim()) && busy === null

  return (
    <Panel className="mb-6 p-5">
      <div className="mb-3 flex items-start justify-between gap-3">
        <div>
          <h2 className="text-[14px] font-semibold text-foreground">{t("tasks.batch.title")}</h2>
          <p className="mt-0.5 text-[12.5px] text-muted-foreground">{t("tasks.batch.help")}</p>
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label={t("common.cancel")}
          className="cursor-pointer rounded-[var(--radius-md)] p-1 text-muted-foreground hover:bg-surface-3 hover:text-foreground"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      {error && (
        <div className="mb-3 flex items-start gap-1.5 text-[12.5px] font-medium text-danger">
          <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" /> {error}
        </div>
      )}

      {rows === null ? (
        <>
          <textarea
            value={text}
            onChange={(e) => setText(e.target.value)}
            rows={10}
            spellCheck={false}
            placeholder={t("tasks.batch.placeholder")}
            className={cn(fieldClass, "resize-y font-mono text-[12px]")}
          />
          <div className="mt-3 flex items-center gap-3">
            <Button variant="primary" onClick={() => void parse()} disabled={!text.trim() || busy !== null}>
              {busy === "parse" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : t("tasks.batch.parse")}
            </Button>
            {createdCount > 0 && (
              <span className="flex items-center gap-1 text-[12.5px] text-success">
                <CheckCircle2 className="h-3.5 w-3.5" /> {t("tasks.batch.createdSoFar", { count: createdCount })}
              </span>
            )}
          </div>
        </>
      ) : (
        <>
          {general.map((w) => (
            <div key={w.code} className="mb-2 text-[12px] text-warning">
              {t(`tasks.batch.warn.${w.code}`)}
            </div>
          ))}
          {rows.length === 0 ? (
            <p className="text-[13px] text-muted-foreground">{t("tasks.batch.nothingLeft")}</p>
          ) : (
            <ol className="space-y-3">
              {rows.map((row, index) => (
                <li key={row.id} className={cn("rounded-[var(--radius-md)] border p-3", row.error ? "border-danger/50" : "border-border")}>
                  <div className="flex items-start gap-2">
                    <span className="mt-2 w-5 shrink-0 text-right font-mono text-[11px] text-subtle-foreground">{index + 1}</span>
                    <div className="min-w-0 flex-1 space-y-2">
                      <input
                        value={row.title}
                        onChange={(e) => update(row.id, { title: e.target.value })}
                        placeholder={t("tasks.batch.titleLabel")}
                        aria-label={t("tasks.batch.titleLabel")}
                        className={cn(fieldClass, "font-medium", row.title.length > 200 && "border-danger")}
                      />
                      <textarea
                        value={row.description}
                        onChange={(e) => update(row.id, { description: e.target.value })}
                        rows={3}
                        placeholder={t("tasks.batch.descriptionLabel")}
                        aria-label={t("tasks.batch.descriptionLabel")}
                        className={cn(fieldClass, "resize-y")}
                      />
                      <div className="flex flex-wrap items-center gap-2">
                        <select
                          value={row.priority}
                          onChange={(e) => update(row.id, { priority: Number(e.target.value) })}
                          aria-label={t("tasks.batch.priorityLabel")}
                          className="h-8 cursor-pointer rounded-[var(--radius-md)] border border-border-strong bg-surface px-2 text-[12.5px] text-foreground"
                        >
                          {PRIORITIES.map((p) => (
                            <option key={p.key} value={p.value}>
                              {t(`tasks.batch.priority.${p.key}`)}
                            </option>
                          ))}
                        </select>
                        {warningsFor(index).map((w) => (
                          <Badge key={w.code} tone="amber">
                            {t(`tasks.batch.warn.${w.code}`)}
                          </Badge>
                        ))}
                      </div>
                      {row.error && <div className="text-[12px] font-medium text-danger">{row.error}</div>}
                    </div>
                    <button
                      type="button"
                      onClick={() => remove(row.id)}
                      aria-label={t("tasks.batch.remove")}
                      className="mt-1 cursor-pointer rounded-[var(--radius-md)] p-1.5 text-muted-foreground hover:bg-surface-3 hover:text-danger"
                    >
                      <Trash2 className="h-4 w-4" />
                    </button>
                  </div>
                </li>
              ))}
            </ol>
          )}

          <div className="mt-4 flex items-center gap-3">
            <Button variant="primary" onClick={() => void create()} disabled={!canCreate}>
              {busy === "create" ? (
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
              ) : (
                t("tasks.batch.create", { count: rows.length })
              )}
            </Button>
            <Button
              onClick={() => {
                setRows(null)
                setWarnings([])
              }}
              disabled={busy !== null}
            >
              {t("tasks.batch.back")}
            </Button>
            {!workspaceId && <span className="text-[12px] text-danger">{t("tasks.errNoWorkspace")}</span>}
          </div>
        </>
      )}
    </Panel>
  )
}
