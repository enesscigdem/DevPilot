import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { AlertCircle, Check, Clock, Cpu, KeyRound, Loader2, Pencil, Plus, Trash2, X, Zap } from "lucide-react"
import { useTranslation } from "react-i18next"
import {
  createAiModel,
  deleteAiModel,
  discoverAiModels,
  getAiModels,
  getAiStageAssignments,
  setAiStageAssignments,
  MODEL_TEST_CLIENT_GUARD_SECONDS,
  MODEL_TEST_LIMIT_SECONDS,
  testAiModel,
  updateAiModel,
} from "@/api"
import { fmt } from "@/i18n"
import { cn } from "@/lib/utils"
import { ModelPicker } from "@/components/ModelPicker"
import { PageContainer, PageHeading, SectionHead } from "@/components/shared"
import { Badge, Button, Panel } from "@/components/ui/primitives"
import { AI_STAGES, type AiAdapterType, type AiModel, type AiModelOption, type AiStage, type SaveAiModelRequest } from "@/types"

/* ------------------------------- Provider presets ------------------------------ */

interface Preset {
  id: string
  name: string
  adapter: AiAdapterType
  baseUrl: string
  modelHint: string
  useMaxCompletionTokens?: boolean
}

// Claude and Gemini use their own APIs; every other endpoint speaks the OpenAI chat-completions protocol.
// Picking a preset only pre-fills the form.
const PRESETS: Preset[] = [
  { id: "anthropic", adapter: "Claude", name: "Claude (Anthropic)", baseUrl: "https://api.anthropic.com", modelHint: "claude-…" },
  { id: "gemini", adapter: "Gemini", name: "Gemini (Google)", baseUrl: "https://generativelanguage.googleapis.com", modelHint: "gemini-…" },
  { id: "openai", adapter: "OpenAiCompatible", name: "OpenAI", baseUrl: "https://api.openai.com/v1", modelHint: "gpt-…", useMaxCompletionTokens: true },
  { id: "deepseek", adapter: "OpenAiCompatible", name: "DeepSeek", baseUrl: "https://api.deepseek.com", modelHint: "deepseek-chat" },
  { id: "kimi", adapter: "OpenAiCompatible", name: "Kimi (Moonshot)", baseUrl: "https://api.moonshot.ai/v1", modelHint: "kimi-…" },
  { id: "qwen", adapter: "OpenAiCompatible", name: "Qwen (Alibaba)", baseUrl: "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", modelHint: "qwen-plus" },
  { id: "mistral", adapter: "OpenAiCompatible", name: "Mistral", baseUrl: "https://api.mistral.ai/v1", modelHint: "mistral-large-latest" },
  { id: "openrouter", adapter: "OpenAiCompatible", name: "OpenRouter", baseUrl: "https://openrouter.ai/api/v1", modelHint: "vendor/model-name" },
  { id: "ollama", adapter: "OpenAiCompatible", name: "Ollama (local)", baseUrl: "http://localhost:11434/v1", modelHint: "llama3.1" },
  { id: "lmstudio", adapter: "OpenAiCompatible", name: "LM Studio (local)", baseUrl: "http://localhost:1234/v1", modelHint: "model-id" },
]

const ADAPTERS: AiAdapterType[] = ["OpenAiCompatible", "Claude", "Gemini"]

function isLocalUrl(url: string): boolean {
  try {
    const host = new URL(url).hostname
    return host === "localhost" || host === "127.0.0.1" || host === "[::1]" || host === "::1"
  } catch {
    return false
  }
}

function hostOf(url: string): string {
  try {
    return new URL(url).host
  } catch {
    return url
  }
}

/* ---------------------------------- Form state --------------------------------- */

interface Draft {
  presetId: string
  adapterType: AiAdapterType
  name: string
  baseUrl: string
  modelName: string
  apiKey: string
  maxOutputTokens: string
  inputPrice: string
  outputPrice: string
  supportsReasoningEffort: boolean
  useMaxCompletionTokens: boolean
  isEnabled: boolean
  isDefault: boolean
}

const emptyDraft: Draft = {
  presetId: "",
  adapterType: "OpenAiCompatible",
  name: "",
  baseUrl: "",
  modelName: "",
  apiKey: "",
  maxOutputTokens: "",
  inputPrice: "",
  outputPrice: "",
  supportsReasoningEffort: false,
  useMaxCompletionTokens: false,
  isEnabled: true,
  isDefault: false,
}

