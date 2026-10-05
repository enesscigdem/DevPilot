import { useMemo, useState } from "react"
import { useTranslation } from "react-i18next"
import { AlertTriangle, ExternalLink, ImageOff } from "lucide-react"
import { Badge, Panel } from "@/components/ui/primitives"
import { executionVisualImageUrl } from "@/api"
import { cn } from "@/lib/utils"
import type { VisualCaptureManifest, VisualShot } from "@/types"

interface Props {
  executionId: string
  manifest: VisualCaptureManifest
  acknowledged: boolean
  onAcknowledgedChange: (value: boolean) => void
  /** Hides the checkbox once the review is decided. */
  decided?: boolean
}

/** Before/after screenshots of a UI change, with the confirmation the reviewer gives before approving. */
export function VisualReviewPanel({ executionId, manifest, acknowledged, onAcknowledgedChange, decided }: Props) {
  const { t } = useTranslation()
  const shots = useMemo(() => manifest.shots ?? [], [manifest.shots])
  const [viewport, setViewport] = useState<string>(shots[0]?.viewport ?? "desktop")
  const shot: VisualShot | undefined = useMemo(
    () => shots.find((s) => s.viewport === viewport) ?? shots[0],
    [shots, viewport],
  )
  const cacheKey = manifest.capturedAtUtc

  const hasImages = shots.length > 0
  const showAcknowledge = manifest.requiresReview && !decided

  return (
    <Panel className="p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-[13px] font-semibold text-foreground">{t("review.visual.title")}</div>
          <div className="text-[12px] text-muted-foreground">{t("review.visual.subtitle")}</div>
        </div>
        {hasImages && shots.length > 1 && (
          <div role="tablist" className="inline-flex rounded-[var(--radius-md)] border border-border bg-surface-2 p-0.5">
            {shots.map((s) => (
              <button
                key={s.viewport}
                role="tab"
                aria-selected={s.viewport === shot?.viewport}
                onClick={() => setViewport(s.viewport)}
                className={cn(
                  "cursor-pointer rounded-[var(--radius-sm)] px-2.5 py-1 text-[12px] font-medium transition-colors",
                  s.viewport === shot?.viewport
                    ? "bg-surface text-foreground shadow-[var(--shadow-sm)]"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {t(`review.visual.${s.viewport}`, { defaultValue: s.viewport })}
              </button>
            ))}
          </div>
        )}
      </div>

      {!hasImages && (
        <div className="mt-3 flex items-start gap-2 rounded-[var(--radius-md)] border border-amber-500/30 bg-amber-500/10 p-3 text-[12px] text-amber-700 dark:text-amber-400">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <div>
            <div className="font-medium">{t("review.visual.unavailable")}</div>
            {manifest.reason && <div className="mt-0.5 opacity-90">{manifest.reason}</div>}
            {manifest.requiresReview && <div className="mt-1">{t("review.visual.unavailableHint")}</div>}
          </div>
        </div>
      )}

      {hasImages && shot && (
        <>
          <div className={cn("mt-3 grid gap-3", shot.beforeFile ? "md:grid-cols-2" : "grid-cols-1")}>
            {shot.beforeFile && (
              <ShotImage
                label={t("review.visual.before")}
                tone="neutral"
                url={executionVisualImageUrl(executionId, shot.beforeFile, cacheKey)}
                openLabel={t("review.visual.openImage")}
              />
            )}
            {shot.afterFile ? (
              <ShotImage
                label={t("review.visual.after")}
                tone="green"
                url={executionVisualImageUrl(executionId, shot.afterFile, cacheKey)}
                openLabel={t("review.visual.openImage")}
              />
            ) : (
              <div className="flex items-center justify-center gap-2 rounded-[var(--radius-md)] border border-dashed border-border p-6 text-[12px] text-muted-foreground">
                <ImageOff className="h-4 w-4" />
                {t("review.visual.unavailable")}
              </div>
            )}
          </div>
          {(!shot.beforeFile || manifest.reason) && (
            <div className="mt-2 text-[12px] text-muted-foreground">
              {!shot.beforeFile && !manifest.reason ? t("review.visual.noBaseline") : manifest.reason}
            </div>
          )}
        </>
      )}

      {showAcknowledge && (
        <label className="mt-3 flex cursor-pointer items-start gap-2 rounded-[var(--radius-md)] border border-border bg-surface-2 p-3 text-[13px] text-foreground">
          <input
            type="checkbox"
            checked={acknowledged}
            onChange={(event) => onAcknowledgedChange(event.target.checked)}
            className="mt-0.5 h-4 w-4 cursor-pointer accent-[var(--accent)]"
          />
          <span>{t("review.visual.acknowledge")}</span>
        </label>
      )}
    </Panel>
  )
}

function ShotImage({
  label,
  tone,
  url,
  openLabel,
}: {
  label: string
  tone: "neutral" | "green"
  url: string
  openLabel: string
}) {
  return (
    <figure className="min-w-0">
      <figcaption className="mb-1.5 flex items-center justify-between">
        <Badge tone={tone}>{label}</Badge>
        <a
          href={url}
          target="_blank"
          rel="noreferrer"
          className="inline-flex cursor-pointer items-center gap-1 text-[11.5px] text-muted-foreground hover:text-foreground"
        >
          {openLabel}
          <ExternalLink className="h-3 w-3" />
        </a>
      </figcaption>
      <a href={url} target="_blank" rel="noreferrer" className="block cursor-zoom-in">
        <img
          src={url}
          alt={label}
          loading="lazy"
          className="w-full rounded-[var(--radius-md)] border border-border bg-surface-2"
        />
      </a>
    </figure>
  )
}
