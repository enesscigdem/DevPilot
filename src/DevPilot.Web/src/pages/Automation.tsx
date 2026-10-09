import { useEffect, useMemo, useState } from "react"
import { AlertCircle, Check, Loader2, PauseCircle, ShieldCheck } from "lucide-react"
import { useTranslation } from "react-i18next"
import { getAutomationPolicy, updateAutomationPolicy } from "@/api"
import { cn } from "@/lib/utils"
import { useWorkspace } from "@/lib/workspace"
import { PageContainer, PageHeading, SectionHead } from "@/components/shared"
import { Badge, Button, Panel } from "@/components/ui/primitives"
import { AUTOMATION_LEVELS, CONFLICT_MODES, type AutomationLevel, type AutomationPolicy, type ConflictMode } from "@/types"

const fieldClass =
  "h-9 w-full rounded-[var(--radius-md)] border border-border-strong bg-surface px-3 text-[13px] text-foreground outline-none placeholder:text-subtle-foreground focus:border-primary focus:ring-2 focus:ring-ring/30"

interface Draft {
  level: AutomationLevel
  paused: boolean
  maxFilesChanged: string
  maxLinesChanged: string
  maxParallelExecutions: string
  protectedPaths: string
  requireGreenCiForMerge: boolean
  allowBuildOnlyDelivery: boolean
  requireVisualReview: boolean
  conflictMode: ConflictMode
}

function toDraft(p: AutomationPolicy): Draft {
  return {
    level: p.level,
    paused: p.paused,
    maxFilesChanged: String(p.maxFilesChanged),
    maxLinesChanged: String(p.maxLinesChanged),
    maxParallelExecutions: String(p.maxParallelExecutions),
    protectedPaths: p.protectedPaths.join("\n"),
    requireGreenCiForMerge: p.requireGreenCiForMerge,
    allowBuildOnlyDelivery: p.allowBuildOnlyDelivery,
    requireVisualReview: p.requireVisualReview,
    conflictMode: p.conflictMode,
  }
}

function inRange(value: string, min: number, max: number): number | null {
  const n = Number(value)
  return Number.isInteger(n) && n >= min && n <= max ? n : null
}

function patternsOf(text: string): string[] {
  return text
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
}

function sameDraft(a: Draft, b: Draft): boolean {
  return (
    a.level === b.level &&
    a.paused === b.paused &&
    a.maxFilesChanged === b.maxFilesChanged &&
    a.maxLinesChanged === b.maxLinesChanged &&
    a.maxParallelExecutions === b.maxParallelExecutions &&
    patternsOf(a.protectedPaths).join("\n") === patternsOf(b.protectedPaths).join("\n") &&
    a.requireGreenCiForMerge === b.requireGreenCiForMerge &&
    a.allowBuildOnlyDelivery === b.allowBuildOnlyDelivery &&
    a.requireVisualReview === b.requireVisualReview &&
    a.conflictMode === b.conflictMode
  )
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-[12px] font-medium text-foreground">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-[11.5px] text-subtle-foreground">{hint}</span>}
    </label>
  )
}