function draftFromModel(m: AiModel): Draft {
  return {
    presetId: PRESETS.find((p) => p.baseUrl === m.baseUrl && p.adapter === m.adapterType)?.id ?? "",
    adapterType: m.adapterType,
    name: m.name,
    baseUrl: m.baseUrl,
    modelName: m.modelName,
    apiKey: "",
    maxOutputTokens: m.maxOutputTokens?.toString() ?? "",
    inputPrice: m.inputPricePerMillionTokensUsd?.toString() ?? "",
    outputPrice: m.outputPricePerMillionTokensUsd?.toString() ?? "",
    supportsReasoningEffort: m.supportsReasoningEffort,
    useMaxCompletionTokens: m.useMaxCompletionTokens,
    isEnabled: m.isEnabled,
    isDefault: m.isDefault,
  }
}

function parseNumber(value: string): number | null {
  const trimmed = value.trim()
  if (!trimmed) return null
  const n = Number(trimmed)
  return Number.isFinite(n) ? n : null
}

function toRequest(d: Draft): SaveAiModelRequest {
  return {
    name: d.name.trim(),
    adapterType: d.adapterType,
    baseUrl: d.baseUrl.trim(),
    modelName: d.modelName.trim(),
    apiKey: d.apiKey.trim() || undefined,
    maxOutputTokens: parseNumber(d.maxOutputTokens),
    supportsReasoningEffort: d.supportsReasoningEffort,
    useMaxCompletionTokens: d.useMaxCompletionTokens,
    inputPricePerMillionTokensUsd: parseNumber(d.inputPrice),
    outputPricePerMillionTokensUsd: parseNumber(d.outputPrice),
    isEnabled: d.isEnabled,
    isDefault: d.isDefault,
  }
}

const fieldClass =
  "h-9 w-full rounded-[var(--radius-md)] border border-border-strong bg-surface px-3 text-[13px] text-foreground outline-none placeholder:text-subtle-foreground focus:border-primary focus:ring-2 focus:ring-ring/30"

function Field({ label, hint, children, className }: { label: string; hint?: string; children: React.ReactNode; className?: string }) {
  return (
    <label className={cn("block", className)}>
      <span className="mb-1.5 block text-[12px] font-medium text-foreground">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-[11.5px] text-subtle-foreground">{hint}</span>}
    </label>
  )
}

function Check2({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <label className="flex cursor-pointer items-center gap-2 text-[13px] text-foreground">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        className="h-4 w-4 rounded border-border-strong accent-primary"
      />
      {label}
    </label>
  )
}

/* ------------------------------------ Form ------------------------------------- */

