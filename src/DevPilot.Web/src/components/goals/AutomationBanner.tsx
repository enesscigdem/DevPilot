import { useCallback, useEffect, useState } from "react"
import { Link } from "react-router-dom"
import { AlertCircle, Loader2, PauseCircle, Zap } from "lucide-react"
import { useTranslation } from "react-i18next"
import { getAutomationPolicy, updateAutomationPolicy } from "@/api"
import { Button, Panel } from "@/components/ui/primitives"
import type { AutomationPolicy } from "@/types"

/**
 * Says, before a goal starts, what will happen to the plans: with automation off they wait for a person, so the
 * screen offers to switch it on in one click instead of sending the person to another page.
 */
export function AutomationBanner({ workspaceId }: { workspaceId: string }) {
  const { t } = useTranslation()
  const [policy, setPolicy] = useState<AutomationPolicy | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    getAutomationPolicy(workspaceId)
      .then(setPolicy)
      .catch(() => setPolicy(null))
  }, [workspaceId])

  useEffect(() => {
    load()
  }, [load])

  if (!policy) return null

  const turnOn = async () => {
    setBusy(true)
    setError(null)
    try {
      // Only the level changes; the limits and protected paths the person already set are sent back as they were.
      setPolicy(
        await updateAutomationPolicy(workspaceId, {
          level: "SemiAuto",
          paused: false,
          maxFilesChanged: policy.maxFilesChanged,
          maxLinesChanged: policy.maxLinesChanged,
          maxParallelExecutions: policy.maxParallelExecutions,
          protectedPaths: policy.protectedPaths,
          requireGreenCiForMerge: policy.requireGreenCiForMerge,
          allowBuildOnlyDelivery: policy.allowBuildOnlyDelivery,
          requireVisualReview: policy.requireVisualReview,
          conflictMode: policy.conflictMode,
        }),
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : t("goals.automation.errTurnOn"))
    } finally {
      setBusy(false)
    }
  }

  if (policy.level === "Manual") {
    return (
      <Panel className="flex flex-wrap items-center gap-3 border-accent-line/60 bg-accent-soft/60 px-4 py-3">
        <Zap className="h-4 w-4 shrink-0 text-accent" />
        <div className="min-w-[240px] flex-1">
          <div className="text-[13px] font-semibold text-foreground">{t("goals.automation.manualTitle")}</div>
          <div className="text-[12.5px] text-muted-foreground">{t("goals.automation.manualBody")}</div>
          {error && (
            <div className="mt-1 flex items-center gap-1 text-[12px] font-medium text-danger">
              <AlertCircle className="h-3.5 w-3.5" /> {error}
            </div>
          )}
        </div>
        <Button variant="primary" size="sm" onClick={() => void turnOn()} disabled={busy}>
          {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : t("goals.automation.turnOn")}
        </Button>
      </Panel>
    )
  }

  if (policy.paused) {
    return (
      <Panel className="flex items-center gap-3 border-accent-line/60 bg-warning-soft px-4 py-3">
        <PauseCircle className="h-4 w-4 shrink-0 text-warning" />
        <div className="flex-1">
          <div className="text-[13px] font-semibold text-foreground">{t("goals.automation.pausedTitle")}</div>
          <div className="text-[12.5px] text-muted-foreground">{t("goals.automation.pausedBody")}</div>
        </div>
        <Link to="/automation" className="text-[12.5px] font-medium text-primary hover:underline">
          {t("goals.automation.settings")}
        </Link>
      </Panel>
    )
  }

  return (
    <div className="flex items-center gap-2 text-[12.5px] text-muted-foreground">
      <Zap className="h-3.5 w-3.5 text-primary" />
      {t("goals.automation.onNote", { level: t(`automation.levels.${policy.level}.name`) })}
      <Link to="/automation" className="font-medium text-primary hover:underline">
        {t("goals.automation.settings")}
      </Link>
    </div>
  )
}