export function Automation() {
  const { t, i18n } = useTranslation()
  const { activeWorkspace, activeWorkspaceId } = useWorkspace()
  const [policy, setPolicy] = useState<AutomationPolicy | null>(null)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [justSaved, setJustSaved] = useState(false)

  useEffect(() => {
    if (!activeWorkspaceId) {
      setPolicy(null)
      setDraft(null)
      return
    }
    let cancelled = false
    setLoading(true)
    setError(null)
    getAutomationPolicy(activeWorkspaceId)
      .then((loaded) => {
        if (cancelled) return
        setPolicy(loaded)
        setDraft(toDraft(loaded))
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : t("automation.errLoad"))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [activeWorkspaceId, t])

  const saved = useMemo(() => (policy ? toDraft(policy) : null), [policy])
  const dirty = draft && saved ? !sameDraft(draft, saved) : false
  const limits = draft
    ? {
        files: inRange(draft.maxFilesChanged, 1, 200),
        lines: inRange(draft.maxLinesChanged, 1, 20000),
        parallel: inRange(draft.maxParallelExecutions, 1, 5),
      }
    : null
  const valid = !!limits && limits.files !== null && limits.lines !== null && limits.parallel !== null

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => {
    setJustSaved(false)
    setDraft((d) => (d ? { ...d, [key]: value } : d))
  }

  const save = async () => {
    if (!activeWorkspaceId || !draft || !limits || !valid) return
    setSaving(true)
    setError(null)
    try {
      const result = await updateAutomationPolicy(activeWorkspaceId, {
        level: draft.level,
        paused: draft.paused,
        maxFilesChanged: limits.files!,
        maxLinesChanged: limits.lines!,
        maxParallelExecutions: limits.parallel!,
        protectedPaths: patternsOf(draft.protectedPaths),
        requireGreenCiForMerge: draft.requireGreenCiForMerge,
        allowBuildOnlyDelivery: draft.allowBuildOnlyDelivery,
        requireVisualReview: draft.requireVisualReview,
        conflictMode: draft.conflictMode,
      })
      setPolicy(result)
      setDraft(toDraft(result))
      setJustSaved(true)
    } catch (err) {
      setError(err instanceof Error ? err.message : t("automation.errSave"))
    } finally {
      setSaving(false)
    }
  }

  const automatic = draft ? draft.level !== "Manual" : false

  return (
    <PageContainer>
      <PageHeading
        eyebrow={t("automation.eyebrow")}
        title={t("automation.title")}
        description={t("automation.description")}
        actions={
          activeWorkspace ? (
            <Badge tone="gray">
              {t("automation.repository")}: {activeWorkspace.owner}/{activeWorkspace.repository}
            </Badge>
          ) : undefined
        }
      />

      {!activeWorkspaceId && (
        <Panel className="px-6 py-10 text-center text-[13px] text-muted-foreground">{t("automation.noWorkspace")}</Panel>
      )}

      {activeWorkspaceId && loading && !draft && (
        <div className="flex items-center gap-2 text-[13px] text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" /> {t("automation.loading")}
        </div>
      )}

      {error && (
        <Panel className="mb-4 flex items-start gap-2 border-danger/40 bg-danger-soft px-4 py-3 text-[13px] text-danger">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{error}</span>
        </Panel>
      )}

      {draft && (
        <div className="space-y-8">
          <section>
            <SectionHead title={t("automation.levelHeading")} />
            <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4" role="radiogroup" aria-label={t("automation.levelHeading")}>
              {AUTOMATION_LEVELS.map((level) => {
                const selected = draft.level === level
                return (
                  <button
                    key={level}
                    type="button"
                    role="radio"
                    aria-checked={selected}
                    onClick={() => set("level", level)}
                    className={cn(
                      "cursor-pointer rounded-[var(--radius-lg)] border p-4 text-left transition-colors",
                      selected
                        ? "border-primary bg-primary-soft"
                        : "border-border-strong bg-surface hover:bg-surface-3",
                    )}
                  >
                    <div className="flex items-center justify-between gap-2">
                      <span className="text-[13.5px] font-semibold text-foreground">{t(`automation.levels.${level}.name`)}</span>
                      {selected && <Check className="h-4 w-4 text-primary" />}
                    </div>
                    <div className="mt-1 text-[12.5px] text-muted-foreground">{t(`automation.levels.${level}.summary`)}</div>
                    <div className="mt-2.5 text-[11.5px] leading-relaxed text-subtle-foreground">
                      {t(`automation.levels.${level}.does`)}
                    </div>
                  </button>
                )
              })}
            </div>
            {automatic && policy?.activeSince && policy.level !== "Manual" && (
              <p className="mt-3 text-[12px] text-subtle-foreground">
                {t("automation.activeSince", {
                  date: new Date(policy.activeSince).toLocaleString(i18n.language),
                })}
              </p>
            )}
          </section>

          {automatic && (
            <>
              <section>
                <SectionHead title={t("automation.safetyHeading")} />
                <Panel className="p-5">
                  <div className="mb-4 flex items-start gap-2.5 text-[12.5px] leading-relaxed text-muted-foreground">
                    <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
                    <p>{t("automation.safetyIntro")}</p>
                  </div>
                  <div className="grid gap-4 md:grid-cols-3">
                    <Field label={t("automation.limits.maxFiles")} hint={t("automation.limits.maxFilesHint")}>
                      <input
                        className={cn(fieldClass, limits?.files === null && "border-danger")}
                        inputMode="numeric"
                        value={draft.maxFilesChanged}
                        onChange={(e) => set("maxFilesChanged", e.target.value)}
                      />
                    </Field>
                    <Field label={t("automation.limits.maxLines")} hint={t("automation.limits.maxLinesHint")}>
                      <input
                        className={cn(fieldClass, limits?.lines === null && "border-danger")}
                        inputMode="numeric"
                        value={draft.maxLinesChanged}
                        onChange={(e) => set("maxLinesChanged", e.target.value)}
                      />
                    </Field>
                    <Field label={t("automation.limits.maxParallel")} hint={t("automation.limits.maxParallelHint")}>
                      <input
                        className={cn(fieldClass, limits?.parallel === null && "border-danger")}
                        inputMode="numeric"
                        value={draft.maxParallelExecutions}
                        onChange={(e) => set("maxParallelExecutions", e.target.value)}
                      />
                    </Field>
                  </div>
                  {!valid && <p className="mt-3 text-[12px] text-danger">{t("automation.errRange")}</p>}

                  <div className="mt-5">
                    <Field label={t("automation.protectedHeading")} hint={t("automation.protectedHint")}>
                      <textarea
                        className={cn(fieldClass, "h-32 resize-y py-2 font-mono text-[12px]")}
                        spellCheck={false}
                        value={draft.protectedPaths}
                        onChange={(e) => set("protectedPaths", e.target.value)}
                      />
                    </Field>
                  </div>


                  {(draft.level === "AutoPr" || draft.level === "FullAuto") && (
                    <div className="mt-5 space-y-3">
                      <label className="flex cursor-pointer items-start gap-2 text-[13px] text-foreground">
                        <input
                          type="checkbox"
                          checked={draft.requireVisualReview}
                          onChange={(e) => set("requireVisualReview", e.target.checked)}
                          className="mt-0.5 h-4 w-4 rounded border-border-strong accent-primary"
                        />
                        <span>
                          {t("automation.requireVisual.label")}
                          <span className="mt-0.5 block text-[11.5px] text-subtle-foreground">{t("automation.requireVisual.hint")}</span>
                        </span>
                      </label>
                      <label className="flex cursor-pointer items-start gap-2 text-[13px] text-foreground">
                        <input
                          type="checkbox"
                          checked={draft.allowBuildOnlyDelivery}
                          onChange={(e) => set("allowBuildOnlyDelivery", e.target.checked)}
                          className="mt-0.5 h-4 w-4 rounded border-border-strong accent-primary"
                        />
                        <span>
                          {t("automation.buildOnly.label")}
                          <span className="mt-0.5 block text-[11.5px] text-subtle-foreground">{t("automation.buildOnly.hint")}</span>
                        </span>
                      </label>
                    </div>
                  )}
                  {draft.level === "FullAuto" && (
                    <label className="mt-5 flex cursor-pointer items-start gap-2 text-[13px] text-foreground">
                      <input
                        type="checkbox"
                        checked={draft.requireGreenCiForMerge}
                        onChange={(e) => set("requireGreenCiForMerge", e.target.checked)}
                        className="mt-0.5 h-4 w-4 rounded border-border-strong accent-primary"
                      />
                      <span>
                        {t("automation.requireCi.label")}
                        <span className="mt-0.5 block text-[11.5px] text-subtle-foreground">{t("automation.requireCi.hint")}</span>
                      </span>
                    </label>
                  )}
                </Panel>
              </section>

              <section>
                <SectionHead title={t("automation.conflict.heading")} />
                <p className="-mt-1 mb-3 max-w-[760px] text-[12.5px] leading-relaxed text-muted-foreground">{t("automation.conflict.intro")}</p>
                <div className="grid gap-3 md:grid-cols-3" role="radiogroup" aria-label={t("automation.conflict.heading")}>
                  {CONFLICT_MODES.map((mode) => {
                    const selected = draft.conflictMode === mode
                    return (
                      <button
                        key={mode}
                        type="button"
                        role="radio"
                        aria-checked={selected}
                        onClick={() => set("conflictMode", mode)}
                        className={cn(
                          "cursor-pointer rounded-[var(--radius-lg)] border p-4 text-left transition-colors",
                          selected ? "border-primary bg-primary-soft" : "border-border-strong bg-surface hover:bg-surface-3",
                        )}
                      >
                        <div className="flex items-center justify-between gap-2">
                          <span className="text-[13.5px] font-semibold text-foreground">{t(`automation.conflict.modes.${mode}.name`)}</span>
                          <span className="flex items-center gap-1.5">
                            {mode === "Balanced" && <Badge tone="blue">{t("automation.conflict.recommended")}</Badge>}
                            {selected && <Check className="h-4 w-4 text-primary" />}
                          </span>
                        </div>
                        <div className="mt-1 text-[12.5px] text-muted-foreground">{t(`automation.conflict.modes.${mode}.summary`)}</div>
                        <div className="mt-2.5 text-[11.5px] leading-relaxed text-subtle-foreground">
                          {t(`automation.conflict.modes.${mode}.does`)}
                        </div>
                      </button>
                    )
                  })}
                </div>
                {draft.conflictMode !== "Careful" && (
                  <p className="mt-3 max-w-[760px] text-[12px] leading-relaxed text-subtle-foreground">{t("automation.conflict.note")}</p>
                )}
              </section>

              <section>
                <Panel className={cn("flex items-start justify-between gap-4 p-5", draft.paused && "border-warning/50 bg-warning-soft")}>
                  <div className="flex items-start gap-2.5">
                    <PauseCircle className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
                    <div>
                      <div className="text-[13px] font-semibold text-foreground">{t("automation.paused.title")}</div>
                      <div className="mt-0.5 text-[12px] text-muted-foreground">
                        {draft.paused ? t("automation.paused.notice") : t("automation.paused.body")}
                      </div>
                    </div>
                  </div>
                  <input
                    type="checkbox"
                    role="switch"
                    aria-label={t("automation.paused.title")}
                    checked={draft.paused}
                    onChange={(e) => set("paused", e.target.checked)}
                    className="mt-1 h-4 w-4 shrink-0 cursor-pointer rounded border-border-strong accent-primary"
                  />
                </Panel>
              </section>
            </>
          )}

          <div className="flex items-center gap-3">
            <Button variant="primary" onClick={() => void save()} disabled={!dirty || !valid || saving}>
              {saving ? t("automation.saving") : t("automation.save")}
            </Button>
            {justSaved && !dirty && (
              <span className="flex items-center gap-1 text-[12.5px] text-success">
                <Check className="h-3.5 w-3.5" /> {t("automation.saved")}
              </span>
            )}
          </div>
        </div>
      )}
    </PageContainer>
  )
}