function ModelForm({
  editing,
  onCancel,
  onSaved,
}: {
  editing: AiModel | null
  onCancel: () => void
  onSaved: () => void
}) {
  const { t } = useTranslation()
  const [draft, setDraft] = useState<Draft>(editing ? draftFromModel(editing) : emptyDraft)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [modelOptions, setModelOptions] = useState<AiModelOption[]>([])
  const [loadingModels, setLoadingModels] = useState(false)
  const [modelsMessage, setModelsMessage] = useState<{ ok: boolean; text: string } | null>(null)

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft((d) => ({ ...d, [key]: value }))
  const preset = PRESETS.find((p) => p.id === draft.presetId)
  const local = isLocalUrl(draft.baseUrl)
  const openAiStyle = draft.adapterType === "OpenAiCompatible"

  const applyPreset = (id: string) => {
    const p = PRESETS.find((x) => x.id === id)
    setDraft((d) => ({
      ...d,
      presetId: id,
      adapterType: p ? p.adapter : "OpenAiCompatible",
      baseUrl: p ? p.baseUrl : d.baseUrl,
      name: p && !d.name ? p.name : d.name,
      useMaxCompletionTokens: p?.useMaxCompletionTokens ?? false,
    }))
  }

  const loadModels = async () => {
    setLoadingModels(true)
    setModelsMessage(null)
    try {
      const result = await discoverAiModels({
        adapterType: draft.adapterType,
        baseUrl: draft.baseUrl.trim(),
        apiKey: draft.apiKey.trim() || undefined,
        existingModelId: editing?.id,
      })
      setModelOptions(result.models)
      setModelsMessage({ ok: result.success, text: result.success ? t("models.form.modelsFound", { count: result.models.length }) : result.message })
    } catch (err) {
      setModelsMessage({ ok: false, text: err instanceof Error ? err.message : t("models.form.modelsFailed") })
    } finally {
      setLoadingModels(false)
    }
  }

  const canSave = draft.name.trim() && draft.baseUrl.trim() && draft.modelName.trim() && !saving

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!canSave) return
    setSaving(true)
    setError(null)
    try {
      const request = toRequest(draft)
      if (editing) await updateAiModel(editing.id, request)
      else await createAiModel(request)
      onSaved()
    } catch (err) {
      setError(err instanceof Error ? err.message : t("models.errSave"))
    } finally {
      setSaving(false)
    }
  }

  const keyPlaceholder = editing?.hasApiKey
    ? t("models.form.apiKeyKeep", { hint: editing.apiKeyHint ?? "" })
    : local
      ? t("models.form.apiKeyLocal")
      : "sk-…"

  return (
    <Panel className="mb-6 p-5">
      <form onSubmit={submit} className="space-y-4">
        <h2 className="text-[14px] font-semibold text-foreground">
          {editing ? t("models.editModel") : t("models.addModel")}
        </h2>

        <div>
          <span className="mb-1.5 block text-[12px] font-medium text-foreground">{t("models.form.preset")}</span>
          <div className="flex flex-wrap gap-1.5">
            {PRESETS.map((p) => (
              <button
                key={p.id}
                type="button"
                onClick={() => applyPreset(p.id)}
                className={cn(
                  "rounded-full border px-2.5 py-1 text-[12px] font-medium transition-colors",
                  draft.presetId === p.id
                    ? "border-primary bg-primary-soft text-primary"
                    : "border-border-strong text-muted-foreground hover:bg-surface-3 hover:text-foreground",
                )}
              >
                {p.name}
              </button>
            ))}
            <button
              type="button"
              onClick={() => setDraft((d) => ({ ...d, presetId: "", adapterType: "OpenAiCompatible" }))}
              className={cn(
                "rounded-full border px-2.5 py-1 text-[12px] font-medium transition-colors",
                draft.presetId === ""
                  ? "border-primary bg-primary-soft text-primary"
                  : "border-border-strong text-muted-foreground hover:bg-surface-3 hover:text-foreground",
              )}
            >
              {t("models.form.custom")}
            </button>
          </div>
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("models.form.adapterType")} className="sm:col-span-2">
            <select
              className={fieldClass}
              value={draft.adapterType}
              onChange={(e) => setDraft((d) => ({ ...d, adapterType: e.target.value as AiAdapterType, presetId: "" }))}
            >
              {ADAPTERS.map((a) => (
                <option key={a} value={a}>
                  {t(`models.adapter.${a}`)}
                </option>
              ))}
            </select>
          </Field>
          <Field label={t("models.form.name")}>
            <input
              className={fieldClass}
              value={draft.name}
              maxLength={120}
              onChange={(e) => set("name", e.target.value)}
              placeholder={t("models.form.namePlaceholder")}
            />
          </Field>
          <Field label={t("models.form.modelName")}>
            <div className="flex gap-2">
              <ModelPicker
                value={draft.modelName}
                onChange={(v) => set("modelName", v)}
                options={modelOptions}
                placeholder={preset?.modelHint ?? t("models.form.modelNamePlaceholder")}
                inputClassName={cn(fieldClass, "font-mono")}
              />
              <Button type="button" size="md" onClick={() => void loadModels()} disabled={loadingModels || !draft.baseUrl.trim()}>
                {loadingModels && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                {t("models.form.loadModels")}
              </Button>
            </div>
            {modelsMessage && (
              <span className={cn("mt-1 block text-[11.5px]", modelsMessage.ok ? "text-success" : "text-danger")}>{modelsMessage.text}</span>
            )}
          </Field>
          <Field label={t("models.form.baseUrl")}>
            <input
              className={cn(fieldClass, "font-mono")}
              value={draft.baseUrl}
              onChange={(e) => {
                set("baseUrl", e.target.value)
                set("presetId", PRESETS.find((p) => p.baseUrl === e.target.value && p.adapter === draft.adapterType)?.id ?? "")
              }}
              placeholder="https://api.example.com/v1"
              inputMode="url"
            />
          </Field>
          <Field label={t("models.form.apiKey")} hint={t("models.form.keyEncrypted")}>
            <div className="relative">
              <KeyRound className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-subtle-foreground" />
              <input
                className={cn(fieldClass, "pl-8 font-mono")}
                type="password"
                autoComplete="off"
                value={draft.apiKey}
                onChange={(e) => set("apiKey", e.target.value)}
                placeholder={keyPlaceholder}
              />
            </div>
          </Field>
        </div>

        <details className="group rounded-[var(--radius-md)] border border-border">
          <summary className="cursor-pointer select-none px-3 py-2 text-[12px] font-medium text-muted-foreground hover:text-foreground">
            {t("models.form.advanced")}
          </summary>
          <div className="grid gap-4 border-t border-border p-3 sm:grid-cols-3">
            <Field label={t("models.form.maxOutputTokens")} hint={t("models.form.maxOutputTokensHint")}>
              <input
                className={fieldClass}
                inputMode="numeric"
                value={draft.maxOutputTokens}
                onChange={(e) => set("maxOutputTokens", e.target.value)}
              />
            </Field>
            <Field label={t("models.form.inputPrice")}>
              <input className={fieldClass} inputMode="decimal" value={draft.inputPrice} onChange={(e) => set("inputPrice", e.target.value)} />
            </Field>
            <Field label={t("models.form.outputPrice")}>
              <input className={fieldClass} inputMode="decimal" value={draft.outputPrice} onChange={(e) => set("outputPrice", e.target.value)} />
            </Field>
            <p className="text-[11.5px] text-subtle-foreground sm:col-span-3">{t("models.form.priceHint")}</p>
            <div className={cn("space-y-2 sm:col-span-3", !openAiStyle && "hidden")}>
              <Check2
                checked={draft.supportsReasoningEffort}
                onChange={(v) => set("supportsReasoningEffort", v)}
                label={t("models.form.supportsReasoning")}
              />
              <Check2
                checked={draft.useMaxCompletionTokens}
                onChange={(v) => set("useMaxCompletionTokens", v)}
                label={t("models.form.useMaxCompletion")}
              />
            </div>
          </div>
        </details>

        <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
          <Check2 checked={draft.isEnabled} onChange={(v) => set("isEnabled", v)} label={t("models.form.enabled")} />
          <Check2 checked={draft.isDefault} onChange={(v) => set("isDefault", v)} label={t("models.form.isDefault")} />
        </div>

        {error && (
          <div className="flex items-start gap-2 rounded-[var(--radius-md)] border border-danger/25 bg-danger-soft px-3 py-2 text-[12.5px] text-danger">
            <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
            <span>{error}</span>
          </div>
        )}

        <div className="flex items-center justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onCancel}>
            {t("models.form.cancel")}
          </Button>
          <Button type="submit" variant="primary" disabled={!canSave}>
            {saving && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            {saving ? t("models.form.saving") : t("models.form.save")}
          </Button>
        </div>
      </form>
    </Panel>
  )
}

