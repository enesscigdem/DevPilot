import { useTranslation } from "react-i18next"
import { Check, Loader2, Minus, X } from "lucide-react"
import { formatDuration } from "@/lib/duration"
import { cn } from "@/lib/utils"
import type { ExecutionRevision } from "@/types"

export type RunKey = "initial" | number

export interface RunOption {
  key: RunKey
  state: ExecutionRevision["state"] | "Initial"
  durationMs?: number | null
}

interface RunSelectorProps {
  runs: RunOption[]
  value: RunKey
  onChange: (key: RunKey) => void
}

function StateMark({ state }: { state: RunOption["state"] }) {
  if (state === "Running") return <Loader2 className="h-3 w-3 animate-spin text-primary" />
  if (state === "Failed") return <X className="h-3 w-3 text-danger" />
  if (state === "Cancelled" || state === "NoChange") return <Minus className="h-3 w-3 text-muted-foreground" />
  return <Check className="h-3 w-3 text-success" />
}

/**
 * The first run and every requested fix as tabs. The running one is marked, so it is obvious which one is live,
 * and any earlier one is a single click away without mixing its results into the others.
 */
export function RunSelector({ runs, value, onChange }: RunSelectorProps) {
  const { t } = useTranslation()
  return (
    <div className="border-b border-border bg-canvas">
      <div
        role="tablist"
        aria-label={t("revision.run.label")}
        className="mx-auto flex max-w-[1500px] items-center gap-1 overflow-x-auto px-6 py-2"
      >
        {runs.map((run) => {
          const selected = run.key === value
          const label = run.key === "initial" ? t("revision.run.initial") : t("revision.number", { n: run.key })
          return (
            <button
              key={String(run.key)}
              type="button"
              role="tab"
              aria-selected={selected}
              onClick={() => onChange(run.key)}
              className={cn(
                "flex shrink-0 cursor-pointer items-center gap-2 rounded-[var(--radius-md)] border px-3 py-1.5 text-[12.5px] font-medium transition-colors",
                selected
                  ? "border-primary/40 bg-primary-soft text-foreground"
                  : "border-transparent text-muted-foreground hover:bg-surface-3 hover:text-foreground",
              )}
            >
              <StateMark state={run.state} />
              <span>{label}</span>
              {run.state === "Running" && (
                <span className="rounded-full bg-primary px-1.5 py-px font-mono text-[9.5px] uppercase tracking-wide text-primary-foreground">
                  {t("revision.run.live")}
                </span>
              )}
              {run.state !== "Running" && run.durationMs != null && (
                <span className="font-mono text-[10.5px] text-subtle-foreground">{formatDuration(run.durationMs)}</span>
              )}
            </button>
          )
        })}
      </div>
    </div>
  )
}