/** Counts up while `active`, so a slow connection test shows it is still working. */
function useElapsedSeconds(active: boolean): number {
  const [seconds, setSeconds] = useState(0)
  useEffect(() => {
    if (!active) return
    const started = Date.now()
    setSeconds(0)
    const timer = setInterval(() => setSeconds(Math.floor((Date.now() - started) / 1000)), 1000)
    return () => clearInterval(timer)
  }, [active])
  return seconds
}

/* ------------------------------------ Card ------------------------------------- */

function ModelCard({
  model,
  testing,
  note,
  onTest,
  onCancelTest,
  onEdit,
  onDelete,
  onMakeDefault,
}: {
  model: AiModel
  testing: boolean
  note: string | null
  onTest: () => void
  onCancelTest: () => void
  onEdit: () => void
  onDelete: () => void
  onMakeDefault: () => void
}) {
  const { t } = useTranslation()
  const elapsed = useElapsedSeconds(testing)

  const testBadge =
    model.lastTestSucceeded === true ? (
      <Badge tone="green">
        <Check className="h-3 w-3" />
        {t("models.testOk")}
      </Badge>
    ) : model.lastTestOutcome === "Timeout" ? (
      // Our own time limit ran out: the provider never answered. Not an error the provider returned.
      <Badge tone="amber">
        <Clock className="h-3 w-3" />
        {t("models.testTimedOut", { seconds: MODEL_TEST_LIMIT_SECONDS })}
      </Badge>
    ) : model.lastTestOutcome === "HttpError" ? (
      <Badge tone="red">{t("models.testHttpError", { status: model.lastTestStatusCode ?? "" })}</Badge>
    ) : model.lastTestOutcome === "NetworkError" ? (
      <Badge tone="red">{t("models.testNetworkError")}</Badge>
    ) : model.lastTestSucceeded === false ? (
      <Badge tone="red">{t("models.testFailed")}</Badge>
    ) : (
      <Badge tone="gray">{t("models.notTested")}</Badge>
    )

  return (
    <Panel className={cn("p-4", !model.isEnabled && "opacity-70")}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <Cpu className="h-4 w-4 shrink-0 text-subtle-foreground" strokeWidth={2} />
            <span className="text-[14px] font-semibold text-foreground">{model.name}</span>
            {model.isDefault && <Badge tone="blue">{t("models.default")}</Badge>}
            {!model.isEnabled && <Badge tone="gray">{t("models.disabled")}</Badge>}
          </div>
          <div className="mt-1 font-mono text-[12px] text-muted-foreground">
            {model.modelName} · {hostOf(model.baseUrl)} · {t(`models.adapter.${model.adapterType}`)}
          </div>
          <div className="mt-2.5 flex flex-wrap items-center gap-2">
            {model.hasApiKey ? (
              <Badge mono>
                <KeyRound className="h-3 w-3" />
                {t("models.keySet", { hint: model.apiKeyHint ?? "" })}
              </Badge>
            ) : (
              !isLocalUrl(model.baseUrl) && <Badge tone="amber">{t("models.noKey")}</Badge>
            )}
            <span title={model.lastTestedAt ? fmt.dateTime(model.lastTestedAt) : undefined}>{testBadge}</span>
          </div>
          {model.lastTestSucceeded === false && model.lastTestMessage && (
            <p
              className={cn(
                "mt-2 max-w-xl text-[12px]",
                model.lastTestOutcome === "Timeout" ? "text-amber-600 dark:text-amber-400" : "text-danger",
              )}
            >
              {model.lastTestMessage}
            </p>
          )}
          {/* A failed test already shows its message above; the live note would repeat it. */}
          {note && !(model.lastTestSucceeded === false && model.lastTestMessage && note.includes(model.lastTestMessage)) && (
            <p className="mt-2 text-[12px] text-muted-foreground">{note}</p>
          )}
        </div>

        <div className="flex flex-wrap items-center gap-1.5">
          <Button size="sm" onClick={onTest} disabled={testing}>
            {testing ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Zap className="h-3.5 w-3.5" />}
            {testing
              ? t("models.testingFor", { seconds: Math.min(elapsed, MODEL_TEST_LIMIT_SECONDS), limit: MODEL_TEST_LIMIT_SECONDS })
              : t("models.test")}
          </Button>
          {testing && (
            <Button size="sm" variant="danger" onClick={onCancelTest}>
              <X className="h-3.5 w-3.5" />
              {t("models.cancelTest")}
            </Button>
          )}
          {!model.isDefault && model.isEnabled && (
            <Button size="sm" variant="subtle" onClick={onMakeDefault}>
              {t("models.makeDefault")}
            </Button>
          )}
          <Button size="sm" variant="ghost" onClick={onEdit}>
            <Pencil className="h-3.5 w-3.5" />
            {t("models.edit")}
          </Button>
          <Button size="sm" variant="danger" onClick={onDelete}>
            <Trash2 className="h-3.5 w-3.5" />
            <span className="sr-only">{t("models.delete")}</span>
          </Button>
        </div>
      </div>
    </Panel>
  )
}

/* ------------------------------------ Page ------------------------------------- */

type Assignments = Record<AiStage, string>

const emptyAssignments: Assignments = { Planning: "", CodeGeneration: "", Repair: "", Brain: "" }

export function Models() {
  const { t } = useTranslation()
  const [models, setModels] = useState<AiModel[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [formOpen, setFormOpen] = useState(false)
  const [editing, setEditing] = useState<AiModel | null>(null)
  const [testingId, setTestingId] = useState<string | null>(null)
  const [notes, setNotes] = useState<Record<string, string>>({})

  const [saved, setSaved] = useState<Assignments>(emptyAssignments)
  const [assign, setAssign] = useState<Assignments>(emptyAssignments)
  const [stageMessage, setStageMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [savingStages, setSavingStages] = useState(false)

  const load = useCallback(async () => {
    try {
      const [list, stages] = await Promise.all([getAiModels(), getAiStageAssignments()])
      const next = { ...emptyAssignments }
      for (const s of stages) next[s.stage] = s.aiModelConfigId ?? ""
      setModels(list)
      setSaved(next)
      setAssign(next)
      setLoadError(null)
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : t("models.errLoad"))
    } finally {
      setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void load()
  }, [load])

  const enabledModels = useMemo(() => models.filter((m) => m.isEnabled), [models])
  const dirty = AI_STAGES.some((s) => assign[s] !== saved[s])

  const openNew = () => {
    setEditing(null)
    setFormOpen(true)
  }

  const openEdit = (m: AiModel) => {
    setEditing(m)
    setFormOpen(true)
  }

  const handleSaved = async () => {
    setFormOpen(false)
    setEditing(null)
    await load()
  }

  const runAction = async (action: () => Promise<unknown>, fallback: string) => {
    try {
      await action()
      await load()
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : fallback)
    }
  }

  // The running test's controller, so the user can stop waiting. Cancelling aborts the request: the server
  // sees the disconnect, stops the provider call and records nothing on the model.
  const testAbort = useRef<{ id: string; controller: AbortController; cancelled: boolean } | null>(null)

  const cancelTest = (id: string) => {
    const current = testAbort.current
    if (current?.id !== id) return
    current.cancelled = true
    current.controller.abort()
  }

  const handleTest = async (m: AiModel) => {
    const run = { id: m.id, controller: new AbortController(), cancelled: false }
    testAbort.current = run
    // The server ends the test at its own limit; the browser only steps in if the server stays silent past it.
    let guardFired = false
    const guard = setTimeout(() => {
      guardFired = true
      run.controller.abort()
    }, MODEL_TEST_CLIENT_GUARD_SECONDS * 1000)

    setTestingId(m.id)
    setNotes((n) => ({ ...n, [m.id]: "" }))
    try {
      const result = await testAiModel(m.id, { signal: run.controller.signal })
      setNotes((n) => ({ ...n, [m.id]: t("models.testResult", { message: result.message, ms: result.durationMs }) }))
      await load()
    } catch (err) {
      const aborted = err instanceof DOMException && err.name === "AbortError"
      const message = guardFired
        ? t("models.testNoServerAnswer", { seconds: MODEL_TEST_CLIENT_GUARD_SECONDS })
        : aborted && run.cancelled
          ? t("models.testCancelled")
          : err instanceof Error
            ? err.message
            : t("models.errTest")
      setNotes((n) => ({ ...n, [m.id]: message }))
    } finally {
      clearTimeout(guard)
      if (testAbort.current === run) testAbort.current = null
      setTestingId(null)
    }
  }

  const handleDelete = (m: AiModel) => {
    if (!window.confirm(t("models.confirmDelete", { name: m.name }))) return
    void runAction(() => deleteAiModel(m.id), t("models.errDelete"))
  }

  const handleMakeDefault = (m: AiModel) =>
    void runAction(
      () =>
        updateAiModel(m.id, {
          name: m.name,
          adapterType: m.adapterType,
          baseUrl: m.baseUrl,
          modelName: m.modelName,
          maxOutputTokens: m.maxOutputTokens,
          supportsReasoningEffort: m.supportsReasoningEffort,
          useMaxCompletionTokens: m.useMaxCompletionTokens,
          inputPricePerMillionTokensUsd: m.inputPricePerMillionTokensUsd,
          outputPricePerMillionTokensUsd: m.outputPricePerMillionTokensUsd,
          isEnabled: m.isEnabled,
          isDefault: true,
        }),
      t("models.errSave"),
    )

  const saveStages = async () => {
    setSavingStages(true)
    setStageMessage(null)
    try {
      await setAiStageAssignments(AI_STAGES.map((stage) => ({ stage, aiModelConfigId: assign[stage] || null })))
      setSaved(assign)
      setStageMessage({ ok: true, text: t("models.stagesSaved") })
    } catch (err) {
      setStageMessage({ ok: false, text: err instanceof Error ? err.message : t("models.errStages") })
    } finally {
      setSavingStages(false)
    }
  }

  return (
    <PageContainer>
      <PageHeading
        eyebrow={t("models.eyebrow")}
        title={t("models.title")}
        description={t("models.description")}
        actions={
          !formOpen && (
            <Button variant="primary" onClick={openNew}>
              <Plus className="h-4 w-4" />
              {t("models.addModel")}
            </Button>
          )
        }
      />

      {loadError && (
        <div className="mb-4 flex items-start gap-2 rounded-[var(--radius-md)] border border-danger/25 bg-danger-soft px-3 py-2.5 text-[13px] text-danger">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{loadError}</span>
        </div>
      )}

      {formOpen && (
        <ModelForm
          key={editing?.id ?? "new"}
          editing={editing}
          onCancel={() => {
            setFormOpen(false)
            setEditing(null)
          }}
          onSaved={() => void handleSaved()}
        />
      )}

      {loading && (
        <div className="flex items-center gap-2 text-[13px] text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          {t("models.loading")}
        </div>
      )}

      {!loading && models.length === 0 && !formOpen && (
        <Panel className="flex flex-col items-center px-6 py-12 text-center">
          <div className="mb-3 flex h-11 w-11 items-center justify-center rounded-full border border-border bg-surface-2">
            <Cpu className="h-5 w-5 text-foreground" strokeWidth={2} />
          </div>
          <h2 className="text-[15px] font-semibold text-foreground">{t("models.emptyTitle")}</h2>
          <p className="mt-1.5 max-w-md text-[13px] leading-relaxed text-muted-foreground">{t("models.emptyBody")}</p>
          <Button variant="primary" className="mt-4" onClick={openNew}>
            <Plus className="h-4 w-4" />
            {t("models.addModel")}
          </Button>
        </Panel>
      )}

      {models.length > 0 && (
        <section className="mb-8">
          <SectionHead title={t("models.modelsHeading")} count={models.length} />
          <div className="space-y-3">
            {models.map((m) => (
              <ModelCard
                key={m.id}
                model={m}
                testing={testingId === m.id}
                note={notes[m.id] || null}
                onTest={() => void handleTest(m)}
                onCancelTest={() => cancelTest(m.id)}
                onEdit={() => openEdit(m)}
                onDelete={() => handleDelete(m)}
                onMakeDefault={() => handleMakeDefault(m)}
              />
            ))}
          </div>
        </section>
      )}

      {models.length > 0 && (
        <section>
          <SectionHead title={t("models.stagesHeading")} />
          <Panel className="divide-y divide-border">
            {AI_STAGES.map((stage) => (
              <div key={stage} className="flex flex-wrap items-center justify-between gap-3 px-4 py-3">
                <div>
                  <div className="text-[13px] font-medium text-foreground">{t(`models.stage.${stage}`)}</div>
                  <div className="text-[12px] text-subtle-foreground">{t(`models.stageHint.${stage}`)}</div>
                </div>
                <select
                  value={assign[stage]}
                  onChange={(e) => setAssign((a) => ({ ...a, [stage]: e.target.value }))}
                  className={cn(fieldClass, "w-full sm:w-64")}
                >
                  <option value="">{t("models.useDefault")}</option>
                  {enabledModels.map((m) => (
                    <option key={m.id} value={m.id}>
                      {m.name}
                    </option>
                  ))}
                </select>
              </div>
            ))}
            <div className="flex items-center justify-between gap-3 px-4 py-3">
              <p
                className={cn(
                  "text-[12px]",
                  stageMessage ? (stageMessage.ok ? "text-success" : "text-danger") : "text-subtle-foreground",
                )}
              >
                {stageMessage?.text ?? t("models.stagesHint")}
              </p>
              <Button variant="primary" onClick={() => void saveStages()} disabled={!dirty || savingStages}>
                {savingStages && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                {t("models.saveStages")}
              </Button>
            </div>
          </Panel>
        </section>
      )}
    </PageContainer>
  )
}
